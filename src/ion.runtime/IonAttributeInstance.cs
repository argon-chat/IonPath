namespace ion.runtime;

public record IonAttributeInstance(string name, List<object> arguments)
{
    public bool IsBuiltinAttribute => name.Equals("builtin");
    public bool IsScalarAttribute => name.Equals("scalar");
    public bool IsTag => name.Equals("tag");

    public static implicit operator IonAttributeInstance(string value) => new(value, []);
}

public record IonBuiltinAttributeInstance() : IonAttributeInstance("builtin", []);
public record IonScalarAttributeInstance() : IonAttributeInstance("scalar", []);
public record IonTagAttributeInstance(int tag) : IonAttributeInstance("tag", [tag]);


public record IonAttributeType(string name, List<IonType> arguments) : IonBase(name, []);