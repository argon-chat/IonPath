namespace ion.runtime.network;

public record IonTransportOptions
{
    public Dictionary<Type, Type> Services { get; } = new();
    public List<Type> Interceptors { get; } = new();
    public IonWebSocketOptions WebSocketOptions { get; set; } = new();

    /// <summary>
    /// Maps a service interface type to the port it is exclusively bound to.
    /// Services not present in this dictionary are accessible on any port.
    /// </summary>
    public Dictionary<Type, int> PortBindings { get; } = new();

    /// <summary>
    /// Maps a port to a list of interceptor types that only apply on that port.
    /// </summary>
    public Dictionary<int, List<Type>> PortInterceptors { get; } = new();

    /// <summary>
    /// Ports where global interceptors are excluded. Only port-specific interceptors will run.
    /// </summary>
    public HashSet<int> ExcludeGlobalInterceptorPorts { get; } = [];

    /// <summary>
    /// When true, unhandled exception details (stack trace, type, message) are included in error responses.
    /// Should be false in production.
    /// </summary>
    public bool DetailedErrors { get; set; } = false;

    /// <summary>
    /// When true, if a request does not contain an X-Ion-Correlation-Id header,
    /// the server will generate one automatically.
    /// </summary>
    public bool GenerateCorrelationIdIfMissing { get; set; } = true;
}

public enum IonWebSocketAuthFlow
{
    SubProtocol,
    Query,
}

public record IonWebSocketOptions
{
    public IonWebSocketAuthFlow Flow { get; set; }
    public Type TicketExchangeHandle { get; set; }
}

/// <summary>
/// Collects port bindings at registration time so they can be applied to Kestrel before the host starts.
/// </summary>
public sealed class IonPortBindingRegistry
{
    internal HashSet<int> Ports { get; } = [];

    internal void Add(int port) => Ports.Add(port);
}