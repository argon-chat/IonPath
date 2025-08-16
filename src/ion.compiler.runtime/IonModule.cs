namespace ion.runtime;

using syntax;

public sealed class IonModule
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required List<IonType> Definitions { get; init; }
    public required List<IonService> Services { get; init; }
    public required IReadOnlyList<IonFeature> Features { get; init; }
    public required IReadOnlyList<IonAttributeType> Attributes { get; init; }
    public required IReadOnlyList<string> Imports { get; init; }
    public IonFileSyntax? Syntax { get; init; } = null;


    public static readonly Lazy<IonModule> GetStdModule = new(() => new IonModule
    {
        Name = "std",
        Path = "ion://std",
        Features = ["builtin"],
        Definitions =
        [
            new("void", ["builtin"], [], true),

            new("bool", ["scalar", "builtin"], [], true),

            new("i1", ["scalar", "builtin"], [], true),
            new("i2", ["scalar", "builtin"], [], true),
            new("i4", ["scalar", "builtin"], [], true),
            new("i8", ["scalar", "builtin"], [], true),
            new("i16", ["scalar", "builtin"], [], true),

            new("u1", ["scalar", "builtin"], [], true),
            new("u2", ["scalar", "builtin"], [], true),
            new("u4", ["scalar", "builtin"], [], true),
            new("u8", ["scalar", "builtin"], [], true),
            new("u16", ["scalar", "builtin"], [], true),

            new("f2", ["scalar", "builtin"], [], true),
            new("f4", ["scalar", "builtin"], [], true),
            new("f8", ["scalar", "builtin"], [], true),

            new("bigint", ["builtin"], [], true),

            new("guid", ["builtin"], [], true),
            new("string", ["builtin"], [], true),
            new("datetime", ["builtin"], [], true),
            new("dateonly", ["builtin"], [], true),
            new("timeonly", ["builtin"], [], true),
            new("uri", ["builtin"], [], true),
            new("duration", ["scalar", "builtin"], [], true),

            new IonGenericType("Maybe", ["builtin"], [], ["T"], []),
            new IonGenericType("Array", ["builtin"], [], ["T"], []),
        ],
        Attributes =
        [
            new("builtin", []),
            new("scalar", []),
            new("tag", [new IonType("i4", ["scalar", "builtin"], [], true)]),
            new("deadline", [new IonType("i4", ["scalar", "builtin"], [], true)]),
            new("deprecated", []),
            new("internal", [])
        ],
        Imports = [],
        Services = []
    });

    public static readonly Lazy<IonModule> GetVectorModule = new(() => new IonModule
    {
        Name = "vector",
        Path = "ion://vector",
        Features = ["builtin"],
        Definitions =
        [
            new("vec2f", ["builtin"], [], true),
            new("vec3f", ["builtin"], [], true),
            new("vec4f", ["builtin"], [], true),

            new("vec2d", ["builtin"], [], true),
            new("vec3d", ["builtin"], [], true),
            new("vec4d", ["builtin"], [], true),

            new("vec2h", ["builtin"], [], true),
            new("vec3h", ["builtin"], [], true),
            new("vec4h", ["builtin"], [], true),
        ],
        Attributes = [],
        Imports = [],
        Services = []
    });

    public static readonly Lazy<IonModule> GetOrleansModule = new(() => new IonModule
    {
        Name = "orleans",
        Path = "ion://orleans",
        Features = ["builtin"],
        Definitions = [],
        Attributes =
        [
            new("grainId", []),
            new("oneWay", [])
        ],
        Imports = [],
        Services = []
    });
}

public interface ITypeWithName
{
    IonType Type { get; }
    IonIdentifier Name { get; }
}

public record IonBase(IonIdentifier name, IReadOnlyList<IonAttributeInstance> attributes);

public record IonField(
    IonIdentifier name,
    IonType type,
    IReadOnlyList<IonAttributeInstance> attributes) : IonBase(name, attributes), ITypeWithName
{
    public IonType Type => type;
    public IonIdentifier Name => name;
}

