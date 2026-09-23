namespace IonTestClientServer.Streaming;

using System.Collections.Concurrent;
using ion.runtime;
using ion.runtime.client;
using ion.runtime.network;
using TestContracts;

/// <summary>
/// The SignalR-shaped surface: groups, users, exclusions, typed pushes, and what happens to a
/// connection that cannot keep up — on both transports.
/// </summary>
[TestFixture(IonStreamTransportKind.WebSocket)]
[TestFixture(IonStreamTransportKind.WebTransport)]
[Parallelizable(ParallelScope.None)]
public class StreamPushTests(IonStreamTransportKind transport)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private LabHost host = null!;
    private readonly List<Listener> listeners = [];

    [SetUp]
    public async Task SetUp()
    {
        IonWsClient.ResetTransportHealth();
        host = await LabHost.StartAsync(o => o.OutboundQueueCapacity = 4096);
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var l in listeners)
            await l.DisposeAsync();
        listeners.Clear();
        await host.DisposeAsync();
    }

    /// <summary>A client listening to a topic in the background, recording what arrives.</summary>
    private sealed class Listener : IAsyncDisposable
    {
        public readonly ConcurrentQueue<LabEvent> Events = new();
        public readonly CancellationTokenSource Cts = new();
        public Task Run = Task.CompletedTask;
        public SemaphoreSlim? Gate;
        public string User = "";

        public async ValueTask DisposeAsync()
        {
            await Cts.CancelAsync();
            Gate?.Release(int.MaxValue / 2);
            try { await Run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) { }
        }
    }

    private async Task<Listener> ListenAsync(string topic, string user, SemaphoreSlim? gate = null)
    {
        var listener = new Listener { Gate = gate, User = user };
        var lab = host.Lab(user, null, transport);
        listener.Run = Task.Run(async () =>
        {
            await foreach (var e in lab.Listen(topic, listener.Cts.Token))
            {
                listener.Events.Enqueue(e);
                if (gate is not null)
                    await gate.WaitAsync(listener.Cts.Token);
            }
        });
        listeners.Add(listener);

        var c = await host.Probe.ByUserAsync(user);
        await c.StreamStarted.Task.WaitAsync(Wait);
        return listener;
    }

    [Test]
    public async Task A_group_push_reaches_every_member_and_nobody_else()
    {
        var a = await ListenAsync("g1", "a");
        var b = await ListenAsync("g1", "b");
        var c = await ListenAsync("g1", "c");
        var outsider = await ListenAsync("g2", "d");

        var queued = await host.Connections.Group("g1").SendAsync(new LabEvent(1, "g1", "hello"));
        Assert.That(queued, Is.EqualTo(3));

        foreach (var l in new[] { a, b, c })
            await Eventually.TrueAsync(() => l.Events.Count == 1, Wait, $"{l.User} got the push");

        await Task.Delay(200);
        Assert.That(outsider.Events, Is.Empty, "a member of another group got nothing");
        Assert.That(a.Events.Single(), Is.EqualTo(new LabEvent(1, "g1", "hello")));
    }

    [Test]
    public async Task Except_leaves_the_named_connections_out()
    {
        var a = await ListenAsync("room", "a");
        var b = await ListenAsync("room", "b");
        var aId = (await host.Probe.ByUserAsync("a")).ConnectionId;

        var queued = await host.Connections.Group("room").Except(aId).SendAsync(new LabEvent(7, "room", "not for a"));

        Assert.That(queued, Is.EqualTo(1));
        await Eventually.TrueAsync(() => b.Events.Count == 1, Wait, "b got it");
        await Task.Delay(200);
        Assert.That(a.Events, Is.Empty);
    }

    [Test]
    public async Task A_user_push_reaches_every_device_of_that_user()
    {
        var phone = await ListenAsync("t1", "bob");
        var laptop = await ListenAsync("t2", "bob");
        var other = await ListenAsync("t1", "carol");

        var queued = await host.Connections.User("bob").SendAsync(new LabEvent(1, "dm", "hi bob"));

        Assert.That(queued, Is.EqualTo(2));
        await Eventually.TrueAsync(() => phone.Events.Count == 1 && laptop.Events.Count == 1, Wait, "both devices");
        await Task.Delay(200);
        Assert.That(other.Events, Is.Empty);
    }

    [Test]
    public async Task A_push_to_a_single_connection_through_its_context()
    {
        var a = await ListenAsync("x", "a");
        var ctx = host.Connections.Find((await host.Probe.ByUserAsync("a")).ConnectionId)!;

        Assert.That(await ctx.SendAsync(new LabEvent(42, "x", "direct")), Is.True);
        await Eventually.TrueAsync(() => a.Events.Count == 1, Wait, "direct push");

        Assert.ThrowsAsync<ArgumentException>(async () => await ctx.SendAsync("a string is not a LabEvent"));
    }

    [Test]
    public async Task A_union_case_pushed_on_its_own_arrives_with_the_union_envelope()
    {
        var received = new ConcurrentQueue<ILabSignal>();
        using var cts = new CancellationTokenSource();
        var lab = host.Lab("a", null, transport);
        var run = Task.Run(async () =>
        {
            await foreach (var s in lab.Signals("lobby", cts.Token))
                received.Enqueue(s);
        });

        var c = await host.Probe.ByUserAsync("a");
        await c.StreamStarted.Task.WaitAsync(Wait);

        var group = host.Connections.Group("room:lobby");
        Assert.That(await group.SendAsync(new Joined("ann")), Is.EqualTo(1), "a case type is accepted by a stream of the union");
        Assert.That(await group.SendAsync<ILabSignal>(new Said("ann", "hi")), Is.EqualTo(1));
        Assert.That(await group.SendAsync(new LabEvent(1, "x", "y")), Is.Zero, "a LabEvent does not fit a LabSignal stream");

        await Eventually.TrueAsync(() => received.Count == 2, Wait, "both signals");
        Assert.That(received.ToArray(), Is.EqualTo(new ILabSignal[] { new Joined("ann"), new Said("ann", "hi") }));

        await cts.CancelAsync();
        try { await run.WaitAsync(Wait); } catch (OperationCanceledException) { }
    }

    [Test]
    public async Task Pushes_from_concurrent_publishers_keep_per_publisher_order_for_every_listener()
    {
        const int listenerCount = 40, publishers = 4, perPublisher = 250;

        var all = new List<Listener>();
        for (var i = 0; i < listenerCount; i++)
            all.Add(await ListenAsync("storm", $"l{i}"));

        var group = host.Connections.Group("storm");
        await Task.WhenAll(Enumerable.Range(0, publishers).Select(p => Task.Run(async () =>
        {
            for (var i = 0; i < perPublisher; i++)
            {
                Assert.That(await group.SendAsync(new LabEvent(p * 1_000_000 + i, "storm", $"p{p}")), Is.EqualTo(listenerCount));
                if (i % 25 == 0)
                    await Task.Yield();
            }
        })));

        foreach (var l in all)
        {
            await Eventually.TrueAsync(() => l.Events.Count == publishers * perPublisher, Wait,
                $"{l.User} received {l.Events.Count}/{publishers * perPublisher}");

            foreach (var byPublisher in l.Events.GroupBy(e => e.body))
                Assert.That(byPublisher.Select(e => e.seq), Is.Ordered, $"{l.User}: order of {byPublisher.Key}");
        }
    }

    [Test]
    public async Task A_consumer_that_stops_reading_is_dropped_as_slow_and_nobody_else_suffers()
    {
        await host.DisposeAsync();
        host = await LabHost.StartAsync(o => o.OutboundQueueCapacity = 32);

        var stuck = await ListenAsync("flood", "stuck", gate: new SemaphoreSlim(0));
        var healthy = await ListenAsync("flood", "healthy");
        var stuckConnection = await host.Probe.ByUserAsync("stuck");

        var body = new string('x', 2048);
        var group = host.Connections.Group("flood");
        var sent = 0;

        // Rounds paced by the healthy listener: it always keeps up, the stuck one never reads.
        while (!stuckConnection.Disconnected.Task.IsCompleted && sent < 200_000)
        {
            for (var i = 0; i < 16; i++)
                await group.SendAsync(new LabEvent(sent++, "flood", body));

            var target = sent;
            await Eventually.TrueAsync(() => healthy.Events.Count >= target || stuckConnection.Disconnected.Task.IsCompleted,
                Wait, "healthy listener keeps up");
        }

        var info = await stuckConnection.Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.SlowConsumer));
        Assert.That(info.Exception, Is.TypeOf<IonStreamSlowConsumerException>());

        // The healthy one is still connected, catches up on everything, and keeps receiving.
        var all = sent;
        await Eventually.TrueAsync(() => healthy.Events.Count == all, Wait, $"healthy listener caught up ({healthy.Events.Count}/{all})");
        await group.SendAsync(new LabEvent(sent++, "flood", "after"));
        await Eventually.TrueAsync(() => healthy.Events.Count == all + 1, Wait, "healthy listener unaffected");
        Assert.That(healthy.Events.Select(e => e.seq), Is.Ordered);
        Assert.That(host.Connections.Group("flood").Connections, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Group_membership_stays_consistent_under_concurrent_churn()
    {
        const int connectionCount = 24, groupCount = 3;

        var all = new List<Listener>();
        for (var i = 0; i < connectionCount; i++)
            all.Add(await ListenAsync("base", $"c{i}"));

        var contexts = host.Probe.All.Select(c => c.Context!).ToArray();
        var leaving = contexts.Take(6).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Eight threads join and leave three groups as fast as they can — few groups, so emptying
        // and re-creating a group races with joins into it constantly — while six connections go away.
        var churn = Enumerable.Range(0, 8).Select(t => Task.Run(async () =>
        {
            var random = new Random(t);
            while (!stop.IsCancellationRequested)
            {
                var ctx = contexts[random.Next(contexts.Length)];
                var group = $"churn-{random.Next(groupCount)}";
                if (random.Next(2) == 0) await ctx.AddToGroupAsync(group);
                else await ctx.RemoveFromGroupAsync(group);
            }
        })).ToArray();

        await Task.Delay(500);
        foreach (var ctx in leaving)
            ctx.Abort("leaving mid-churn");

        await Task.WhenAll(churn);
        await Eventually.TrueAsync(() => host.Connections.Count == connectionCount - leaving.Length, Wait, "the leavers are gone");

        for (var g = 0; g < groupCount; g++)
        {
            var group = $"churn-{g}";
            var indexed = host.Connections.Group(group).Connections.Select(c => c.ConnectionId).ToHashSet();
            var claimed = contexts.Where(c => c.State != IonStreamState.Closed && c.Groups.Contains(group))
                .Select(c => c.ConnectionId).ToHashSet();

            Assert.That(indexed, Is.EquivalentTo(claimed), $"{group}: the index and the connections agree");
            Assert.That(indexed.Intersect(leaving.Select(l => l.ConnectionId)), Is.Empty, $"{group}: no departed member");
        }
    }

    [Test]
    public async Task Groups_do_not_leak_members_once_connections_are_gone()
    {
        for (var round = 0; round < 3; round++)
        {
            var batch = new List<Listener>();
            for (var i = 0; i < 10; i++)
                batch.Add(await ListenAsync($"topic-{i % 3}", $"r{round}-{i}"));

            Assert.That(host.Connections.Count, Is.EqualTo(10));

            foreach (var l in batch)
            {
                await l.DisposeAsync();
                listeners.Remove(l);
            }

            await Eventually.TrueAsync(() => host.Connections.Count == 0, Wait, "all connections gone");
            for (var t = 0; t < 3; t++)
                Assert.That(host.Connections.Group($"topic-{t}").Connections, Is.Empty, $"topic-{t} emptied");
        }

        // A context outliving its connection cannot rejoin a group.
        var ended = host.Probe.All.First().Context!;
        await ended.AddToGroupAsync("zombie");
        Assert.That(host.Connections.Group("zombie").Connections, Is.Empty);
        Assert.That(await host.Connections.Group("topic-0").SendAsync(new LabEvent(0, "t", "nobody")), Is.Zero);
    }
}
