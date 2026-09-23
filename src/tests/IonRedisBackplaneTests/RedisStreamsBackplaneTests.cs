namespace IonRedisBackplaneTests;

using System.Diagnostics;
using System.Formats.Cbor;
using ion.runtime.network;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

/// <summary>
/// <see cref="RedisStreamsBackplane"/> against a real Dragonfly, several instances standing in for
/// server nodes. Each test has a stream of its own.
/// </summary>
[TestFixture]
public sealed class RedisStreamsBackplaneTests
{
    /// <summary>Long enough for a stray delivery to show up: a few read cycles of the nodes' 500 ms BLOCK.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.5);

    private readonly List<Node> nodes = [];

    [TearDown]
    public async Task TearDownAsync()
    {
        foreach (var node in nodes)
            await node.DisposeAsync();
        nodes.Clear();
    }

    private Node NewNode(string key, Action<IonRedisBackplaneOptions>? configure = null)
    {
        var node = new Node(key, configure);
        nodes.Add(node);
        return node;
    }

    private async Task<Node> StartNodeAsync(string key, Action<IonRedisBackplaneOptions>? configure = null)
    {
        var node = NewNode(key, configure);
        await node.StartAsync();
        return node;
    }

    private static IDatabase Db => Dragonfly.Admin.GetDatabase();

    [Test]
    public async Task A_message_reaches_every_other_node_once_and_never_its_origin()
    {
        var key = Node.NewStreamKey();
        var a = await StartNodeAsync(key);
        var b = await StartNodeAsync(key);
        var c = await StartNodeAsync(key);

        var sent = new IonBackplaneMessage
        {
            OriginNodeId = a.Id,
            Command = IonBackplaneCommand.Close,
            TargetKind = IonBackplaneTargetKind.Connections,
            Targets = ["conn-1", "conn-2"],
            Excluded = ["conn-3"],
            Group = "lobby",
            PayloadType = "System.Int32",
            Payload = new byte[] { 1, 2, 3 },
            Reason = "maintenance",
            AllowReconnect = true
        };
        await a.Backplane.PublishAsync(sent);

        await b.WaitForCountAsync(1);
        await c.WaitForCountAsync(1);
        await Task.Delay(Quiet);

        Assert.Multiple(() =>
        {
            Assert.That(a.Received, Is.Empty, "the origin got its own message back");
            Assert.That(b.Received, Has.Length.EqualTo(1));
            Assert.That(c.Received, Has.Length.EqualTo(1));
        });
        AssertSameMessage(b.Received[0], sent);
        AssertSameMessage(c.Received[0], sent);
    }

