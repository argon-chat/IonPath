namespace ion.runtime;

using ion.syntax;

public record IonAttributeInstance(IonIdentifier name, List<object> arguments)
{
    public bool IsBuiltinAttribute => name.Equals("builtin");
    public bool IsScalarAttribute => name.Equals("scalar");
    public bool IsTag => name.Equals("tag");

    public static implicit operator IonAttributeInstance(string value) => new(value, []);
}

public record IonBuiltinAttributeInstance() : IonAttributeInstance("builtin", []);
public record IonScalarAttributeInstance() : IonAttributeInstance("scalar", []);
public record IonTagAttributeInstance(int tag) : IonAttributeInstance("tag", [tag]);


public record IonAttributeType(IonIdentifier name, List<IonType> arguments) : IonBase(name, []);

public record IonEnumType(IonIdentifier name, 
    IonType baseType, 
    IReadOnlyDictionary<string, string> kv, 
    bool isFlags, IReadOnlyList<IonAttributeInstance> attributes) : IonBase(name, attributes);