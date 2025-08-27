namespace ion.runtime;

using ion.syntax;

public record IonAttributeInstance(IonIdentifier name, List<object> arguments)
{
    public bool IsBuiltinAttribute => name.Equals("builtin");
    public bool IsScalarAttribute => name.Equals("scalar");
    public bool IsTag => name.Equals("tag");
    public bool IsUnion => name.Equals("union");
    public bool IsUnionCase => name.Equals("unionCase");

    public static implicit operator IonAttributeInstance(string value) => new(value, []);
}

public record IonBuiltinAttributeInstance() : IonAttributeInstance("builtin", []);
public record IonScalarAttributeInstance() : IonAttributeInstance("scalar", []);
public record IonTagAttributeInstance(int tag) : IonAttributeInstance("tag", [tag]);
public record IonUnionAttributeInstance() : IonAttributeInstance("union", []);
public record IonUnionCaseAttributeInstance() : IonAttributeInstance("unionCase", []);
public record IonBitAttributeInstance(int bitCount) : IonAttributeInstance("bits", [bitCount]);

public static class NumberBitEx
{
    public static IonBitAttributeInstance Bits(this int bytesCount) => new(bytesCount * 8);
}


public record IonAttributeType(IonIdentifier name, List<IonArgument> arguments) : IonBase(name, []);