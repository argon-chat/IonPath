namespace ion.runtime.network;

public record IonTransportOptions
{
    public Dictionary<Type, Type> Services { get; } = new();
    public List<Type> Interceptors { get; } = new();
}