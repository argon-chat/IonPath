namespace IonTestClientServer.Streaming;

using System.Formats.Cbor;
using System.Net.WebSockets;
using ion.runtime;
using ion.runtime.network;

/// <summary>
/// Resumable sessions on the wire, frame by frame, from a hand-driven WebSocket: what READY
/// announces, what is replayed after a RESUME and what is not, when an ACK is due, and what the
/// server does with a RESUME or an ACK that makes no sense — which a client with a bug, or a
/// stranger with a guessed token, will send.
/// </summary>
[Parallelizable(ParallelScope.None)]
public class StreamResumeProtocolTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string Resume = "?resume=1";

    private LabHost host = null!;

    [SetUp]
    public async Task SetUp() => host = await LabHost.StartAsync();

    [TearDown]
    public async Task TearDown()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            foreach (var line in host.Log.Lines(Microsoft.Extensions.Logging.LogLevel.Information).Where(l => l.Contains("Ion.Streams")).TakeLast(20))
                TestContext.Out.WriteLine(line);

        await host.DisposeAsync();
    }

    private static Task SendIntsAsync(RawStream raw, params int[] args) => raw.SendArgsAsync(w =>
    {
        foreach (var a in args)
            w.WriteInt32(a);
    }, args.Length);

    private static Task SendTopicAsync(RawStream raw, string topic) => raw.SendArgsAsync(w => w.WriteTextString(topic), 1);

    private static (string Id, CborReader Tail, int Size) ReadReady(byte[] payload)
    {
        var r = new CborReader(payload);
        var size = r.ReadStartArray()!.Value;
        var id = r.ReadTextString();
        r.ReadUInt64();
        r.ReadUInt64();
        return (id, r, size);
    }

    private static string ResumeTokenOf(byte[] readyPayload)
    {
        var (_, rest, size) = ReadReady(readyPayload);
        Assert.That(size, Is.EqualTo(6), "a resumable READY has six elements");
        return rest.ReadTextString();
    }

    private static byte[] Ack(long n)
    {
        var buffer = new byte[IonStreamProtocol.MaxAckFrameSize];
        return buffer[..IonStreamProtocol.WriteAck(buffer, n)];
    }

    private static int ReadInt(byte[] dataPayload) => new CborReader(dataPayload).ReadInt32();

    private static IonProtocolError ReadError(byte[] payload) => IonStreamProtocol.ReadError(payload);

    [Test]
    public async Task READY_carries_a_resume_token_only_when_asked_and_allowed()
    {
        await using (var plain = await RawStream.OpenAsync(host, "Listen"))
        {
            await SendTopicAsync(plain, "t");
            var (_, _, size) = ReadReady(await plain.ExpectAsync(IonStreamProtocol.OpReady));
            Assert.That(size, Is.EqualTo(3), "not asked");
        }

        await using (var asked = await RawStream.OpenAsync(host, "Listen", query: Resume))
        {
            await SendTopicAsync(asked, "t");
            var payload = await asked.ExpectAsync(IonStreamProtocol.OpReady);
            var (id, rest, size) = ReadReady(payload);
            Assert.That(size, Is.EqualTo(6));
            var token = rest.ReadTextString();
            var window = rest.ReadUInt64();
            var inputBudget = rest.ReadUInt64();
            Assert.Multiple(() =>
            {
                Assert.That(token, Has.Length.GreaterThanOrEqualTo(40), "256 bits of randomness");
                Assert.That(token, Is.Not.EqualTo(id), "never the connection id, which other code sees");
                Assert.That(window, Is.EqualTo(30_000UL));
                Assert.That(inputBudget, Is.EqualTo(1024UL * 1024), "the server's ResumeBufferSize");
            });
        }

        await using var off = await LabHost.StartAsync(o => o.ResumeWindow = TimeSpan.Zero);
        await using var refused = await RawStream.OpenAsync(off, "Listen", query: Resume);
        await SendTopicAsync(refused, "t");
        Assert.That(ReadReady(await refused.ExpectAsync(IonStreamProtocol.OpReady)).Size, Is.EqualTo(3), "not allowed");
    }

    [Test]
    public async Task A_resume_replays_exactly_what_was_not_received_and_nothing_before_it()
    {
        string token;
        await using (var first = await RawStream.OpenAsync(host, "Count", query: Resume))
        {
            await SendIntsAsync(first, 0, 10, 0);
            token = ResumeTokenOf(await first.ExpectAsync(IonStreamProtocol.OpReady));

            for (var i = 0; i < 10; i++)
                Assert.That(ReadInt(await first.ExpectAsync(IonStreamProtocol.OpData)), Is.EqualTo(i));
            await first.ExpectAsync(IonStreamProtocol.OpEnd);

            // Acknowledge four, claim six on resume: the transport "died" with frames 7..11 unread.
            await first.SendAsync(Ack(4));
        } // aborted: no goodbye

        var c = await host.Probe.SingleAsync();
        Assert.That(c.Disconnected.Task.IsCompleted, Is.False, "END is not delivered until acknowledged");

        await using var second = await RawStream.OpenAsync(host, "Count", query: Resume);
        await second.SendAsync(IonStreamProtocol.ResumeFrame(token, 6));

        var resumed = await second.ExpectAsync(IonStreamProtocol.OpResumed);
        Assert.That(IonStreamProtocol.ReadResumed(resumed), Is.Zero, "the server has none of the client's frames");

        for (var i = 6; i < 10; i++)
            Assert.That(ReadInt(await second.ExpectAsync(IonStreamProtocol.OpData)), Is.EqualTo(i), "the replay starts after what the client had");
        await second.ExpectAsync(IonStreamProtocol.OpEnd);

        await second.SendAsync(Ack(11));
        await second.ExpectCloseAsync(WebSocketCloseStatus.NormalClosure);
        await second.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None);

        var info = await c.Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.Completed));
        Assert.That(c.ConnectCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task The_server_says_how_much_input_it_has_so_the_client_resends_only_the_rest()
    {
        string token;
        await using (var first = await RawStream.OpenAsync(host, "Echo", query: Resume))
        {
            await first.SendArgsAsync(_ => { }, 0);
            token = ResumeTokenOf(await first.ExpectAsync(IonStreamProtocol.OpReady));

            foreach (var s in new[] { "a", "b", "c" })
                await first.SendAsync(DataFrame(s));
            for (var i = 0; i < 3; i++)
                await first.ExpectAsync(IonStreamProtocol.OpData);
        }

        await using var second = await RawStream.OpenAsync(host, "Echo", query: Resume);
        await second.SendAsync(IonStreamProtocol.ResumeFrame(token, 3));
        Assert.That(IonStreamProtocol.ReadResumed(await second.ExpectAsync(IonStreamProtocol.OpResumed)), Is.EqualTo(3),
            "all three inputs arrived before the transport died");

        await second.SendAsync(DataFrame("d"));
        var echo = await second.ExpectAsync(IonStreamProtocol.OpData);
        Assert.That(new CborReader(echo).ReadTextString(), Is.EqualTo("D"), "the session goes on where it was");

        static byte[] DataFrame(string s)
        {
            var w = new CborWriter();
            w.WriteStartArray(1);
            w.WriteTextString(s);
            w.WriteEndArray();
            return IonStreamProtocol.Frame(IonStreamProtocol.OpData, w);
        }
    }

    [Test]
    public async Task A_goodbye_is_held_until_it_is_acknowledged_and_resent_after_a_resume()
    {
        string token;
        await using (var first = await RawStream.OpenAsync(host, "Count", query: Resume))
        {
            await SendIntsAsync(first, 0, 3, 0);
            token = ResumeTokenOf(await first.ExpectAsync(IonStreamProtocol.OpReady));
            for (var i = 0; i < 3; i++)
                await first.ExpectAsync(IonStreamProtocol.OpData);
            await first.ExpectAsync(IonStreamProtocol.OpEnd);

            // A client that closes the transport without acknowledging the END has not been told.
            await first.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            await Task.Delay(300);
        }

        var c = await host.Probe.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(c.Disconnected.Task.IsCompleted, Is.False, "the stream completed, but the client has not heard");
            Assert.That(c.Context!.State, Is.EqualTo(IonStreamState.Closing));
        });

        await using var second = await RawStream.OpenAsync(host, "Count", query: Resume);
        await second.SendAsync(IonStreamProtocol.ResumeFrame(token, 3));
        await second.ExpectAsync(IonStreamProtocol.OpResumed);
        await second.ExpectAsync(IonStreamProtocol.OpEnd);
        await second.SendAsync(Ack(4));
        await second.ExpectCloseAsync(WebSocketCloseStatus.NormalClosure);

        Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.Completed));
    }

    [Test]
    public async Task A_close_right_behind_the_acknowledged_goodbye_is_the_end_not_a_lost_transport()
    {
        // What every client does on END: acknowledge it and close its side at once. The two land
        // together; the server must read the close as the end of a delivered goodbye, not wait out
        // a 30 s resume window for a client that has already gone.
        for (var round = 0; round < 20; round++)
        {
            await using var raw = await RawStream.OpenAsync(host, "Count", $"u{round}", Resume);
            await SendIntsAsync(raw, 0, 3, 0);
            ResumeTokenOf(await raw.ExpectAsync(IonStreamProtocol.OpReady));
            for (var i = 0; i < 3; i++)
                await raw.ExpectAsync(IonStreamProtocol.OpData);
            await raw.ExpectAsync(IonStreamProtocol.OpEnd);

            await raw.SendAsync(Ack(4));
            await raw.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

            var c = await host.Probe.ByUserAsync($"u{round}");
            var info = await c.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.Completed), $"round {round}");
            Assert.That(host.Log.Lines(Microsoft.Extensions.Logging.LogLevel.Information)
                .Any(l => l.Contains($"{c.ConnectionId} lost its", StringComparison.Ordinal)), Is.False, $"round {round}: never detached");
        }
    }

    [Test]
    public async Task Received_frames_are_acknowledged_in_batches_and_on_the_timer()
    {
        // A slow heartbeat (its timer ticks once a second), so an ACK that comes sooner came from the batch rule.
        await using var slow = await LabHost.StartAsync(o =>
        {
            o.KeepAliveInterval = TimeSpan.FromSeconds(5);
            o.ClientTimeout = TimeSpan.FromSeconds(10);
        });
        await using var raw = await RawStream.OpenAsync(slow, "Echo", query: Resume);
        await raw.SendArgsAsync(_ => { }, 0);
        ResumeTokenOf(await raw.ExpectAsync(IonStreamProtocol.OpReady));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < IonStreamProtocol.AckEveryFrames; i++)
            await raw.SendAsync(Data(i));
        long acked;
        while ((acked = await NextAckAsync(raw)) < IonStreamProtocol.AckEveryFrames)
        {
        }

        Assert.That(acked, Is.EqualTo(IonStreamProtocol.AckEveryFrames));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(700)), "a full batch is acknowledged at once, not on the timer");

        await raw.SendAsync(Data(99));
        Assert.That(await NextAckAsync(raw), Is.EqualTo(IonStreamProtocol.AckEveryFrames + 1), "a lone frame, on the timer");

        static byte[] Data(int i)
        {
            var w = new CborWriter();
            w.WriteStartArray(1);
            w.WriteTextString($"x{i}");
            w.WriteEndArray();
            return IonStreamProtocol.Frame(IonStreamProtocol.OpData, w);
        }

        static async Task<long> NextAckAsync(RawStream raw)
        {
            while (true)
            {
                var (type, frame) = await raw.NextAsync(TimeSpan.FromSeconds(3));
                Assert.That(type, Is.EqualTo(WebSocketMessageType.Binary), "expected an ACK, got a close");
                if (frame[0] == IonStreamProtocol.OpAck)
                    return IonStreamProtocol.ReadAck(frame.AsMemory(1));
            }
        }
    }

    [Test]
    public async Task A_transport_that_closes_without_a_goodbye_is_lost_not_left()
    {
        string token;
        await using (var first = await RawStream.OpenAsync(host, "Listen", query: Resume))
        {
            await SendTopicAsync(first, "t");
            token = ResumeTokenOf(await first.ExpectAsync(IonStreamProtocol.OpReady));

            // Only the socket is closed — the way a proxy that restarts closes it. The server lets a
            // lost transport go without a close handshake of its own.
            await first.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "just the socket", CancellationToken.None);
        }

        var c = await host.Probe.SingleAsync();
        await Eventually.TrueAsync(() => c.Context!.State == IonStreamState.Reconnecting, Wait,
            "a proxy closing the socket is not the client leaving");

        await using var second = await RawStream.OpenAsync(host, "Listen", query: Resume);
        await second.SendAsync(IonStreamProtocol.ResumeFrame(token, 0));
        await second.ExpectAsync(IonStreamProtocol.OpResumed);
        Assert.That(c.Context!.State, Is.EqualTo(IonStreamState.Connected));

        // Leaving on purpose is a CLOSE frame.
        await second.SendAsync(IonStreamProtocol.ClientCloseFrame("done"));
        Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.ClientClosed));
    }

    [Test]
    public async Task An_unknown_token_is_refused_as_not_resumable()
    {
        await using var raw = await RawStream.OpenAsync(host, "Listen", query: Resume);
        await raw.SendAsync(IonStreamProtocol.ResumeFrame("not-a-session", 0));

        var error = ReadError(await raw.ExpectAsync(IonStreamProtocol.OpError));
        Assert.That(error.code, Is.EqualTo(IonStreamProtocol.NotResumableCode));
        await raw.ExpectCloseAsync();
        Assert.That(host.Probe.All, Is.Empty);
    }

    [Test]
    public async Task A_token_opens_its_own_stream_and_no_other()
    {
        string token;
        await using (var first = await RawStream.OpenAsync(host, "Listen", query: Resume))
        {
            await SendTopicAsync(first, "t");
            token = ResumeTokenOf(await first.ExpectAsync(IonStreamProtocol.OpReady));
        }

        await using (var wrong = await RawStream.OpenAsync(host, "Count", query: Resume))
        {
            await wrong.SendAsync(IonStreamProtocol.ResumeFrame(token, 0));
            Assert.That(ReadError(await wrong.ExpectAsync(IonStreamProtocol.OpError)).code, Is.EqualTo(IonStreamProtocol.NotResumableCode));
        }

        await using var right = await RawStream.OpenAsync(host, "Listen", query: Resume);
        await right.SendAsync(IonStreamProtocol.ResumeFrame(token, 0));
        await right.ExpectAsync(IonStreamProtocol.OpResumed);
    }

    [Test]
    public async Task A_resume_that_claims_more_than_was_sent_ends_the_session()
    {
        string token;
        await using (var first = await RawStream.OpenAsync(host, "Listen", query: Resume))
        {
            await SendTopicAsync(first, "t");
            token = ResumeTokenOf(await first.ExpectAsync(IonStreamProtocol.OpReady));
        }

        var c = await host.Probe.SingleAsync();
        await using var liar = await RawStream.OpenAsync(host, "Listen", query: Resume);
        await liar.SendAsync(IonStreamProtocol.ResumeFrame(token, 5));

        Assert.That(ReadError(await liar.ExpectAsync(IonStreamProtocol.OpError)).code, Is.EqualTo("PROTOCOL_VIOLATION"));
        var info = await c.Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ProtocolViolation));

        await using var late = await RawStream.OpenAsync(host, "Listen", query: Resume);
        await late.SendAsync(IonStreamProtocol.ResumeFrame(token, 0));
        Assert.That(ReadError(await late.ExpectAsync(IonStreamProtocol.OpError)).code, Is.EqualTo(IonStreamProtocol.NotResumableCode),
            "a session that ended over it is gone");
    }

    [Test]
    public async Task An_ACK_for_frames_never_sent_is_a_violation()
    {
        await using var raw = await RawStream.OpenAsync(host, "Listen", query: Resume);
        await SendTopicAsync(raw, "t");
        await raw.ExpectAsync(IonStreamProtocol.OpReady);
        await raw.SendAsync(Ack(5));

        Assert.That(ReadError(await raw.ExpectAsync(IonStreamProtocol.OpError)).code, Is.EqualTo("PROTOCOL_VIOLATION"));
        var c = await host.Probe.SingleAsync();
        Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.ProtocolViolation));
    }

    [Test]
    public async Task An_ACK_on_a_stream_that_is_not_resumable_is_a_violation()
    {
        await using var raw = await RawStream.OpenAsync(host, "Listen");
        await SendTopicAsync(raw, "t");
        await raw.ExpectAsync(IonStreamProtocol.OpReady);
        await raw.SendAsync(Ack(0));

        Assert.That(ReadError(await raw.ExpectAsync(IonStreamProtocol.OpError)).code, Is.EqualTo("PROTOCOL_VIOLATION"));
    }

    [Test]
    public async Task Input_beyond_the_announced_budget_is_a_violation_not_a_stall()
    {
        // A stream method that does not read its input: the server keeps reading anyway — for the
        // ACKs behind the input — and holds what it cannot hand over, up to the budget READY
        // announced (plus one frame). A client that sends past it has broken the protocol.
        await using var tight = await LabHost.StartAsync(o =>
        {
            o.ResumeBufferSize = 32 * 1024;
            o.MaxReceiveMessageSize = 8 * 1024;
            o.InputQueueCapacity = 1;
        });
        tight.Probe.EchoDelay = TimeSpan.FromMinutes(1);

        await using var raw = await RawStream.OpenAsync(tight, "Echo", query: Resume);
        await raw.SendArgsAsync(_ => { }, 0);
        ResumeTokenOf(await raw.ExpectAsync(IonStreamProtocol.OpReady));

        var payload = new string('x', 1000);
        for (var i = 0; i < 60; i++)
        {
            var w = new CborWriter();
            w.WriteStartArray(1);
            w.WriteTextString(payload);
            w.WriteEndArray();
            await raw.SendAsync(IonStreamProtocol.Frame(IonStreamProtocol.OpData, w));
        }

        Assert.That(ReadError(await raw.ExpectAsync(IonStreamProtocol.OpError)).code, Is.EqualTo("PROTOCOL_VIOLATION"));
        var c = await tight.Probe.SingleAsync();
        Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.ProtocolViolation));
    }

    [Test]
    public async Task A_malformed_RESUME_is_a_violation()
    {
        await using var raw = await RawStream.OpenAsync(host, "Listen", query: Resume);
        await raw.SendAsync([IonStreamProtocol.OpResume, 0xff, 0x00]);

        Assert.That(ReadError(await raw.ExpectAsync(IonStreamProtocol.OpError)).code, Is.EqualTo("PROTOCOL_VIOLATION"));
        await raw.ExpectCloseAsync();
    }
}
