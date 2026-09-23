namespace IonTestClientServer.Streaming;

using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

/// <summary>
/// Samples the runtime's allocation ticks (one event per ~100 KB allocated, tagged with the type
/// that crossed the line), so an allocation budget failure can say what is being allocated.
/// </summary>
public sealed class AllocationProfile : EventListener
{
    private readonly ConcurrentDictionary<string, long> bytesByType = new();
    private volatile bool recording;

    public void Start() => recording = true;

    public void Stop() => recording = false;

    public IEnumerable<(string Type, long Bytes)> Top(int count)
        => bytesByType.OrderByDescending(p => p.Value).Take(count).Select(p => (p.Key, p.Value));

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1 /* GC */);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!recording || !e.EventName!.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null)
            return;

        var nameIndex = e.PayloadNames!.IndexOf("TypeName");
        var amountIndex = e.PayloadNames.IndexOf("AllocationAmount64");
        if (nameIndex < 0 || amountIndex < 0)
            return;

        var type = e.Payload[nameIndex] as string ?? "?";
        var amount = Convert.ToInt64(e.Payload[amountIndex]);
        bytesByType.AddOrUpdate(type, amount, (_, v) => v + amount);
    }
}
