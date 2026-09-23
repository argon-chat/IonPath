namespace IonTestClientServer.Streaming;

using System.Collections.Concurrent;
using System.Diagnostics;
using ion.runtime;
using ion.runtime.client;
using ion.runtime.network;
using TestContracts;

/// <summary>
/// The network misbehaving, and peers that are slow rather than dead. This is where a stream
/// either earns the word "reliable" or does not: a partition must be noticed on both sides within
/// the timeouts, a blip shorter than them must be survived, and a client that is merely slow —
/// while still pinging — must never be mistaken for a dead one.
/// </summary>
[TestFixture(IonStreamTransportKind.WebSocket)]
[TestFixture(IonStreamTransportKind.WebTransport)]
[Parallelizable(ParallelScope.None)]
public class StreamResilienceTests(IonStreamTransportKind transport)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    /// <summary>Both timeouts are 1.5 s (LabHost.FastTimings and LabHost.Client); detection must land inside this.</summary>
    private static readonly TimeSpan DetectionBudget = TimeSpan.FromSeconds(5);

    private LabHost host = null!;
    private FaultyProxy proxy = null!;

    [SetUp]
    public async Task SetUp()
    {
        IonWsClient.ResetTransportHealth();
        host = await LabHost.StartAsync();
        proxy = FaultyProxy.Start(host.Port);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
        {
            foreach (var c in host.Probe.All)
                TestContext.Out.WriteLine($"server saw {c.User}: {c.Context?.Disconnect} [{c.Context?.Disconnect?.Exception?.GetType().Name}: {c.Context?.Disconnect?.Exception?.Message}]");
            foreach (var line in host.Log.Lines(Microsoft.Extensions.Logging.LogLevel.Debug).TakeLast(40))
                TestContext.Out.WriteLine(line);
        }

        await proxy.DisposeAsync();
        await host.DisposeAsync();
    }

    private IStreamLab Lab(string user = "alice", Action<IonStreamClientOptions>? configure = null)
        => host.LabVia(proxy.BaseAddress, user, configure, transport);

    [Test]
    public async Task A_partition_is_noticed_on_both_sides_within_the_timeouts()
    {
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in Lab().Listen("t"))
            {
            }
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);

        var sw = Stopwatch.StartNew();
        proxy.Freeze();

        var client = Assert.ThrowsAsync<IonStreamDisconnectedException>(async () => await consumer.WaitAsync(Wait));
        var clientNoticed = sw.Elapsed;
        var server = await c.Disconnected.Task.WaitAsync(Wait);
        var serverNoticed = sw.Elapsed;

        TestContext.Out.WriteLine($"client noticed after {clientNoticed.TotalMilliseconds:F0} ms, server after {serverNoticed.TotalMilliseconds:F0} ms");
        Assert.Multiple(() =>
        {
            Assert.That(client!.Reason, Is.EqualTo(IonDisconnectReason.Timeout));
            Assert.That(server.Reason, Is.EqualTo(IonDisconnectReason.Timeout));
            Assert.That(server.Exception, Is.TypeOf<TimeoutException>());
            Assert.That(clientNoticed, Is.LessThan(DetectionBudget));
            Assert.That(serverNoticed, Is.LessThan(DetectionBudget));
            Assert.That(c.DisconnectCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task A_partition_in_the_middle_of_a_flood_does_not_hang_the_server()
    {
        // The stream method yields as fast as the socket takes it; when the partition hits, a send
        // is stuck in a full TCP window. The client's silence must still end it.
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in Lab().Count(0, int.MaxValue, 0))
            {
            }
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);
        await Task.Delay(200);

        var sw = Stopwatch.StartNew();
        proxy.Freeze();

        Assert.CatchAsync<IonStreamDisconnectedException>(async () => await consumer.WaitAsync(Wait));
        var server = await c.Disconnected.Task.WaitAsync(Wait);

        Assert.That(server.Reason, Is.EqualTo(IonDisconnectReason.Timeout));
        Assert.That(sw.Elapsed, Is.LessThan(DetectionBudget), "the server noticed despite its stuck send");
        Assert.That(c.StreamStoppedBeforeDisconnect, Is.True);
        await Eventually.TrueAsync(() => host.Connections.Count == 0, Wait, "registry drained");
    }

    [Test]
    public async Task A_blip_shorter_than_the_timeouts_is_survived()
    {
        var received = new ConcurrentQueue<LabEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var e in Lab().Listen("blip"))
            {
                received.Enqueue(e);
                if (received.Count == 2)
                    break;
            }
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);

        await host.Connections.Group("blip").SendAsync(new LabEvent(1, "blip", "before"));
        await Eventually.TrueAsync(() => received.Count == 1, Wait, "the first push");

        proxy.Freeze();
        await host.Connections.Group("blip").SendAsync(new LabEvent(2, "blip", "during"));
        await Task.Delay(TimeSpan.FromMilliseconds(600)); // well under both 1.5 s timeouts
        proxy.Thaw();

        await consumer.WaitAsync(Wait);
        Assert.That(received.Select(e => e.body), Is.EqualTo(new[] { "before", "during" }), "the push held back by the blip arrives");

        var info = await c.Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ClientClosed), "it ended because the client broke off, not because of the blip");
    }

    [Test]
    public async Task A_reset_connection_is_a_lost_transport_on_both_sides()
    {
        if (transport == IonStreamTransportKind.WebTransport)
            Assert.Ignore("QUIC has no RST to forge from the outside; WebTransport death is covered by the partition tests.");

        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in Lab().Listen("t"))
            {
            }
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);

        proxy.Reset();

        var client = Assert.ThrowsAsync<IonStreamDisconnectedException>(async () => await consumer.WaitAsync(Wait));
        var server = await c.Disconnected.Task.WaitAsync(Wait);
        Assert.That(client!.Reason, Is.EqualTo(IonDisconnectReason.TransportLost));
        Assert.That(server.Reason, Is.EqualTo(IonDisconnectReason.TransportLost));
        Assert.That(server.IsGraceful, Is.False);
    }

    [Test]
    public async Task A_slow_consumer_that_keeps_pinging_is_never_dropped()
    {
        // The consumer stalls for longer than both timeouts, twice, while the server has all 20000
        // items ready: the client stops reading, the server's sends stall on a full window — and
        // nobody times anybody out, because the heartbeats keep flowing both ways.
        const int count = 20_000;
        var last = -1;
        await foreach (var i in host.Lab("slow", null, transport).Count(0, count, 0))
        {
            Assert.That(i, Is.EqualTo(last + 1), "in order, nothing lost");
            last = i;
            if (i is 0 or 10_000)
                await Task.Delay(TimeSpan.FromSeconds(2.5));
        }

        Assert.That(last, Is.EqualTo(count - 1));
        var c = await host.Probe.SingleAsync();
        Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.Completed));
    }

    [Test]
    public async Task A_stream_method_slow_to_read_its_input_is_not_mistaken_for_a_silent_client()
    {
        await using var slow = await LabHost.StartAsync(o => o.InputQueueCapacity = 2);
        slow.Probe.EchoDelay = TimeSpan.FromMilliseconds(300); // 12 items ≈ 3.6 s, over twice the client timeout

        var input = Enumerable.Range(0, 12).Select(i => $"x{i}").ToArray();
        var output = new List<string>();
        await foreach (var s in slow.Lab("reader", null, transport).Echo(ToAsync(input)))
            output.Add(s);

        Assert.That(output, Is.EqualTo(input.Select(s => s.ToUpperInvariant())));
        var c = await slow.Probe.SingleAsync();
        Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.Completed));

        static async IAsyncEnumerable<string> ToAsync(string[] values)
        {
            foreach (var v in values)
            {
                yield return v;
                await Task.Yield();
            }
        }
    }

    [Test]
    public async Task Stopping_the_host_is_bounded_even_by_a_client_that_stopped_reading()
    {
        var gate = new SemaphoreSlim(0);
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in host.Lab("stuck", null, transport).Listen("flood"))
                    await gate.WaitAsync();
            }
            catch (Exception)
            {
            }
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);

        var body = new string('z', 4096);
        for (var i = 0; i < 400; i++)
            await host.Connections.Group("flood").SendAsync(new LabEvent(i, "flood", body));

        var sw = Stopwatch.StartNew();
        await host.StopAsync();

        // CloseTimeout (2 s) + StreamStopTimeout (2 s), plus Kestrel's own wind-down.
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(9)), "shutdown did not wait on the stuck client");
        Assert.That(c.Disconnected.Task.IsCompleted, Is.True, "its disconnect hook ran before the host finished stopping");
        Assert.That((await c.Disconnected.Task).Reason, Is.AnyOf(IonDisconnectReason.ServerShutdown, IonDisconnectReason.SlowConsumer));

        gate.Release(1000);
        await consumer.WaitAsync(Wait);
    }
}
