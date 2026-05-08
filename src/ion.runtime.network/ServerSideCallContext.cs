namespace ion.runtime.network;

using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

public class ServerSideCallContext(AsyncServiceScope scope, Type @interface, MethodInfo @method) : IIonCallContext
{
    public Type InterfaceName { get; } = @interface;
    public MethodInfo MethodName { get; } = @method;

    public IDictionary<string, string> RequestItems { get; } =
        new Dictionary<string, string>([], StringComparer.InvariantCultureIgnoreCase);

    public IDictionary<string, string> ResponseItems { get; } =
        new Dictionary<string, string>([], StringComparer.InvariantCultureIgnoreCase);

    public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
    public IServiceProvider ServiceProvider => scope.ServiceProvider;

    /// <summary>
    /// Client session identifier. Persists across all requests from a single client instance/tab.
    /// </summary>
    public string? SessionId
    {
        get => RequestItems.TryGetValue(IonCorrelationHeaders.SessionId, out var v) ? v : null;
        set
        {
            if (value is not null)
                RequestItems[IonCorrelationHeaders.SessionId] = value;
        }
    }

    /// <summary>
    /// Correlation identifier for a logical operation/flow (group of related requests).
    /// </summary>
    public string CorrelationId
    {
        get => RequestItems.TryGetValue(IonCorrelationHeaders.CorrelationId, out var v) ? v : string.Empty;
        set => RequestItems[IonCorrelationHeaders.CorrelationId] = value;
    }

    public void Dispose() => Stopwatch.Stop();
}