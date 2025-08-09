namespace ion.runtime;

[AttributeUsage(AttributeTargets.Method)]
public sealed class DeadlineAttribute(uint seconds) : Attribute
{
    public uint Seconds { get; } = seconds;
}