public record IonConstant(
    IonIdentifier name,
    IonType type,
    string constantValue,
    IReadOnlyList<IonAttributeInstance> attributes) : IonBase(name, attributes);

public record IonArgument(
    IonIdentifier name,
    IonType type,
    IReadOnlyList<IonAttributeInstance> attributes)
    : IonBase(name, attributes), ITypeWithName
{
    public IonType Type => type;
    public IonIdentifier Name => name;
}


public abstract record IonTypeConstraint(IonIdentifier Name) : IonBase(Name, []);

public sealed record IonTypeParameter(
    IonIdentifier Name,
    IReadOnlyList<IonAttributeInstance> Attributes
) : IonBase(Name, Attributes)
{
    public static implicit operator IonTypeParameter(string value) => new(value, []);
}

public record IonGenericType(
    IonIdentifier name,
    IReadOnlyList<IonAttributeInstance> attributes,
    IReadOnlyList<IonField> fields,
    IReadOnlyList<IonTypeParameter> TypeParameters,
    IReadOnlyList<IonType> TypeArguments,
    bool isTypedef = false
) : IonType(name, attributes, fields, isTypedef)
{
    public bool IsGenericDefinition => TypeParameters.Count > 0 && TypeArguments.Count == 0;
    public bool IsGenericInstance => TypeArguments.Count > 0;
    public bool IsOpenGeneric => TypeArguments.Count == 0;
}

public record IonType(
    IonIdentifier name,
    IReadOnlyList<IonAttributeInstance> attributes,
    IReadOnlyList<IonField> fields,
    bool isTypedef = false)
    : IonBase(name, attributes)
{
    public bool IsBuiltin => attributes.Any(x => x.IsBuiltinAttribute);
    public bool IsScalar => attributes.Any(x => x.IsScalarAttribute);
    public int? Tag => attributes.FirstOrDefault(x => x.IsTag)?.arguments.OfType<int>().FirstOrDefault();
    public bool IsUnresolved => this is IonUnresolvedType;
    public bool IsGenericType => this is IonGenericType;
    public bool IsUnresolvedGenericType => this is IonUnresolvedType && name.Identifier.Equals("?");
    public bool IsVoid => this.name.Identifier.Equals("void");
    public bool IsMaybe => this.name.Identifier.Equals("Maybe");
    public bool IsArray => this.name.Identifier.Equals("Array");
}

public record IonArray(IonType type, int rank, bool IsFixedSize) : IonType(type.name, type.attributes, type.fields, type.isTypedef);

public record IonEnum(
    IonIdentifier name,
    IReadOnlyList<IonAttributeInstance> attributes,
    IReadOnlyList<IonConstant> members,
    IonType baseType) : IonType(name, attributes, []);

public record IonFlags(
    IonIdentifier name,
    IReadOnlyList<IonAttributeInstance> attributes,
    IReadOnlyList<IonConstant> members,
    IonType baseType) : IonType(name, attributes, []);

public record IonMethod(
    IonIdentifier name,
    IReadOnlyList<IonArgument> arguments,
    IonType returnType,
    IReadOnlyList<IonMethodModifiers> modifiers,
    IReadOnlyList<IonAttributeInstance> attributes)
    : IonBase(name, attributes)
{
    public bool IsStreamable => modifiers.Any(x => x is IonMethodModifiers.Stream);
}

public record IonService(
    IonIdentifier name,
    IReadOnlyList<IonMethod> methods,
    IReadOnlyList<IonAttributeInstance> attributes)
    : IonBase(name, attributes);

public sealed record IonUnresolvedType(
    IonIdentifier name,
    IReadOnlyList<IonAttributeInstance> attributes,
    IonSyntaxMember syntax,
    bool isTypedef = false)
    : IonType(name, attributes, [], isTypedef);