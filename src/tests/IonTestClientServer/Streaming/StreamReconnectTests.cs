namespace IonTestClientServer.Streaming;

using System.Collections.Concurrent;
using System.Diagnostics;
using ion.runtime;
using ion.runtime.client;
using ion.runtime.network;
using Microsoft.Extensions.DependencyInjection;
using TestContracts;

/// <summary>
/// Reconnecting as a policy: what comes back and what does not. A client that reconnects to
/// everything hammers servers that said no, and one that reconnects to nothing is no better than a
/// bare WebSocket — so every ending is checked for the one right answer: a refusal, an error or a
/// kick is final; a lost network, a restart and a server that is not up yet are ridden out; a
/// client that notices a dead network before the server does takes its session back at once.
/// </summary>
[Parallelizable(ParallelScope.None)]
public class StreamReconnectTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<IonStreamReconnected> reconnected = new();
    private readonly ConcurrentQueue<IonStreamReconnecting> reconnecting = new();

    [SetUp]
    public void SetUp()
    {
        IonWsClient.ResetTransportHealth();
        reconnected.Clear();
        reconnecting.Clear();
    }

    private Action<IonStreamClientOptions> Reconnecting(Action<IonStreamClientOptions>? more = null) => LabHost.Reconnecting(o =>
    {
        o.OnReconnected = reconnected.Enqueue;
        o.OnReconnecting = reconnecting.Enqueue;
        more?.Invoke(o);
    });

    /// <summary>A client for an address that is not tied to any one server's lifetime — for servers that come and go.</summary>
    private IStreamLab Detached(Uri address, string user, Action<IonStreamClientOptions>? more = null)
    {
        var client = LabHostClient(address, user, Reconnecting(more));
        return client.ForService<IStreamLab>(new ServiceCollection().BuildServiceProvider());
    }

    private static IonClient LabHostClient(Uri address, string user, Action<IonStreamClientOptions> configure)
    {
        // The same client LabHost builds, minus the host.
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        return IonClient.Create(new HttpClient(handler) { BaseAddress = address })
            .WithInterceptor(new UserHeader(user))
            .WithStreamOptions(o =>
            {
                o.ServerCertificateValidation = (_, _, _, _) => true;
                o.Transports = [IonStreamTransportKind.WebSocket];
                o.KeepAliveInterval = TimeSpan.FromMilliseconds(150);
                o.ServerTimeout = TimeSpan.FromMilliseconds(1500);
                o.HandshakeTimeout = TimeSpan.FromSeconds(3);
                o.CloseTimeout = TimeSpan.FromSeconds(2);
                configure(o);
            });
    }

    [Test]
    public async Task A_client_that_notices_first_takes_its_session_back_before_the_server_notices()
    {
        await using var host = await LabHost.StartAsync();
        await using var proxy = FaultyProxy.Start(host.Port);

        var got = new ConcurrentQueue<LabEvent>();
        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            await foreach (var e in host.LabVia(proxy.BaseAddress, "alice", Reconnecting()).Listen("room", cts.Token))
                got.Enqueue(e);
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);
        await host.Connections.Group("room").SendAsync(new LabEvent(1, "room", "before"));
        await Eventually.TrueAsync(() => got.Count == 1, Wait, "the first push");

        // The client's half dies; the server's socket stays open and silent. The server would need
        // its 1.5 s client timeout to notice — the client is back long before.
        var sw = Stopwatch.StartNew();
        proxy.CutClientSide();
        await Eventually.TrueAsync(() => reconnected.Count == 1, Wait, "the client reconnected");
        var back = sw.Elapsed;

        await host.Connections.Group("room").SendAsync(new LabEvent(2, "room", "after"));
        await Eventually.TrueAsync(() => got.Count == 2, Wait, "the push after the takeover");

        Assert.Multiple(() =>
        {
            Assert.That(back, Is.LessThan(TimeSpan.FromMilliseconds(1400)), "resumed before the server's own timeout could fire");
            Assert.That(reconnected.Single().Resumed, Is.True, "the new transport took the session over");
            Assert.That(got.Select(e => e.body), Is.EqualTo(new[] { "before", "after" }));
            Assert.That(host.Probe.All, Has.Count.EqualTo(1));
            Assert.That(c.DisconnectCalls, Is.Zero);
        });

        await cts.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await consumer.WaitAsync(Wait));
        Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.ClientClosed));
    }

    [Test]
    public async Task Random_drops_never_lose_or_repeat_an_item()
    {
        await using var host = await LabHost.StartAsync();
        var random = new Random(4242);

        for (var round = 0; round < 8; round++)
        {
            await using var proxy = FaultyProxy.Start(host.Port);
            var user = $"chaos{round}";
            var got = new List<int>();
            var consumer = Task.Run(async () =>
            {
                await foreach (var i in host.LabVia(proxy.BaseAddress, user, Reconnecting()).Count(0, 500, 1))
                    got.Add(i);
            });

            // One to three resets, at random moments of the stream — including, now and then, while
            // the previous resume is still being negotiated.
            var resets = random.Next(1, 4);
            for (var r = 0; r < resets && !consumer.IsCompleted; r++)
            {
                await Task.Delay(random.Next(20, 400));
                proxy.Reset();
            }

            await consumer.WaitAsync(Wait);
            Assert.That(got, Is.EqualTo(Enumerable.Range(0, 500)), $"round {round}: every item once, in order");

            var c = await host.Probe.ByUserAsync(user);
            Assert.That(host.Probe.All.Count(x => x.User == user), Is.EqualTo(1), $"round {round}: one session");
            Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.Completed), $"round {round}");
        }
    }

    [Test]
    public async Task A_refused_call_is_not_retried()
    {
        await using var host = await LabHost.StartAsync();

        var ex = Assert.CatchAsync<IonRequestException>(async () =>
        {
            await foreach (var _ in host.Lab("banned", Reconnecting()).Count(0, 1, 0))
            {
            }
        });

        Assert.That(ex!.Error.code, Is.EqualTo("BANNED"));
        await Task.Delay(300);
        Assert.That(reconnecting, Is.Empty);
        Assert.That(host.Probe.All, Has.Count.EqualTo(1), "one attempt");
    }

    [Test]
    public async Task A_refused_upgrade_is_not_retried()
    {
        await using var host = await LabHost.StartAsync();

        var ex = Assert.CatchAsync<IonRequestException>(async () =>
        {
            await foreach (var _ in host.Lab("forged", Reconnecting()).Count(0, 1, 0))
            {
            }
        });

        Assert.That(ex, Is.Not.InstanceOf<IonStreamDisconnectedException>(), ex!.ToString());
        Assert.That(ex.HttpStatusCode, Is.EqualTo(401));
        Assert.That(ex.Error.code, Is.EqualTo("TICKET_REFUSED"), "the Ion status header of the refusal");
        Assert.That(reconnecting, Is.Empty);
    }

    [Test]
    public async Task A_server_error_is_final_even_when_reconnecting()
    {
        await using var host = await LabHost.StartAsync();

        var items = new List<int>();
        var ex = Assert.CatchAsync<IonRequestException>(async () =>
        {
            await foreach (var i in host.Lab("alice", Reconnecting()).Explode(3))
                items.Add(i);
        });

        Assert.That(ex, Is.Not.InstanceOf<IonStreamDisconnectedException>());
        Assert.That(items, Is.EqualTo(new[] { 0, 1, 2 }));
        Assert.That(reconnecting, Is.Empty);
        Assert.That(host.Probe.All, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task A_close_that_does_not_invite_the_client_back_is_final()
    {
        await using var host = await LabHost.StartAsync();

        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in host.Lab("alice", Reconnecting()).Listen("t"))
            {
            }
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);
        c.Context!.Close("kicked");

        var ex = Assert.ThrowsAsync<IonStreamClosedException>(async () => await consumer.WaitAsync(Wait));
        Assert.That(ex!.Reason, Is.EqualTo("kicked"));
        Assert.That(ex.AllowReconnect, Is.False);
        await Task.Delay(300);
        Assert.That(reconnecting, Is.Empty);
        Assert.That(host.Probe.All, Has.Count.EqualTo(1));
        Assert.That((await c.Disconnected.Task).Reason, Is.EqualTo(IonDisconnectReason.ServerClosed));
    }

    [Test]
    public async Task A_close_that_invites_the_client_back_starts_the_call_afresh()
    {
        await using var host = await LabHost.StartAsync();

        var got = new ConcurrentQueue<LabEvent>();
        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            await foreach (var e in host.Lab("alice", Reconnecting()).Listen("room", cts.Token))
                got.Enqueue(e);
        });

        var first = await host.Probe.SingleAsync();
        await first.StreamStarted.Task.WaitAsync(Wait);
        first.Context!.Close("rebalancing", allowReconnect: true);

        LabConnection? second = null;
        await Eventually.TrueAsync(() => (second = host.Probe.All.FirstOrDefault(x => x.ConnectionId != first.ConnectionId)) is not null,
            Wait, "a new connection");
        await second!.StreamStarted.Task.WaitAsync(Wait);
        await host.Connections.Group("room").SendAsync(new LabEvent(1, "room", "on the new one"));
        await Eventually.TrueAsync(() => got.Count == 1, Wait, "the push");

        Assert.Multiple(() =>
        {
            Assert.That(consumer.IsCompleted, Is.False, "the consumer never saw the close");
            Assert.That(reconnected.Single().Resumed, Is.False, "a closed session is not resumed");
            Assert.That(reconnecting.Single().Cause, Is.TypeOf<IonStreamClosedException>());
        });

        Assert.That((await first.Disconnected.Task).Reason, Is.EqualTo(IonDisconnectReason.ServerClosed));
        await cts.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await consumer.WaitAsync(Wait));
    }

    [Test]
    public async Task A_server_restart_is_ridden_out()
    {
        var port = LabHost.FreePort();
        var first = await LabHost.StartAsync(port: port);

        var got = new ConcurrentQueue<LabEvent>();
        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            await foreach (var e in Detached(first.BaseAddress, "alice").Listen("room", cts.Token))
                got.Enqueue(e);
        });

        var c = await first.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);
        await first.Connections.Group("room").SendAsync(new LabEvent(1, "room", "before the restart"));
        await Eventually.TrueAsync(() => got.Count == 1, Wait, "the first push");

        // A deployment: the server says "come back", goes away for a while, and a new one takes its place.
        await first.DisposeAsync();
        Assert.That((await c.Disconnected.Task).Reason, Is.EqualTo(IonDisconnectReason.ServerShutdown));
        await Eventually.TrueAsync(() => reconnecting.Count >= 2, Wait, "the client keeps trying while nobody listens");

        await using var second = await LabHost.StartAsync(port: port);
        var c2 = await second.Probe.SingleAsync(Wait);
        await c2.StreamStarted.Task.WaitAsync(Wait);
        await second.Connections.Group("room").SendAsync(new LabEvent(2, "room", "after the restart"));
        await Eventually.TrueAsync(() => got.Count == 2, Wait, "the push from the new server");

        Assert.Multiple(() =>
        {
            Assert.That(got.Select(e => e.body), Is.EqualTo(new[] { "before the restart", "after the restart" }));
            Assert.That(reconnected.Single().Resumed, Is.False, "a new server has no session to resume");
            Assert.That(consumer.IsCompleted, Is.False);
        });

        await cts.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await consumer.WaitAsync(Wait));
    }

    [Test]
    public async Task A_server_that_is_down_at_the_start_is_waited_for()
    {
        var port = LabHost.FreePort();
        var address = new Uri($"https://localhost:{port}");

        var items = new List<int>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var i in Detached(address, "early").Count(0, 5, 0))
                items.Add(i);
        });

        await Eventually.TrueAsync(() => reconnecting.Count >= 2, Wait, "retrying while there is no server");
        await using var host = await LabHost.StartAsync(port: port);
        await consumer.WaitAsync(Wait);

        Assert.That(items, Is.EqualTo(Enumerable.Range(0, 5)));
        Assert.That(reconnecting.Select(r => r.Attempt).Take(2), Is.EqualTo(new[] { 1, 2 }), "attempts are counted");
        Assert.That(reconnected, Is.Empty, "the first connection is not a reconnection");
    }

    [Test]
    public async Task MaxAttempts_bounds_the_retries_and_the_last_failure_is_thrown()
    {
        var address = new Uri($"https://localhost:{LabHost.FreePort()}");

        var ex = Assert.CatchAsync<Exception>(async () =>
        {
            await foreach (var _ in Detached(address, "nobody", o => o.Reconnect!.MaxAttempts = 3).Count(0, 1, 0))
            {
            }
        });

        Assert.That(ex, Is.InstanceOf<IonStreamDisconnectedException>().Or.InstanceOf<HttpRequestException>(), ex!.ToString());
        Assert.That(reconnecting.Select(r => r.Attempt), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public async Task Cancelling_while_reconnecting_ends_the_call_at_once_and_for_good()
    {
        await using var host = await LabHost.StartAsync();
        await using var proxy = FaultyProxy.Start(host.Port);

        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in host.LabVia(proxy.BaseAddress, "alice", Reconnecting()).Listen("t", cts.Token))
            {
            }
        });

        var c = await host.Probe.SingleAsync();
        await c.StreamStarted.Task.WaitAsync(Wait);

        proxy.Freeze();
        await Eventually.TrueAsync(() => !reconnecting.IsEmpty, Wait, "the client is reconnecting");

        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await consumer.WaitAsync(Wait));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(700)), "no waiting out a backoff or a connect");

        proxy.Thaw();
        var attempts = proxy.Connections;
        await Task.Delay(1000);
        Assert.That(proxy.Connections, Is.EqualTo(attempts), "and no attempt after it");
        Assert.That(host.Probe.All, Has.Count.EqualTo(1));
    }

    private sealed class UserHeader(string user) : IIonInterceptor
    {
        public Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
        {
            context.RequestItems[LabTickets.UserHeader] = user;
            return next(context, ct);
        }
    }
}
