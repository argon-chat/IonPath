namespace IonTestClientServer.Streaming;

using ion.runtime;
using ion.runtime.client;
using ion.runtime.network;

/// <summary>
/// Hundreds of connections ending every way at once. Whatever the interleaving, every connection
/// that connected is disconnected exactly once, for the reason that ended it, and nothing is left
/// behind in the registry or its groups.
/// </summary>
[TestFixture(IonStreamTransportKind.WebSocket)]
[TestFixture(IonStreamTransportKind.WebTransport)]
[Parallelizable(ParallelScope.None)]
public class StreamChaosTests(IonStreamTransportKind transport)
{
    private enum Ending
    {
        Completes,
        ClientBreaks,
        ClientCancels,
        ServerCloses,
        ServerAborts,
        Explodes,
        Rejected
    }

    private static IonDisconnectReason Expected(Ending ending) => ending switch
    {
        Ending.Completes => IonDisconnectReason.Completed,
        Ending.ClientBreaks or Ending.ClientCancels => IonDisconnectReason.ClientClosed,
        Ending.ServerCloses => IonDisconnectReason.ServerClosed,
        Ending.ServerAborts => IonDisconnectReason.ServerAborted,
        Ending.Explodes => IonDisconnectReason.Faulted,
        _ => IonDisconnectReason.Rejected
    };

