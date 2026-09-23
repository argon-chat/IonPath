namespace IonTestClientServer.Streaming;

using System.Diagnostics;
using System.Reflection;
using ion.runtime.client;
using ion.runtime.network;
using TestContracts;

/// <summary>
/// An allocation budget for the steady state of a stream: server and client together, over a real
/// socket, per item. It guards the zero-copy paths — a regression to an allocation per frame on
/// either side shows up here long before it shows up in a profiler.
/// </summary>
[TestFixture(IonStreamTransportKind.WebSocket)]
[TestFixture(IonStreamTransportKind.WebTransport)]
[Parallelizable(ParallelScope.None)]
[NonParallelizable]
public class StreamAllocationTests(IonStreamTransportKind transport)
{
    /// <summary>
    /// What one <c>i4</c> item may cost end to end, server and client together. Measured on a
    /// Release build: 38 B over WebSocket, of which 32 B is the item's own payload array on the
    /// client — the one allocation the design keeps, because a decoded value may alias it — and
    /// 58 B over WebTransport, the difference being System.Net.Quic's own. One more allocation per
    /// item anywhere in the path (a state-machine box is 70–130 B) breaks the budget.
    /// </summary>
    private const double BudgetBytesPerItem = 100;

    /// <param name="resumable">
    /// A resumable session on top: each item is also copied into the server's replay buffer (a
    /// pooled array, returned on acknowledgement) and acknowledged in batches — which must cost no
    /// more than the budget either.
    /// </param>
    [Test]
    public async Task A_steady_stream_stays_within_its_allocation_budget([Values] bool resumable)
    {
        // A Debug build compiles every async state machine as a class, allocated per call whatever
        // the method builder says — the number would measure the compiler, not the code.
        foreach (var assembly in new[] { typeof(IonClient).Assembly, typeof(RpcEndpoints).Assembly, typeof(IIonStreamConnections).Assembly })
            if (assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true)
                Assert.Ignore($"{assembly.GetName().Name} is a Debug build; run with -c Release to measure allocations.");

        await using var host = await LabHost.StartAsync();
        var lab = host.Lab("alloc", resumable ? LabHost.Reconnecting() : null, transport);

        // Warm up: JIT, pools, the socket, the formatter caches.
        await DrainAsync(lab, 20_000);

        const int items = 200_000;
        using var profile = new AllocationProfile();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        profile.Start();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        await DrainAsync(lab, items);

        var perItem = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)items;
        profile.Stop();
        TestContext.Out.WriteLine($"{transport}{(resumable ? ", resumable" : "")}: {perItem:F1} bytes per item, server and client together");
        foreach (var (type, bytes) in profile.Top(15))
            TestContext.Out.WriteLine($"  {bytes / (double)items,7:F1} B/item  {type}");

        Assert.That(perItem, Is.LessThan(BudgetBytesPerItem));
    }

    private static async Task DrainAsync(IStreamLab lab, int count)
    {
        var sum = 0L;
        // ConfigureAwait(false): the test runner's synchronization context is not what is measured.
        await foreach (var i in lab.Count(0, count, 0).ConfigureAwait(false))
            sum += i;
        Assert.That(sum, Is.EqualTo((long)count * (count - 1) / 2));
    }
}
