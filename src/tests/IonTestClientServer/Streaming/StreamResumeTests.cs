namespace IonTestClientServer.Streaming;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ion.runtime;
using ion.runtime.client;
using ion.runtime.network;
using TestContracts;

/// <summary>
/// Resumable sessions, through a network that fails for real: the connection drops and the call
/// goes on — with nothing lost and nothing twice, in both directions, whatever was in flight — and
/// the server keeps its side of it: the same connection, the same groups, no disconnect hook, pushes
/// held for the client until it is back. And when the client does not come back, the session is let
/// go after the window, not before and not never.
/// </summary>
[TestFixture(IonStreamTransportKind.WebSocket)]
[TestFixture(IonStreamTransportKind.WebTransport)]
[Parallelizable(ParallelScope.None)]
public class StreamResumeTests(IonStreamTransportKind transport)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private LabHost host = null!;
    private FaultyProxy proxy = null!;
    private readonly ConcurrentQueue<IonStreamReconnected> reconnected = new();
    private readonly ConcurrentQueue<IonStreamReconnecting> reconnecting = new();

    [SetUp]
    public async Task SetUp()
    {
        IonWsClient.ResetTransportHealth();
        reconnected.Clear();
        reconnecting.Clear();
        host = await LabHost.StartAsync();
        proxy = FaultyProxy.Start(host.Port);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            Dump(host);

        await proxy.DisposeAsync();
        await host.DisposeAsync();
    }

    private static void Dump(LabHost h)
    {
        foreach (var c in h.Probe.All)
            TestContext.Out.WriteLine($"server saw {c.User} {c.ConnectionId}: {c.Context?.State} {c.Context?.Disconnect} [{c.Context?.Disconnect?.Exception?.GetType().Name}: {c.Context?.Disconnect?.Exception?.Message}]");
        foreach (var line in h.Log.Lines(Microsoft.Extensions.Logging.LogLevel.Debug).TakeLast(60))
            TestContext.Out.WriteLine(line);
    }

    private IStreamLab Lab(string user = "alice", Action<IonStreamClientOptions>? more = null, LabHost? on = null, FaultyProxy? via = null)
        => (on ?? host).LabVia((via ?? proxy).BaseAddress, user, LabHost.Reconnecting(o =>
        {
            // A UDP partition fails a QUIC connect by timing out; three seconds a try would only slow the tests down.
            o.WebTransportConnectTimeout = TimeSpan.FromSeconds(1);
            o.OnReconnected = reconnected.Enqueue;
            o.OnReconnecting = reconnecting.Enqueue;
            more?.Invoke(o);
        }), transport);

    /// <summary>
    /// Breaks the connection under a running call. A WebSocket gets an RST — both sides know at once;
    /// QUIC cannot be reset from outside, so a WebTransport call is partitioned until the server has
    /// let go of the transport, and then the network comes back.
    /// </summary>
    private async Task DropAsync(LabConnection c)
    {
        if (transport == IonStreamTransportKind.WebSocket)
        {
            proxy.Reset();
            return;
        }

        proxy.Freeze();
        try
        {
            await Eventually.TrueAsync(() => c.Context?.State == IonStreamState.Reconnecting, Wait, "the server let the transport go");
        }
        finally
        {
            proxy.Thaw();
        }
    }

    private int ResumesOf(LabConnection c)
        => host.Log.Lines(Microsoft.Extensions.Logging.LogLevel.Information)
            .Count(l => l.Contains($"Stream connection {c.ConnectionId} resumed", StringComparison.Ordinal));

    [Test]
    public async Task A_dropped_connection_is_resumed_with_nothing_lost_and_nothing_twice()
    {
        var got = new ConcurrentQueue<int>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var i in Lab().Count(0, 600, 3))
                got.Enqueue(i);
        });

        var c = await host.Probe.SingleAsync();
        await Eventually.TrueAsync(() => got.Count >= 100, Wait, "the stream is flowing");

        await DropAsync(c);
        await consumer.WaitAsync(Wait);

        Assert.Multiple(() =>
        {
            Assert.That(got, Is.EqualTo(Enumerable.Range(0, 600)), "every item, once, in order");
            Assert.That(reconnected, Is.Not.Empty, "the client did reconnect");
            Assert.That(reconnected.All(r => r.Resumed), Is.True, "and resumed rather than started afresh");
            Assert.That(reconnected.All(r => r.ConnectionId == c.ConnectionId), Is.True, "the same connection");
            Assert.That(host.Probe.All, Has.Count.EqualTo(1), "one connection on the server, however many transports");
            Assert.That(c.ConnectCalls, Is.EqualTo(1));
            Assert.That(ResumesOf(c), Is.GreaterThanOrEqualTo(1));
        });

        var info = await c.Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.Completed));
        Assert.That(c.DisconnectCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task While_the_client_is_away_nothing_ends_and_pushes_wait_for_it()
    {
        var got = new ConcurrentQueue<LabEvent>();
        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            await foreach (var e in Lab().Listen("room", cts.Token))
                got.Enqueue(e);
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);
        await host.Connections.Group("room").SendAsync(new LabEvent(0, "room", "before"));
        await Eventually.TrueAsync(() => got.Count == 1, Wait, "the first push");

        proxy.Freeze();
        await Eventually.TrueAsync(() => c.Context!.State == IonStreamState.Reconnecting, Wait, "the server noticed the silence");

        Assert.Multiple(() =>
        {
            Assert.That(c.Context!.ConnectionAborted.IsCancellationRequested, Is.False, "ConnectionAborted has not fired");
            Assert.That(c.Context.Disconnect, Is.Null);
            Assert.That(c.DisconnectCalls, Is.Zero, "no disconnect hook for a client that may come back");
            Assert.That(c.StreamStopped, Is.False, "the stream method runs on");
            Assert.That(host.Connections.Group("room").Connections.Select(x => x.ConnectionId), Does.Contain(c.ConnectionId), "still in its group");
        });

        for (var i = 1; i <= 10; i++)
            Assert.That(await host.Connections.Group("room").SendAsync(new LabEvent(i, "room", "during")), Is.EqualTo(1), "queued for the absent client");

        proxy.Thaw();
        await Eventually.TrueAsync(() => got.Count == 11, Wait, "everything pushed while it was away");

        Assert.Multiple(() =>
        {
            Assert.That(got.Select(e => e.seq), Is.EqualTo(Enumerable.Range(0, 11).Select(i => (long)i)), "in order, once each");
            Assert.That(c.Context!.State, Is.EqualTo(IonStreamState.Connected));
            Assert.That(reconnected.Last().Resumed, Is.True);
            Assert.That(host.Probe.All, Has.Count.EqualTo(1));
        });

        await cts.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await consumer.WaitAsync(Wait));
        Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.ClientClosed),
            "a client that resumed and then left, left");
    }

    [Test]
    public async Task Input_is_neither_lost_nor_repeated_across_a_drop()
    {
        const int count = 400;
        LabConnection? c = null;
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<string> Input([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < count; i++)
            {
                if (i == 150)
                {
                    c ??= await host.Probe.SingleAsync();
                    _ = Task.Run(async () =>
                    {
                        try { await DropAsync(c); dropped.TrySetResult(); }
                        catch (Exception ex) { dropped.TrySetException(ex); }
                    });
                }

                yield return $"m{i}";
                await Task.Delay(2, ct);
            }
        }

        var output = new List<string>();
        await foreach (var s in Lab().Echo(Input()).WithCancellation(new CancellationTokenSource(Wait).Token))
            output.Add(s);

        await dropped.Task.WaitAsync(Wait);
        Assert.That(output, Is.EqualTo(Enumerable.Range(0, count).Select(i => $"M{i}")),
            "every input item reached the stream method exactly once, and every echo came back exactly once");
        Assert.That(host.Probe.All, Has.Count.EqualTo(1));
        Assert.That(reconnected.All(r => r.Resumed), Is.True);
    }

    [Test]
    public async Task The_same_session_survives_one_drop_after_another()
    {
        const int rounds = 3;
        var got = new ConcurrentQueue<LabEvent>();
        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            await foreach (var e in Lab().Listen("room", cts.Token))
                got.Enqueue(e);
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);

        for (var round = 0; round < rounds; round++)
        {
            await host.Connections.Group("room").SendAsync(new LabEvent(round, "room", "r"));
            await Eventually.TrueAsync(() => got.Count == round + 1, Wait, $"push {round}");
            await DropAsync(c);
        }

        await host.Connections.Group("room").SendAsync(new LabEvent(rounds, "room", "last"));
        await Eventually.TrueAsync(() => got.Count == rounds + 1, Wait, "the push after the last drop");

        Assert.Multiple(() =>
        {
            Assert.That(got.Select(e => e.seq), Is.EqualTo(Enumerable.Range(0, rounds + 1).Select(i => (long)i)));
            Assert.That(host.Probe.All, Has.Count.EqualTo(1));
            Assert.That(c.ConnectCalls, Is.EqualTo(1));
            Assert.That(c.DisconnectCalls, Is.Zero);
            Assert.That(ResumesOf(c), Is.GreaterThanOrEqualTo(rounds));
        });

        await cts.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await consumer.WaitAsync(Wait));
    }

    [Test]
    public async Task A_slow_consumer_on_a_resumable_session_is_never_dropped_and_loses_nothing()
    {
        // A small replay budget: the server stops sending while the client is not acknowledging
        // (because it is not reading), and must neither drop anything nor mistake it for death.
        await using var tight = await LabHost.StartAsync(o => o.ResumeBufferSize = 64 * 1024);
        await using var tightProxy = FaultyProxy.Start(tight.Port);

        const int count = 20_000;
        var last = -1;
        await foreach (var i in Lab("slow", on: tight, via: tightProxy).Count(0, count, 0))
        {
            Assert.That(i, Is.EqualTo(last + 1), "in order, nothing lost");
            last = i;
            if (i is 0 or 10_000)
                await Task.Delay(TimeSpan.FromSeconds(2.5));
        }

        Assert.That(last, Is.EqualTo(count - 1));
        var c = await tight.Probe.SingleAsync();
        try
        {
            Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.Completed));
        }
        catch (TimeoutException)
        {
            Dump(tight);
            throw;
        }

        Assert.That(reconnecting, Is.Empty, "a slow client is not a lost one");
    }

    [Test]
    public async Task A_duplex_flood_through_small_replay_buffers_neither_deadlocks_nor_loses()
    {
        await using var tight = await LabHost.StartAsync(o => o.ResumeBufferSize = 32 * 1024);
        await using var tightProxy = FaultyProxy.Start(tight.Port);

        const int count = 3_000;
        var payload = new string('x', 1000);
        var lab = Lab("flood", o => o.ResumeBufferSize = 32 * 1024, on: tight, via: tightProxy);

        var output = 0;
        await foreach (var s in lab.Echo(Flood()).WithCancellation(new CancellationTokenSource(Wait).Token))
        {
            Assert.That(s, Is.EqualTo($"{output}:{payload}".ToUpperInvariant()));
            output++;
        }

        Assert.That(output, Is.EqualTo(count));
        Assert.That(reconnecting, Is.Empty);

        async IAsyncEnumerable<string> Flood()
        {
            for (var i = 0; i < count; i++)
            {
                yield return $"{i}:{payload}";
                if (i % 100 == 0)
                    await Task.Yield();
            }
        }
    }

    [Test]
    public async Task A_stream_method_that_reads_slowly_never_holds_up_the_acknowledgements()
    {
        // The duplex deadlock a resumable session must not have: the method waits to send (its
        // replay budget spent, waiting for the client's ACK) and so does not read its input; the
        // client's ACK is on the wire behind input the server would not read until the method did.
        // The server keeps reading regardless, and holds the input it cannot hand over yet.
        await using var tight = await LabHost.StartAsync(o =>
        {
            o.ResumeBufferSize = 32 * 1024;
            o.InputQueueCapacity = 1;
        });
        await using var tightProxy = FaultyProxy.Start(tight.Port);
        tight.Probe.EchoDelay = TimeSpan.FromMilliseconds(1);

        const int count = 200;
        var payload = new string('y', 1000);
        var output = 0;
        await foreach (var s in Lab("slow-reader", o => o.ResumeBufferSize = 32 * 1024, on: tight, via: tightProxy)
                           .Echo(Flood()).WithCancellation(new CancellationTokenSource(Wait).Token))
        {
            Assert.That(s, Is.EqualTo($"{output}:{payload}".ToUpperInvariant()));
            output++;
        }

        Assert.That(output, Is.EqualTo(count));
        Assert.That(reconnecting, Is.Empty, "nothing stalled long enough to look like a dead connection");

        async IAsyncEnumerable<string> Flood()
        {
            for (var i = 0; i < count; i++)
            {
                yield return $"{i}:{payload}";
                if (i % 50 == 0)
                    await Task.Yield();
            }
        }
    }

    [Test]
    public async Task A_client_that_does_not_come_back_is_let_go_after_the_resume_window()
    {
        await using var windowed = await LabHost.StartAsync(o => o.ResumeWindow = TimeSpan.FromSeconds(1));
        await using var windowedProxy = FaultyProxy.Start(windowed.Port);

        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in Lab(on: windowed, via: windowedProxy).Listen("t", cts.Token))
            {
            }
        });

        var c = await windowed.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);

        var sw = Stopwatch.StartNew();
        windowedProxy.Freeze();

        await Eventually.TrueAsync(() => c.Context!.State == IonStreamState.Reconnecting, Wait, "detached");
        var detachedAfter = sw.Elapsed;
        Assert.That(c.Context!.ConnectionAborted.IsCancellationRequested, Is.False);

        var info = await c.Disconnected.Task.WaitAsync(Wait);
        var endedAfter = sw.Elapsed;
        TestContext.Out.WriteLine($"detached after {detachedAfter.TotalMilliseconds:F0} ms, ended after {endedAfter.TotalMilliseconds:F0} ms");

        Assert.Multiple(() =>
        {
            Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.Timeout), "the reason the transport was lost for");
            Assert.That(info.Exception, Is.TypeOf<IonStreamNotResumedException>());
            Assert.That(info.Exception!.InnerException, Is.TypeOf<TimeoutException>());
            Assert.That(endedAfter - detachedAfter, Is.GreaterThan(TimeSpan.FromMilliseconds(900)), "not before the window ran out");
            Assert.That(endedAfter - detachedAfter, Is.LessThan(TimeSpan.FromSeconds(3)), "and not long after");
            Assert.That(c.TokenCancelledBeforeDisconnect, Is.True, "ConnectionAborted fired when the session ended");
            Assert.That(c.StreamStoppedBeforeDisconnect, Is.True);
            Assert.That(c.DisconnectCalls, Is.EqualTo(1));
        });

        await Eventually.TrueAsync(() => windowed.Connections.Count == 0, Wait, "registry drained");
        await cts.CancelAsync();
        windowedProxy.Thaw();
        Assert.CatchAsync<OperationCanceledException>(async () => await consumer.WaitAsync(Wait));
    }

    [Test]
    public async Task After_the_window_the_call_starts_afresh_and_says_so()
    {
        await using var windowed = await LabHost.StartAsync(o => o.ResumeWindow = TimeSpan.FromMilliseconds(300));
        await using var windowedProxy = FaultyProxy.Start(windowed.Port);

        var got = new ConcurrentQueue<LabEvent>();
        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            await foreach (var e in Lab(on: windowed, via: windowedProxy).Listen("room", cts.Token))
                got.Enqueue(e);
        });

        var first = await windowed.Probe.SingleAsync();
        await first.StreamStarted.Task.WaitAsync(Wait);
        await windowed.Connections.Group("room").SendAsync(new LabEvent(1, "room", "first"));
        await Eventually.TrueAsync(() => got.Count == 1, Wait, "the first push");

        windowedProxy.Freeze();
        var info = await first.Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Exception, Is.TypeOf<IonStreamNotResumedException>());
        windowedProxy.Thaw();

        LabConnection? second = null;
        await Eventually.TrueAsync(() => (second = windowed.Probe.All.FirstOrDefault(x => x.ConnectionId != first.ConnectionId)) is not null,
            Wait, "the call came back as a new connection");
        await second!.StreamStarted.Task.WaitAsync(Wait);
        await Eventually.TrueAsync(() => !reconnected.IsEmpty, Wait, "OnReconnected");

        await windowed.Connections.Group("room").SendAsync(new LabEvent(2, "room", "second"));
        await Eventually.TrueAsync(() => got.Count == 2, Wait, "the push to the new connection");

        Assert.Multiple(() =>
        {
            Assert.That(got.Select(e => e.body), Is.EqualTo(new[] { "first", "second" }), "the consumer never noticed");
            Assert.That(reconnected.Last().Resumed, Is.False, "and was told the session was started afresh");
            Assert.That(reconnected.Last().ConnectionId, Is.EqualTo(second.ConnectionId));
            Assert.That(windowed.Probe.All, Has.Count.EqualTo(2));
        });

        await cts.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await consumer.WaitAsync(Wait));
    }

    [Test]
    public async Task A_session_resumes_over_WebSocket_when_QUIC_stops_getting_through()
    {
        if (transport != IonStreamTransportKind.WebTransport)
            Assert.Ignore("A WebTransport session losing UDP.");

        var got = new ConcurrentQueue<LabEvent>();
        using var cts = new CancellationTokenSource();
        var lab = host.LabVia(proxy.BaseAddress, "roamer", LabHost.Reconnecting(o =>
        {
            o.WebTransportConnectTimeout = TimeSpan.FromSeconds(1);
            o.OnReconnected = reconnected.Enqueue;
        }), IonStreamTransportKind.WebTransport, IonStreamTransportKind.WebSocket);

        var consumer = Task.Run(async () =>
        {
            await foreach (var e in lab.Listen("room", cts.Token))
                got.Enqueue(e);
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);
        Assert.That(c.Transport, Is.EqualTo("wt"));

        await host.Connections.Group("room").SendAsync(new LabEvent(1, "room", "over quic"));
        await Eventually.TrueAsync(() => got.Count == 1, Wait, "the first push");

        proxy.FreezeUdp();
        await Eventually.TrueAsync(() => c.Context!.TransportName == "ws" && c.Context.State == IonStreamState.Connected, Wait,
            "resumed on the WebSocket");

        await host.Connections.Group("room").SendAsync(new LabEvent(2, "room", "over tcp"));
        await Eventually.TrueAsync(() => got.Count == 2, Wait, "the push after the switch");

        Assert.Multiple(() =>
        {
            Assert.That(got.Select(e => e.body), Is.EqualTo(new[] { "over quic", "over tcp" }));
            Assert.That(reconnected.Last().Resumed, Is.True);
            Assert.That(host.Probe.All, Has.Count.EqualTo(1), "one session, two transports");
        });

        await cts.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await consumer.WaitAsync(Wait));
    }
}