    [Test]
    [TestCase(1, 200)]
    [TestCase(2, 200)]
    [TestCase(3, 200)]
    public async Task Every_connection_ends_exactly_once_whatever_the_interleaving(int seed, int connections)
    {
        await using var host = await LabHost.StartAsync(o => o.OutboundQueueCapacity = 10_000);
        var random = new Random(seed);
        var plan = Enumerable.Range(0, connections)
            .Select(i => (Ending)random.Next(7))
            .Select((ending, i) => (User: ending == Ending.Rejected ? $"banned-{i}" : $"u{i}", Ending: ending))
            .ToArray();

        var durations = new System.Collections.Concurrent.ConcurrentDictionary<string, TimeSpan>();
        var runs = plan.Select(p => Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { return await RunAsync(host, p.User, p.Ending); }
            finally { durations[$"{p.User}:{p.Ending}"] = sw.Elapsed; }
        })).ToArray();
        var all = Task.WhenAll(runs);
        if (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(90))) != all)
        {
            var stuck = plan.Select((p, i) => (p, run: runs[i])).Where(x => !x.run.IsCompleted).Select(x =>
            {
                var c = host.Probe.Find(host.Probe.All.FirstOrDefault(s => s.User == x.p.User)?.ConnectionId ?? "");
                return $"{x.p.User}:{x.p.Ending} server[{(c is null ? "never connected" : $"trace={string.Join(">", c.Trace)} disc={c.Context?.Disconnect} state={c.Context?.State}")}]";
            });
            Assert.Fail("stuck calls: " + string.Join("; ", stuck));
        }

        var outcomes = await all;

        foreach (var (call, took) in durations.OrderByDescending(d => d.Value).Take(5))
            TestContext.Out.WriteLine($"slowest: {call} {took.TotalMilliseconds:F0} ms");

        Assert.That(outcomes.Where(o => o is not null), Is.Empty, "client-side outcomes: " + string.Join("; ", outcomes.Where(o => o is not null)));

        var drain = System.Diagnostics.Stopwatch.StartNew();
        await Eventually.TrueAsync(() => host.Connections.Count == 0 || drain.Elapsed > TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), "");
        if (host.Connections.Count > 0)
        {
            foreach (var lingering in host.Connections.All.Connections)
            {
                var c = host.Probe.Find(lingering.ConnectionId);
                TestContext.Out.WriteLine($"lingering: {c?.User} {lingering.TransportName} state={lingering.State} disc={lingering.Disconnect} trace={string.Join(">", c?.Trace ?? new())}");
            }
        }

        await Eventually.TrueAsync(() => host.Connections.Count == 0, TimeSpan.FromSeconds(60), $"registry drained ({host.Connections.Count} left)");
        Assert.That(drain.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), "the registry drained promptly");

        foreach (var (user, ending) in plan)
        {
            var c = await host.Probe.ByUserAsync(user);
            var expected = Expected(ending);
            await Eventually.TrueAsync(() => c.GlobalDisconnectCalls >= 1, TimeSpan.FromSeconds(10), $"{user} global disconnect");

            Assert.Multiple(() =>
            {
                Assert.That(c.GlobalDisconnectCalls, Is.EqualTo(1), $"{user}: global disconnects");
                Assert.That(c.Context!.Disconnect!.Reason, Is.EqualTo(expected), $"{user} ({ending})");
                if (ending != Ending.Rejected)
                {
                    Assert.That(c.DisconnectCalls, Is.EqualTo(1), $"{user}: service disconnects");
                    Assert.That(c.StreamStoppedBeforeDisconnect, Is.True, $"{user}: stream stopped first");
                }
                else
                {
                    Assert.That(c.DisconnectCalls, Is.Zero, $"{user}: a refusing hook is not disconnected");
                }
            });
        }

        Assert.That(host.Connections.Group("chaos").Connections, Is.Empty);
    }

    /// <summary>Runs one connection to the planned ending; returns a description of anything unexpected.</summary>
    private async Task<string?> RunAsync(LabHost host, string user, Ending ending)
    {
        var lab = host.Lab(user, null, transport);

        try
        {
            switch (ending)
            {
                case Ending.Completes:
                {
                    var n = 0;
                    await foreach (var _ in lab.Count(0, 50, 0))
                        n++;
                    return n == 50 ? null : $"{user}: {n}/50 items";
                }
                case Ending.ClientBreaks:
                {
                    await foreach (var _ in lab.Count(0, int.MaxValue, 1))
                        break;
                    return null;
                }
                case Ending.ClientCancels:
                {
                    // Cancelled once the call is running — a cancellation before it connects never
                    // reaches the server, and is covered by the transport tests.
                    using var cts = new CancellationTokenSource();
                    var delay = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100));
                    var armed = false;
                    try
                    {
                        await foreach (var _ in lab.Count(0, int.MaxValue, 1, cts.Token))
                        {
                            // Once: CancelAfter restarts the timer, and items come faster than it runs.
                            if (armed) continue;
                            armed = true;
                            cts.CancelAfter(delay);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }

                    return $"{user}: cancellation did not surface";
                }
                case Ending.ServerCloses:
                case Ending.ServerAborts:
                {
                    var consuming = Task.Run(async () =>
                    {
                        await foreach (var _ in lab.Listen("chaos"))
                        {
                        }
                    });

                    var c = await host.Probe.ByUserAsync(user);
                    await c.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));

                    if (ending == Ending.ServerCloses)
                        await host.Connections.User(user).CloseAsync("bye");
                    else
                        host.Connections.User(user).Abort("die");

                    try
                    {
                        await consuming.WaitAsync(TimeSpan.FromSeconds(20));
                        return $"{user}: completed normally after a server {ending}";
                    }
                    catch (IonStreamClosedException) when (ending == Ending.ServerCloses)
                    {
                        return null;
                    }
                    catch (IonStreamDisconnectedException) when (ending == Ending.ServerAborts)
                    {
                        return null;
                    }
                }
                case Ending.Explodes:
                {
                    try
                    {
                        await foreach (var _ in lab.Explode(5))
                        {
                        }
                    }
                    catch (IonRequestException ex) when (ex is not IonStreamDisconnectedException)
                    {
                        return null;
                    }

                    return $"{user}: explosion did not surface";
                }
                case Ending.Rejected:
                {
                    try
                    {
                        await foreach (var _ in lab.Count(0, 5, 0))
                        {
                        }
                    }
                    catch (IonRequestException ex) when (ex.Error.code == "BANNED")
                    {
                        return null;
                    }

                    return $"{user}: rejection did not surface";
                }
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            return $"{user} ({ending}): {ex.GetType().Name}: {ex.Message}";
        }
    }
}
