namespace IonTestClientServer.Streaming;

using ion.runtime;
using ion.runtime.client;
using ion.runtime.network;
using TestContracts;

/// <summary>
/// How a stream connection ends, from both sides, for every way it can end — run once per
/// transport. Each test asserts the client's view (what the enumeration did) and the server's view
/// (the disconnect hook: called exactly once, with the right reason, after the stream method stopped,
/// inside the connection's scope, with the connection already out of the registry).
/// </summary>
[TestFixture(IonStreamTransportKind.WebSocket)]
[TestFixture(IonStreamTransportKind.WebTransport)]
[Parallelizable(ParallelScope.None)]
public class StreamLifecycleTests(IonStreamTransportKind transport)
{
    private LabHost host = null!;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [SetUp]
    public async Task SetUp()
    {
        IonWsClient.ResetTransportHealth();
        host = await LabHost.StartAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            foreach (var line in host.Log.Lines(Microsoft.Extensions.Logging.LogLevel.Debug).TakeLast(80))
                TestContext.Out.WriteLine(line);

        await host.DisposeAsync();
    }

    private IStreamLab Lab(string user = "alice", Action<IonStreamClientOptions>? configure = null)
        => host.Lab(user, configure, transport);

    private static string Wire(IonStreamTransportKind kind) => kind == IonStreamTransportKind.WebTransport ? "wt" : "ws";

    /// <summary>The server-side invariants every ended connection must satisfy, whatever ended it.</summary>
    private async Task<IonDisconnectInfo> AssertEndedAsync(LabConnection c, IonDisconnectReason expected)
    {
        var info = await Eventually.WithinAsync(c.Disconnected.Task, Wait, $"OnDisconnected for {c.ConnectionId}");

        // Give a duplicate call a chance to happen before asserting there was none.
        await Task.Delay(50);

        Assert.Multiple(() =>
        {
            Assert.That(info.Reason, Is.EqualTo(expected), info.ToString());
            Assert.That(info.Exception is null, Is.EqualTo(info.IsGraceful), "Exception is null exactly when graceful");
            Assert.That(c.DisconnectCalls, Is.EqualTo(1), "service OnDisconnected calls");
            Assert.That(c.GlobalDisconnectCalls, Is.EqualTo(1), "global OnDisconnected calls");
            Assert.That(c.StreamStoppedBeforeDisconnect, Is.True, "stream method stopped before OnDisconnected");
            Assert.That(c.TokenCancelledBeforeDisconnect, Is.True, "ConnectionAborted fired before OnDisconnected");
            Assert.That(c.ScopeAliveInDisconnect, Is.True, "OnDisconnected ran inside the connection's scope");
            Assert.That(c.RemovedFromRegistryBeforeDisconnect, Is.True, "connection left the registry before OnDisconnected");
            Assert.That(c.StateDuringDisconnect, Is.EqualTo(IonStreamState.Closed));
            var trace = c.Trace.ToArray();
            Assert.That(trace.Take(2), Is.EqualTo(new[] { "global-connected", "connected" }),
                "global hooks connect first: " + string.Join(" → ", trace));
            Assert.That(trace.TakeLast(2), Is.EqualTo(new[] { "disconnected", "global-disconnected" }),
                "and disconnect last: " + string.Join(" → ", trace));
            Assert.That(c.Transport, Is.EqualTo(Wire(transport)), "the transport the server saw");
        });

        await Eventually.TrueAsync(() => host.Connections.Count == 0, Wait, "registry empty");
        return info;
    }

    [Test]
    public async Task Completed_stream_delivers_every_item_in_order_and_ends_gracefully()
    {
        var items = new List<int>();
        await foreach (var i in Lab().Count(10, 200, 0))
            items.Add(i);

        Assert.That(items, Is.EqualTo(Enumerable.Range(10, 200)));

        var c = await host.Probe.SingleAsync();
        await AssertEndedAsync(c, IonDisconnectReason.Completed);
    }

    [Test]
    public async Task Breaking_out_of_the_loop_is_a_graceful_client_close()
    {
        var seen = 0;
        await foreach (var _ in Lab().Count(0, 1_000_000, 5))
            if (++seen == 3)
                break;

        var c = await host.Probe.SingleAsync();
        var info = await AssertEndedAsync(c, IonDisconnectReason.ClientClosed);
        Assert.That(info.IsGraceful, Is.True);
    }

