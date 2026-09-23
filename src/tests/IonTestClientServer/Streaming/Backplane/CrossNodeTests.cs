namespace IonTestClientServer.Streaming.Backplane;

using System.Collections.Concurrent;
using ion.runtime;
using ion.runtime.client;
using ion.runtime.network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestContracts;

/// <summary>
/// Two real servers sharing a Redis-Streams backplane on Dragonfly: every
/// <see cref="IIonStreamConnections"/> operation issued on one node reaches the connections on the
/// other — the reason to have a backplane at all.
/// </summary>
[TestFixture(IonStreamTransportKind.WebSocket)]
[TestFixture(IonStreamTransportKind.WebTransport)]
[Parallelizable(ParallelScope.None)]
public class CrossNodeTests(IonStreamTransportKind transport)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private LabHost a = null!;
    private LabHost b = null!;
    private string streamKey = "";
    private readonly List<(CancellationTokenSource Cts, Task Run)> running = [];

    [SetUp]
    public async Task SetUp()
    {
        IonWsClient.ResetTransportHealth();
        var key = streamKey = $"ion:test:{Guid.NewGuid():N}";

        void Backplane(IServiceCollection s) => s.AddIonRedisStreamsBackplane(o =>
        {
            o.Configuration = DragonflyFixture.Configuration;
            o.StreamKey = key;
        });

        a = await LabHost.StartAsync(services: Backplane);
        b = await LabHost.StartAsync(services: Backplane);
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var (cts, run) in running)
        {
            await cts.CancelAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        }

        running.Clear();
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Test]
    public async Task A_backplane_outage_does_not_stop_local_delivery_and_the_node_catches_up_afterwards()
    {
        // Node C reaches Dragonfly through a proxy we can cut; A and B do not.
        var redisPort = int.Parse(DragonflyFixture.Configuration.Split(':')[^1]);
        await using var cut = FaultyProxy.Start(redisPort);
        await using var c = await LabHost.StartAsync(services: s => s.AddIonRedisStreamsBackplane(o =>
        {
            o.Configuration = $"127.0.0.1:{cut.Port},abortConnect=false,connectTimeout=1000,syncTimeout=2000,asyncTimeout=2000";
            o.StreamKey = streamKey;
        }));

        var onC = await ListenAsync(c, "news", "carol");
        var onB = await ListenAsync(b, "news", "bob");

        cut.Reset();
        cut.Freeze();

        // A push on C during the outage still reaches C's own member, and promptly — whatever the
        // backplane publish is doing meanwhile.
        var publishing = c.Connections.Group("news").SendAsync(new LabEvent(1, "news", "local during outage")).AsTask();
        await Eventually.TrueAsync(() => onC.Events.Count == 1, TimeSpan.FromSeconds(2), "local delivery did not wait for the backplane");
        Assert.DoesNotThrowAsync(async () => await publishing.WaitAsync(Wait), "a backplane failure never fails the caller's push");

        // B pushes while C cannot read the backplane.
        await b.Connections.Group("news").SendAsync(new LabEvent(2, "news", "from B during the outage"));
        await Eventually.TrueAsync(() => onB.Events.Any(e => e.seq == 2), Wait, "B's own member");
        await Task.Delay(500);
        Assert.That(onC.Events.Any(e => e.seq == 2), Is.False, "C cannot have it yet");

        // The link comes back: C's reader resumes from the last entry it saw and delivers what it missed.
        cut.Thaw();
        await Eventually.TrueAsync(() => onC.Events.Any(e => e.seq == 2), TimeSpan.FromSeconds(30),
            "C caught up on B's push from the stream after the outage");
    }

    [Test]
    public async Task A_service_without_a_web_server_reaches_clients_through_the_hub()
    {
        // A worker — a generic host, no Kestrel, no AddIonProtocol — sharing the backplane.
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddIonStreamHub()
            .AddIonRedisStreamsBackplane(o =>
            {
                o.Configuration = DragonflyFixture.Configuration;
                o.StreamKey = streamKey;
            });
        using var worker = builder.Build();
        await worker.StartAsync();
        var hub = worker.Services.GetRequiredService<IIonStreamConnections>();

        var listener = await ListenAsync(a, "spaces/42", "bob");
        var other = await ListenAsync(b, "spaces/42", "carol");

        Assert.That(await hub.Group("spaces/42").SendAsync(new LabEvent(1, "spaces/42", "from the worker")), Is.Zero,
            "nothing is local to the worker");
        await Eventually.TrueAsync(() => listener.Events.Count == 1 && other.Events.Count == 1, Wait, "both nodes delivered");

        await hub.User("bob").SendAsync(new LabEvent(2, "dm", "just bob"));
        await Eventually.TrueAsync(() => listener.Events.Count == 2, Wait, "bob's direct push");
        Assert.That(other.Events, Has.Count.EqualTo(1));

        await hub.User("bob").CloseAsync("session revoked");
        var ex = Assert.ThrowsAsync<IonStreamClosedException>(async () => await listener.Run.WaitAsync(Wait));
        Assert.That(ex!.Reason, Is.EqualTo("session revoked"));
        Assert.That((await listener.Server.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.ServerClosed));

        await worker.StopAsync();
    }

    private async Task<(ConcurrentQueue<LabEvent> Events, Task Run, LabConnection Server)> ListenAsync(LabHost node, string topic, string user)
    {
        var events = new ConcurrentQueue<LabEvent>();
        var cts = new CancellationTokenSource();
        var lab = node.Lab(user, null, transport);
        var run = Task.Run(async () =>
        {
            await foreach (var e in lab.Listen(topic, cts.Token))
                events.Enqueue(e);
        });
        running.Add((cts, run));

        var server = await node.Probe.ByUserAsync(user);
        await server.StreamStarted.Task.WaitAsync(Wait);
        return (events, run, server);
    }

    [Test]
    public async Task A_group_push_on_one_node_reaches_members_on_every_node_in_order()
    {
        var onA = await ListenAsync(a, "news", "alice");
        var onB = await ListenAsync(b, "news", "bob");

        const int count = 500;
        for (var i = 0; i < count; i++)
        {
            var local = await b.Connections.Group("news").SendAsync(new LabEvent(i, "news", "x"));
            Assert.That(local, Is.EqualTo(1), "the count is local: only bob is on node B");
        }

        await Eventually.TrueAsync(() => onA.Events.Count == count, Wait, $"alice on A got {onA.Events.Count}/{count}");
        await Eventually.TrueAsync(() => onB.Events.Count == count, Wait, $"bob on B got {onB.Events.Count}/{count}");
        Assert.That(onA.Events.Select(e => e.seq), Is.EqualTo(Enumerable.Range(0, count).Select(i => (long)i)), "order across the backplane");
    }

    [Test]
    public async Task A_user_push_reaches_every_device_of_that_user_across_nodes()
    {
        var phone = await ListenAsync(a, "t1", "bob");
        var laptop = await ListenAsync(b, "t2", "bob");
        var other = await ListenAsync(b, "t1", "carol");

        await a.Connections.User("bob").SendAsync(new LabEvent(1, "dm", "hi bob"));

        await Eventually.TrueAsync(() => phone.Events.Count == 1 && laptop.Events.Count == 1, Wait, "both of bob's devices");
        await Task.Delay(300);
        Assert.That(other.Events, Is.Empty);
    }

    [Test]
    public async Task Except_is_honoured_on_the_remote_node()
    {
        var excluded = await ListenAsync(a, "room", "alice");
        var included = await ListenAsync(a, "room", "ann");

        await b.Connections.Group("room").Except(excluded.Server.ConnectionId).SendAsync(new LabEvent(1, "room", "not alice"));

        await Eventually.TrueAsync(() => included.Events.Count == 1, Wait, "ann got it");
        await Task.Delay(300);
        Assert.That(excluded.Events, Is.Empty);
    }

    [Test]
    public async Task Closing_a_user_on_one_node_closes_their_connection_on_the_other()
    {
        var onA = await ListenAsync(a, "t", "mallory");

        var closedLocally = await b.Connections.User("mallory").CloseAsync("session revoked");
        Assert.That(closedLocally, Is.Zero, "nothing of mallory's lives on B");

        var ex = Assert.ThrowsAsync<IonStreamClosedException>(async () => await onA.Run.WaitAsync(Wait));
        Assert.That(ex!.Reason, Is.EqualTo("session revoked"));

        var info = await onA.Server.Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ServerClosed));
        Assert.That(info.Message, Is.EqualTo("session revoked"));
    }

    [Test]
    public async Task Aborting_a_connection_by_id_from_another_node()
    {
        var onA = await ListenAsync(a, "t", "eve");

        b.Connections.Client(onA.Server.ConnectionId).Abort("gone");

        Assert.ThrowsAsync<IonStreamDisconnectedException>(async () => await onA.Run.WaitAsync(Wait));
        var info = await onA.Server.Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ServerAborted));
    }

    [Test]
    public async Task A_connection_on_one_node_can_be_grouped_from_the_other()
    {
        var onA = await ListenAsync(a, "lobby", "vip-guest");

        await b.Connections.AddToGroupAsync(onA.Server.ConnectionId, "vip");
        await Eventually.TrueAsync(() => a.Connections.Group("vip").Connections.Count == 1, Wait, "joined on its own node");

        await b.Connections.Group("vip").SendAsync(new LabEvent(1, "vip", "welcome"));
        await Eventually.TrueAsync(() => onA.Events.Count == 1, Wait, "the remote group push arrived");

        await b.Connections.RemoveFromGroupAsync(onA.Server.ConnectionId, "vip");
        await Eventually.TrueAsync(() => a.Connections.Group("vip").Connections.Count == 0, Wait, "left on its own node");
    }

    [Test]
    public async Task A_union_case_pushed_on_one_node_is_re_encoded_for_the_union_stream_on_the_other()
    {
        var received = new ConcurrentQueue<ILabSignal>();
        var cts = new CancellationTokenSource();
        var lab = a.Lab("sig", null, transport);
        var run = Task.Run(async () =>
        {
            await foreach (var s in lab.Signals("hall", cts.Token))
                received.Enqueue(s);
        });
        running.Add((cts, run));
        await (await a.Probe.ByUserAsync("sig")).StreamStarted.Task.WaitAsync(Wait);

        // Encoded on B as a Joined; A must decode it as a Joined and re-encode it as an ILabSignal.
        await b.Connections.Group("room:hall").SendAsync(new Joined("zed"));
        await b.Connections.Group("room:hall").SendAsync<ILabSignal>(new Left("zed"));

        await Eventually.TrueAsync(() => received.Count == 2, Wait, "both signals crossed");
        Assert.That(received.ToArray(), Is.EqualTo(new ILabSignal[] { new Joined("zed"), new Left("zed") }));
    }
}
