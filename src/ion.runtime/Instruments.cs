namespace ion.runtime;

using System.Diagnostics.Metrics;

public static class Instruments
{
    public static readonly Meter Meter = new("Ion");
}

/// <summary>
/// Contains all OpenTelemetry metric names used in Ion.
/// </summary>
/// <remarks>
/// <para><strong>Naming Convention:</strong></para>
/// <list type="bullet">
///   <item>All metric names start with <c>ion-</c> prefix</item>
///   <item>Use lowercase with hyphens for word separation (kebab-case)</item>
///   <item>Format: <c>ion-{feature}-{metric-name}</c></item>
///   <item>Example: <c>ion-redis-connections-allocated</c></item>
/// </list>
/// <para><strong>Metric Types:</strong></para>
/// <list type="bullet">
///   <item><strong>Counter:</strong> Monotonically increasing values (e.g., total requests)</item>
///   <item><strong>Gauge:</strong> Values that can go up and down (e.g., current connections)</item>
///   <item><strong>Histogram:</strong> Distribution of values (e.g., operation duration)</item>
/// </list>
/// </remarks>
public static class InstrumentNames
{
    public const string RpcRequestTotal = "ion-rpc-request-total";
    public const string RpcRequestDuration = "ion-rpc-request-duration";
    public const string RpcActiveConnections = "ion-rpc-active-connections";
    public const string RpcRequestError = "ion-rpc-request-error";
}

/// <remarks>
/// All instruments use the shared <see cref="Instruments.Meter"/> and reference
/// metric names defined in <see cref="InstrumentNames"/>.
/// </remarks>
public static class IonInstruments
{
    private static readonly Counter<long> RpcRequestTotalCounter = Instruments.Meter.CreateCounter<long>(
        InstrumentNames.RpcRequestTotal,
        description: "Total number of RPC requests");

    private static readonly Histogram<double> RpcRequestDurationHistogram = Instruments.Meter.CreateHistogram<double>(
        InstrumentNames.RpcRequestDuration,
        unit: "ms",
        description: "Duration of RPC requests in milliseconds");

    private static readonly UpDownCounter<int> RpcActiveConnectionsCounter = Instruments.Meter.CreateUpDownCounter<int>(
        InstrumentNames.RpcActiveConnections,
        description: "Number of active RPC connections");

    private static readonly Counter<long> RpcRequestErrorCounter = Instruments.Meter.CreateCounter<long>(
        InstrumentNames.RpcRequestError,
        description: "Total number of RPC request errors");

    public static void RecordRequest(string endpoint, string method, int statusCode)
    {
        RpcRequestTotalCounter.Add(1,
            new KeyValuePair<string, object?>("endpoint", endpoint),
            new KeyValuePair<string, object?>("method", method),
            new KeyValuePair<string, object?>("status_code", statusCode));
    }

    public static void RecordRequestDuration(string endpoint, string method, double durationMs)
    {
        RpcRequestDurationHistogram.Record(durationMs,
            new KeyValuePair<string, object?>("endpoint", endpoint),
            new KeyValuePair<string, object?>("method", method));
    }

    public static void RecordError(string endpoint, string method, string errorCode)
    {
        RpcRequestErrorCounter.Add(1,
            new KeyValuePair<string, object?>("endpoint", endpoint),
            new KeyValuePair<string, object?>("method", method),
            new KeyValuePair<string, object?>("error_code", errorCode));
    }

    public static void IncrementActiveConnections(string endpoint)
    {
        RpcActiveConnectionsCounter.Add(1,
            new KeyValuePair<string, object?>("endpoint", endpoint));
    }

    public static void DecrementActiveConnections(string endpoint)
    {
        RpcActiveConnectionsCounter.Add(-1,
            new KeyValuePair<string, object?>("endpoint", endpoint));
    }
}