namespace ion.runtime.network;

public record IonTransportOptions
{
    public Dictionary<Type, Type> Services { get; } = new();
}