    // XREAD answers differently per protocol: an array of [key, entries] pairs on RESP2, a map on RESP3.
    [TestCase(RedisProtocol.Resp3)]
    [TestCase(RedisProtocol.Resp2)]
    public async Task One_publishers_messages_arrive_in_publish_order(RedisProtocol protocol)
    {
        const int count = 1000;
        var key = Node.NewStreamKey();
        Action<IonRedisBackplaneOptions> speak = o => o.ConfigurationFactory = () =>
        {
            var config = ConfigurationOptions.Parse(Dragonfly.Configuration);
            config.Protocol = protocol;
            return config;
        };
        var a = await StartNodeAsync(key, speak);
        var b = await StartNodeAsync(key, speak);
        var c = await StartNodeAsync(key, speak);

        // An asynchronous handler now and then: the next message must still wait for it.
        b.AfterReceive = m => Node.SequenceOf(m) % 50 == 0 ? new ValueTask(Task.Delay(2)) : default;

        await a.PublishManyAsync(0, count);

        await b.WaitForCountAsync(count);
        await c.WaitForCountAsync(count);
        await Task.Delay(Quiet);

        var expected = Enumerable.Range(0, count).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(b.Sequence(), Is.EqualTo(expected));
            Assert.That(c.Sequence(), Is.EqualTo(expected));
            Assert.That(a.Received, Is.Empty);
            Assert.That(b.OverlapSeen, Is.False, "the handler ran concurrently with itself");
        });
    }

    [Test]
    public async Task Concurrent_publishers_reach_every_other_node_exactly_once()
    {
        const int perNode = 300;
        var key = Node.NewStreamKey();
        Node[] all = [await StartNodeAsync(key), await StartNodeAsync(key), await StartNodeAsync(key)];

        await Task.WhenAll(all.Select(node => Task.Run(async () =>
        {
            for (var i = 0; i < perNode; i += 30)
                await node.PublishManyAsync(i, 30);
        })));

        foreach (var node in all)
            await node.WaitForCountAsync(perNode * (all.Length - 1));
        await Task.Delay(Quiet);

        var expected = Enumerable.Range(0, perNode).ToArray();
        Assert.Multiple(() =>
        {
            foreach (var node in all)
            {
                Assert.That(node.Received, Has.Length.EqualTo(perNode * (all.Length - 1)), node.Id);
                Assert.That(node.Sequence(node.Id), Is.Empty, $"{node.Id} got its own messages back");
                foreach (var other in all.Where(o => o != node))
                    Assert.That(node.Sequence(other.Id), Is.EqualTo(expected), $"{node.Id} from {other.Id}");
            }
        });
    }

    [Test]
    public async Task Nothing_published_right_after_start_is_missed_and_nothing_older_is_replayed()
    {
        for (var round = 0; round < 25; round++)
        {
            var key = Node.NewStreamKey();

            // Odd rounds start on a stream that does not exist yet, even ones on one with history.
            if (round % 2 == 0)
                for (var old = 0; old < 3; old++)
                    await Db.StreamAddAsync(key, "m", Node.Message("old-node", -1 - old).Encode());

            await using var node = new Node(key);
            await node.StartAsync();

            // No delay at all: StartAsync returning is the whole promise.
            await Db.StreamAddAsync(key, "m", Node.Message("publisher", round).Encode());

            await node.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
            Assert.That(node.Received[0].OriginNodeId, Is.EqualTo("publisher"), $"round {round}: an entry from before the start was replayed");
            Assert.That(Node.SequenceOf(node.Received[0]), Is.EqualTo(round));
        }
    }

    [Test]
    public async Task Stop_ends_delivery_promptly_is_idempotent_and_a_restart_begins_at_the_end()
    {
        var key = Node.NewStreamKey();
        var a = await StartNodeAsync(key);
        var b = await StartNodeAsync(key, o => o.BlockTimeout = TimeSpan.FromSeconds(1));

        await a.PublishAsync(0);
        await b.WaitForCountAsync(1);
        var readerName = b.Backplane.ReaderClientName!;
        Assert.That(await Dragonfly.ClientIdsAsync(readerName), Is.Not.Empty, "the reader connection is not in CLIENT LIST");

        // The reader is now parked in a blocking XREAD; stopping must not wait it out.
        await Task.Delay(300);
        var watch = Stopwatch.StartNew();
        await b.StopAsync();
        watch.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(watch.Elapsed, Is.LessThan(b.Options.BlockTimeout + TimeSpan.FromSeconds(1)));
            Assert.That(b.Backplane.IsRunning, Is.False);
            Assert.That(b.Backplane.ReaderClientName, Is.Null);
        });

        await a.PublishManyAsync(1, 10);
        await b.AssertQuietAsync(Quiet, 1);

        // Again, and concurrently: no-ops.
        await Task.WhenAll(b.StopAsync(), b.StopAsync());
        await b.StopAsync();

        await Node.EventuallyAsync(async () => (await Dragonfly.ClientIdsAsync(readerName)).Count == 0,
            "the reader connection to close", TimeSpan.FromSeconds(10));

        // A restart reads from the stream's end: the ten published while stopped stay missed.
        await b.StartAsync();
        Assert.ThrowsAsync<InvalidOperationException>(() => b.StartAsync());
        await a.PublishAsync(11);
        await b.WaitForCountAsync(2);
        await b.AssertQuietAsync(Quiet, 2);
        Assert.That(b.Sequence(), Is.EqualTo(new[] { 0, 11 }));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(256 * 1024)]
    [TestCase(1024 * 1024)]
    public async Task Payloads_round_trip_byte_for_byte(int size)
    {
        var key = Node.NewStreamKey();
        var a = await StartNodeAsync(key);
        var b = await StartNodeAsync(key);

        var payload = new byte[size];
        new Random(size).NextBytes(payload);
        var sent = Node.Message(a.Id, 7, payload);

        await a.Backplane.PublishAsync(sent);
        await b.WaitForCountAsync(1);

        Assert.That(b.Received[0].Payload.Length, Is.EqualTo(size));
        Assert.That(b.Received[0].Payload.Span.SequenceEqual(payload), "the payload changed on the way");
        AssertSameMessage(b.Received[0], sent);
    }

    [Test]
    public async Task A_throwing_handler_does_not_stop_later_deliveries()
    {
        const int count = 30;
        var key = Node.NewStreamKey();
        var a = await StartNodeAsync(key);
        var b = NewNode(key);

        var seen = new List<int>();
        await b.Backplane.StartAsync(b.Id, message =>
        {
            var sequence = Node.SequenceOf(message);
            lock (seen)
                seen.Add(sequence);

            return (sequence % 3) switch
            {
                0 => throw new InvalidOperationException($"synchronous failure on {sequence}"),
                1 => FailLaterAsync(sequence),
                _ => default
            };

            static async ValueTask FailLaterAsync(int sequence)
            {
                await Task.Yield();
                throw new InvalidOperationException($"asynchronous failure on {sequence}");
            }
        }, CancellationToken.None);

        await a.PublishManyAsync(0, count);
        await Node.EventuallyAsync(() => { lock (seen) return seen.Count >= count; }, "every message to reach the handler");

        // Still alive after all that.
        await a.PublishAsync(count);
        await Node.EventuallyAsync(() => { lock (seen) return seen.Count == count + 1; }, "the loop to survive the failures");

        lock (seen)
            Assert.That(seen, Is.EqualTo(Enumerable.Range(0, count + 1)));
        Assert.That(b.Log.Entries.Count(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException),
            Is.EqualTo(Enumerable.Range(0, count + 1).Count(s => s % 3 != 2)), "every handler failure is logged");
    }

    [Test]
    public async Task Entries_that_are_not_messages_are_logged_and_skipped()
    {
        var key = Node.NewStreamKey();
        var a = await StartNodeAsync(key);
        var b = await StartNodeAsync(key);

        // Not CBOR at all.
        await Db.StreamAddAsync(key, "m", new byte[] { 0xFF, 0x00, 0x13 });
        // CBOR, but not a message.
        await Db.StreamAddAsync(key, "m", new byte[] { 0x63, (byte)'a', (byte)'b', (byte)'c' });
        // No payload field.
        await Db.StreamAddAsync(key, "other", "value");
        // A message from a newer encoding: dropped quietly, as IonBackplaneMessage.Decode asks.
        var future = new CborWriter();
        future.WriteStartArray(2);
        future.WriteInt32(IonBackplaneMessage.CurrentVersion + 1);
        future.WriteTextString("future-node");
        future.WriteEndArray();
        await Db.StreamAddAsync(key, "m", future.Encode());

        await a.PublishAsync(1);
        await b.WaitForCountAsync(1);
        await b.AssertQuietAsync(Quiet, 1);

        Assert.Multiple(() =>
        {
            Assert.That(Node.SequenceOf(b.Received[0]), Is.EqualTo(1));
            Assert.That(b.Log.Entries.Count(e => e.Level == LogLevel.Error && e.Text.Contains("not a backplane message")), Is.EqualTo(2));
            Assert.That(b.Log.Entries.Count(e => e.Level == LogLevel.Warning && e.Text.Contains("no 'm' field")), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task A_reader_that_was_cut_off_catches_up_on_what_it_missed()
    {
        // B reaches the server through a proxy the test can cut for as long as it likes, which is
        // what proves the messages below were published while B was really unable to read.
        await using var proxy = new TcpProxy(Dragonfly.Host, Dragonfly.MappedPort);
        var key = Node.NewStreamKey();
        var b = await StartNodeAsync(key, o => o.ConfigurationFactory = () =>
        {
            var config = ConfigurationOptions.Parse($"127.0.0.1:{proxy.Port}");
            config.ReconnectRetryPolicy = new LinearRetry(200);
            return config;
        });
        var a = await StartNodeAsync(key);

        await a.PublishManyAsync(0, 5);
        await b.WaitForCountAsync(5);

        proxy.Cut();
        await Node.EventuallyAsync(() => b.Backplane.ReadFailureCount > 0, "the reader to notice its connection is gone");

        await a.PublishManyAsync(5, 50);
        await b.AssertQuietAsync(Quiet, 5);

        proxy.Restore();
        await b.WaitForCountAsync(55, TimeSpan.FromSeconds(60));
        await b.AssertQuietAsync(Quiet, 55);

        Assert.Multiple(() =>
        {
            Assert.That(b.Sequence(), Is.EqualTo(Enumerable.Range(0, 55)));
            Assert.That(b.Log.Entries.Any(e => e.Level == LogLevel.Information && e.Text.Contains("recovered")),
                "the recovery is logged");
        });

        // Before the proxy goes away with the method's scope, or B logs one more failure.
        await b.StopAsync();
    }

    [Test]
    public async Task A_reader_killed_mid_stream_loses_nothing()
    {
        const int count = 1000;
        var key = Node.NewStreamKey();
        var a = await StartNodeAsync(key);
        var b = await StartNodeAsync(key);

        var publishing = Task.Run(async () =>
        {
            for (var i = 0; i < count; i += 20)
            {
                await a.PublishManyAsync(i, 20);
                await Task.Delay(15);
            }
        });

        var kills = 0;
        while (!publishing.IsCompleted)
        {
            await Task.Delay(200);
            kills += await Dragonfly.KillClientsAsync(b.Backplane.ReaderClientName!);
        }
        await publishing;

        await b.WaitForCountAsync(count, TimeSpan.FromSeconds(60));
        await b.AssertQuietAsync(Quiet, count);

        Assert.Multiple(() =>
        {
            Assert.That(kills, Is.GreaterThan(0), "the test never killed the reader");
            Assert.That(b.Backplane.ReadFailureCount, Is.GreaterThan(0), "the kills went unnoticed");
            Assert.That(b.Sequence(), Is.EqualTo(Enumerable.Range(0, count)));
        });
    }

    [TestCase(100)]
    [TestCase(0)]
    public async Task MaxLength_keeps_the_stream_bounded(int maxLength)
    {
        const int count = 2000;
        var key = Node.NewStreamKey();
        var a = NewNode(key, o => o.MaxLength = maxLength);

        await a.PublishAsync(-1);
        await a.PublishManyAsync(0, count - 1);

        var length = await Db.StreamLengthAsync(key);
        if (maxLength > 0)
            // "~" lets Redis trim in whole radix-tree nodes (100 entries by default), never below the limit.
            Assert.That(length, Is.InRange(maxLength, maxLength + 100));
        else
            Assert.That(length, Is.EqualTo(count));
    }

    private static void AssertSameMessage(IonBackplaneMessage actual, IonBackplaneMessage expected)
        => Assert.Multiple(() =>
        {
            Assert.That(actual.OriginNodeId, Is.EqualTo(expected.OriginNodeId));
            Assert.That(actual.Command, Is.EqualTo(expected.Command));
            Assert.That(actual.TargetKind, Is.EqualTo(expected.TargetKind));
            Assert.That(actual.Targets, Is.EqualTo(expected.Targets));
            Assert.That(actual.Excluded, Is.EqualTo(expected.Excluded));
            Assert.That(actual.Group, Is.EqualTo(expected.Group));
            Assert.That(actual.PayloadType, Is.EqualTo(expected.PayloadType));
            Assert.That(actual.Payload.ToArray(), Is.EqualTo(expected.Payload.ToArray()));
            Assert.That(actual.Reason, Is.EqualTo(expected.Reason));
            Assert.That(actual.AllowReconnect, Is.EqualTo(expected.AllowReconnect));
        });
}