    [Test]
    public async Task Cancelling_the_token_is_a_graceful_client_close_and_the_caller_sees_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var seen = 0;

        Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Lab().Count(0, 1_000_000, 5, cts.Token))
                if (++seen == 5)
                    cts.Cancel();
        });

        var c = await host.Probe.SingleAsync();
        await AssertEndedAsync(c, IonDisconnectReason.ClientClosed);
    }

    [Test]
    public async Task A_stream_method_that_throws_fails_the_call_with_a_sanitized_error()
    {
        var items = new List<int>();
        var ex = Assert.ThrowsAsync<IonRequestException>(async () =>
        {
            await foreach (var i in Lab().Explode(3))
                items.Add(i);
        });

        Assert.That(items, Is.EqualTo(new[] { 0, 1, 2 }), "items before the failure still arrive");
        Assert.That(ex!.Error.code, Is.EqualTo("INTERNAL_ERROR"));
        Assert.That(ex.Error.msg, Does.Not.Contain("boom"), "details are sanitized unless DetailedErrors");

        var c = await host.Probe.SingleAsync();
        var info = await AssertEndedAsync(c, IonDisconnectReason.Faulted);
        Assert.That(info.Exception, Is.TypeOf<InvalidOperationException>().With.Message.EqualTo("boom"));
    }

    [Test]
    public async Task Server_close_reaches_the_client_as_a_closed_exception_after_flushing_pushes()
    {
        var received = new List<long>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var e in Lab().Listen("news"))
                received.Add(e.seq);
        });

        var c = await host.Probe.SingleAsync();
        await Eventually.WithinAsync(c.StreamStarted.Task.ContinueWith(_ => 0), Wait, "stream started");

        for (var i = 0; i < 50; i++)
            await host.Connections.Group("news").SendAsync(new LabEvent(i, "news", "x"));
        var closed = await host.Connections.Client(c.ConnectionId).CloseAsync("kicked", allowReconnect: false);
        Assert.That(closed, Is.EqualTo(1));

        var ex = Assert.ThrowsAsync<IonStreamClosedException>(async () => await consumer.WaitAsync(Wait));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Reason, Is.EqualTo("kicked"));
            Assert.That(ex.AllowReconnect, Is.False);
            Assert.That(received, Is.EqualTo(Enumerable.Range(0, 50).Select(i => (long)i)), "pushes queued before the close are flushed, in order");
        });

        var info = await AssertEndedAsync(c, IonDisconnectReason.ServerClosed);
        Assert.That(info.Message, Is.EqualTo("kicked"));
        Assert.That(c.GroupsAtDisconnect, Is.EquivalentTo(new[] { "news" }), "the hook still sees the groups it was in");
        Assert.That(host.Connections.Group("news").Connections, Is.Empty, "and the group no longer has it");
    }

    [Test]
    public async Task Server_abort_reaches_the_client_as_a_dropped_transport()
    {
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in Lab().Listen("t"))
            {
            }
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);
        Assert.That(host.Connections.Client(c.ConnectionId).Abort("nope"), Is.EqualTo(1));

        var ex = Assert.ThrowsAsync<IonStreamDisconnectedException>(async () => await consumer.WaitAsync(Wait));
        Assert.That(ex!.Reason, Is.EqualTo(IonDisconnectReason.TransportLost));

        var info = await AssertEndedAsync(c, IonDisconnectReason.ServerAborted);
        Assert.That(info.Exception, Is.TypeOf<IonStreamAbortedException>());
    }

    [Test]
    public async Task A_connect_hook_that_throws_rejects_the_call_without_falling_back_to_another_transport()
    {
        var ex = Assert.ThrowsAsync<IonRequestException>(async () =>
        {
            await foreach (var _ in host.Lab("banned", null, transport, IonStreamTransportKind.WebSocket).Count(0, 10, 0))
            {
            }
        });

        Assert.That(ex!.Error.code, Is.EqualTo("BANNED"));
        Assert.That(ex, Is.Not.InstanceOf<IonStreamDisconnectedException>());

        var c = await host.Probe.ByUserAsync("banned");
        await Eventually.TrueAsync(() => c.GlobalDisconnectCalls == 1, Wait, "global hook disconnected");
        await Task.Delay(100);

        Assert.Multiple(() =>
        {
            Assert.That(host.Probe.All, Has.Count.EqualTo(1), "a refusal is not retried on the next transport");
            Assert.That(c.DisconnectCalls, Is.Zero, "the hook that refused never connected, so it is not disconnected");
            Assert.That(c.GlobalDisconnectCalls, Is.EqualTo(1), "the global hook that did connect is");
            Assert.That(c.Context!.Disconnect!.Reason, Is.EqualTo(IonDisconnectReason.Rejected));
        });
    }

    [Test]
    public async Task A_connect_hook_that_aborts_ends_the_connection_and_still_gets_its_disconnect()
    {
        Assert.ThrowsAsync<IonStreamDisconnectedException>(async () =>
        {
            await foreach (var _ in Lab("self-abort").Count(0, 10, 0))
            {
            }
        });

        var c = await host.Probe.SingleAsync();
        var info = await Eventually.WithinAsync(c.Disconnected.Task, Wait, "disconnect");
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ServerAborted));
        Assert.That(c.DisconnectCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task Host_shutdown_says_goodbye_and_invites_the_client_back()
    {
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in Lab().Listen("t"))
            {
            }
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);

        await host.StopAsync();

        var ex = Assert.ThrowsAsync<IonStreamClosedException>(async () => await consumer.WaitAsync(Wait));
        Assert.That(ex!.AllowReconnect, Is.True);

        var info = await c.Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ServerShutdown));
        Assert.That(c.DisconnectCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task A_stream_method_that_ignores_cancellation_does_not_hold_the_disconnect_hook_hostage()
    {
        await using var slow = await LabHost.StartAsync(o => o.StreamStopTimeout = TimeSpan.FromMilliseconds(300));

        await foreach (var _ in slow.Lab("alice", null, transport).Stubborn(3000))
            break;

        var c = await slow.Probe.SingleAsync();
        var started = DateTime.UtcNow;
        var info = await c.Disconnected.Task.WaitAsync(Wait);

        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ClientClosed));
        Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(2.5)), "the hook ran after the stop timeout, not after the method");
        Assert.That(c.StreamStopped, Is.False, "the method was still running when the hook ran");
        Assert.That(slow.Log.Lines().Any(l => l.Contains("did not stop within")), Is.True, "the overrun is logged");

        await Eventually.TrueAsync(() => c.StreamStopped, TimeSpan.FromSeconds(5), "the method eventually finishes");
    }

    [Test]
    public async Task A_slow_connect_hook_is_not_mistaken_for_a_silent_client()
    {
        host.Probe.ConnectDelay = TimeSpan.FromMilliseconds(2500); // longer than ClientTimeout (1.5 s)

        var items = new List<int>();
        await foreach (var i in Lab().Count(0, 5, 0))
            items.Add(i);

        Assert.That(items, Has.Count.EqualTo(5));
        var c = await host.Probe.SingleAsync();
        await AssertEndedAsync(c, IonDisconnectReason.Completed);
    }

    [Test]
    public async Task An_idle_stream_is_kept_alive_by_the_heartbeat_well_past_both_timeouts()
    {
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in Lab().Listen("quiet"))
            {
            }
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);

        // Four client timeouts of silence from the application: only pings on the wire.
        await Task.Delay(TimeSpan.FromSeconds(6));

        Assert.That(c.Disconnected.Task.IsCompleted, Is.False, "the heartbeat kept the idle connection alive");
        Assert.That(consumer.IsCompleted, Is.False);

        await host.Connections.Group("quiet").SendAsync(new LabEvent(1, "quiet", "still here"));
        await host.Connections.All.CloseAsync("done");
        Assert.ThrowsAsync<IonStreamClosedException>(async () => await consumer.WaitAsync(Wait));
        await AssertEndedAsync(c, IonDisconnectReason.ServerClosed);
    }

    [Test]
    public async Task A_server_that_goes_silent_times_the_client_out()
    {
        await using var mute = await LabHost.StartAsync(o =>
        {
            o.KeepAliveInterval = Timeout.InfiniteTimeSpan; // the server never pings
            o.ClientTimeout = TimeSpan.FromSeconds(30);
        });

        var ex = Assert.ThrowsAsync<IonStreamDisconnectedException>(async () =>
        {
            await foreach (var _ in mute.Lab("alice", o => o.ServerTimeout = TimeSpan.FromMilliseconds(700), transport).Listen("x"))
            {
            }
        });

        Assert.That(ex!.Reason, Is.EqualTo(IonDisconnectReason.Timeout));

        var c = await mute.Probe.SingleAsync();
        var info = await c.Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.IsGraceful, Is.False, "a client that timed the server out aborted the transport");
    }

    [Test]
    public async Task Duplex_echo_preserves_order_and_ends_when_the_input_ends()
    {
        var input = Enumerable.Range(0, 2000).Select(i => $"item-{i}").ToArray();
        var output = new List<string>();

        await foreach (var s in Lab().Echo(Produce(input)))
            output.Add(s);

        Assert.That(output, Is.EqualTo(input.Select(s => s.ToUpperInvariant())));
        var c = await host.Probe.SingleAsync();
        await AssertEndedAsync(c, IonDisconnectReason.Completed);

        static async IAsyncEnumerable<string> Produce(string[] values)
        {
            foreach (var v in values)
            {
                yield return v;
                if (v.EndsWith('7'))
                    await Task.Yield();
            }
        }
    }

    [Test]
    public async Task An_input_stream_that_throws_surfaces_the_callers_own_exception()
    {
        var output = new List<string>();
        var ex = Assert.ThrowsAsync<ArithmeticException>(async () =>
        {
            await foreach (var s in Lab().Echo(Faulty()))
                output.Add(s);
        });

        Assert.That(ex!.Message, Is.EqualTo("input broke"));

        var c = await host.Probe.SingleAsync();
        var info = await c.Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.AnyOf(IonDisconnectReason.Faulted, IonDisconnectReason.ClientClosed),
            "the server either saw the input fault or the client leaving first");

        static async IAsyncEnumerable<string> Faulty()
        {
            yield return "a";
            yield return "b";
            await Task.Delay(50);
            throw new ArithmeticException("input broke");
        }
    }

    [Test]
    [TestCase(1)]
    [TestCase(16 * 1024 + 7)]
    [TestCase(512 * 1024)]
    [TestCase(3 * 1024 * 1024 + 1)]
    public async Task Large_items_survive_fragmentation_and_framing(int size)
    {
        var n = 0;
        await foreach (var blob in Lab().Blobs(size, 4))
        {
            Assert.That(blob.ToArray(), Is.EqualTo(StreamLabImpl.Blob(size, n)), $"payload {n}");
            n++;
        }

        Assert.That(n, Is.EqualTo(4));
    }
}
