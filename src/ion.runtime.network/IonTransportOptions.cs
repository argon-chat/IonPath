namespace ion.runtime.network;

public record IonTransportOptions
{
    public Dictionary<Type, Type> Services { get; } = new();
    public List<Type> Interceptors { get; } = new();
    public IonWebSocketOptions WebSocketOptions { get; set; } = new();
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