namespace ion.runtime;

public sealed class IonModule
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required List<IonType> Definitions { get; init; }
    public required List<IonFeature> Features { get; init; }
    public required List<IonAttributeType> Attributes { get; init; }
    public required List<string> Imports { get; init; }


    public static readonly Lazy<IonModule> GetStdModule = new(() => new IonModule
    {
        Name = "std",
        Path = "ion://std",
        Features = ["builtin"],
        Definitions =
        [
            new ("bool", ["scalar", "builtin"], [], true),

            new ("i1", ["scalar", "builtin"], [], true),
            new ("i2", ["scalar", "builtin"], [], true),
            new ("i4", ["scalar", "builtin"], [], true),
            new ("i8", ["scalar", "builtin"], [], true),
            new ("i16", ["scalar", "builtin"], [], true),

            new ("u1", ["scalar", "builtin"], [], true),
            new ("u2", ["scalar", "builtin"], [], true),
            new ("u4", ["scalar", "builtin"], [], true),
            new ("u8", ["scalar", "builtin"], [], true),
            new ("u16", ["scalar", "builtin"], [], true),

            new ("f2", ["scalar", "builtin"], [], true),
            new ("f4", ["scalar", "builtin"], [], true),
            new ("f8", ["scalar", "builtin"], [], true),

            new ("bigint", ["builtin"], [], true),

            new ("guid", ["builtin"], [], true),
            new ("string", ["builtin"], [], true),
            new ("datetime", ["builtin"], [], true),
            new ("dateonly", ["builtin"], [], true),
            new ("timeonly", ["builtin"], [], true),
            new ("uri", ["builtin"], [], true),
            new ("duration", ["scalar", "builtin"], [], true)
        ],
        Attributes =
        [
            new ("builtin", []),
            new ("scalar", []),
            new ("tag", [new IonType("i4", ["scalar", "builtin"], [], true)]),
        ],
        Imports = []
    });

    public static readonly Lazy<IonModule> GetVectorModule = new(() => new IonModule
    {
        Name = "vector",
        Path = "ion://vector",
        Features = ["builtin"],
        Definitions =
        [
            new ("vec2f", ["builtin"], [], true),
            new ("vec3f", ["builtin"], [], true),
            new ("vec4f", ["builtin"], [], true),

            new ("vec2d", ["builtin"], [], true),
            new ("vec3d", ["builtin"], [], true),
            new ("vec4d", ["builtin"], [], true),

            new ("vec2h", ["builtin"], [], true),
            new ("vec3h", ["builtin"], [], true),
            new ("vec4h", ["builtin"], [], true),
        ],
        Attributes = [],
        Imports = []
    });
}

public record IonBase(string name, List<IonAttributeInstance> attributes);

public record IonField(string name, IonType type, List<IonAttributeInstance> attributes) : IonBase(name, attributes);

public record IonType(string name, List<IonAttributeInstance> attributes, List<IonField> fields, bool isTypedef = false)
    : IonBase(name, attributes)
{
    public bool IsBuiltin => attributes.Any(x => x.IsBuiltinAttribute);
    public bool IsScalar => attributes.Any(x => x.IsScalarAttribute);
    public int? Tag => attributes.FirstOrDefault(x => x.IsTag)?.arguments.OfType<int>().FirstOrDefault();
}