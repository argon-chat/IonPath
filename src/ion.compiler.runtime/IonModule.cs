namespace ion.runtime;

using syntax;

public sealed class IonModule
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required List<IonType> Definitions { get; init; }
    public required List<IonService> Services { get; init; }
    public required List<IonFeature> Features { get; init; }
    public required List<IonAttributeType> Attributes { get; init; }
    public required List<string> Imports { get; init; }
    public IonFileSyntax? Syntax { get; init; } = null;

    /// <summary>
    /// File level documentation ('//!' module docs) declared at the top of the .ion source.
    /// Lines are joined with '\n'. Null when the module is undocumented.
    /// </summary>
    public string? Doc { get; init; } = null;

    /// <summary>
    /// If this module was loaded as an external dependency, this is the module name from ion.config.json.
    /// Null for the local project's own modules.
    /// </summary>
    public string? SourceModule { get; init; } = null;

    /// <summary>
    /// Whether this module comes from an external dependency.
    /// </summary>
    public bool IsExternal => SourceModule is not null;


    public static readonly Lazy<IonModule> GetStdModule = new(() => new IonModule
    {
        Name = "std",
        Path = "ion://std",
        Features = ["builtin"],
        Definitions =
        [
            new("void", ["builtin"], [], true),

            new("bool", ["scalar", "builtin"], [], true),

            new("i1", ["scalar", "builtin", sizeof(sbyte).Bits()], [], true),
            new("i2", ["scalar", "builtin", sizeof(short).Bits()], [], true),
            new("i4", ["scalar", "builtin", sizeof(int).Bits()], [], true),
            new("i8", ["scalar", "builtin", sizeof(long).Bits()], [], true),
            new("i16", ["scalar", "builtin", 16.Bits()], [], true),

            new("u1", ["scalar", "builtin", sizeof(byte).Bits()], [], true),
            new("u2", ["scalar", "builtin", sizeof(ushort).Bits()], [], true),
            new("u4", ["scalar", "builtin", sizeof(uint).Bits()], [], true),
            new("u8", ["scalar", "builtin", sizeof(ulong).Bits()], [], true),
            new("u16", ["scalar", "builtin", 16.Bits()], [], true),

            new("f2", ["scalar", "builtin", 2.Bits()], [], true),
            new("f4", ["scalar", "builtin", sizeof(float).Bits()], [], true),
            new("f8", ["scalar", "builtin", sizeof(double).Bits()], [], true),

            new("bigint", ["builtin"], [], true),

            new("guid", ["builtin"], [], true),
            new("string", ["builtin"], [], true),
            new("datetime", ["builtin"], [], true),
            new("dateonly", ["builtin"], [], true),
            new("timeonly", ["builtin"], [], true),
            new("uri", ["builtin"], [], true),
            new("duration", ["scalar", "builtin"], [], true),

            new("bytes", ["builtin"], [], true),

            new IonGenericType("Maybe", ["builtin"], [], ["T"], []),
            new IonGenericType("Array", ["builtin"], [], ["T"], []),
            new IonGenericType("Partial", ["builtin"], [], ["T"], []),
        ],
        Attributes =
        [
            new("builtin", []),
            new("scalar", []),
            new("tag", [new IonArgument("tagId", new IonType("i4", ["scalar", "builtin"], [], true), [])]),
            new("deadline", [new IonArgument("time", new IonType("i4", ["scalar", "builtin"], [], true), [])]),
            new("deprecated", []),
            new("internal", []),
            new("bits", [new IonArgument("bitCount", new IonType("i4", ["scalar", "builtin"], [], true), [])]),
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

public record IonBase(IonIdentifier name, IReadOnlyList<IonAttributeInstance> attributes)
{
    private string? _doc;

    /// <summary>
    /// Documentation comment text ('///' / '/** */') attached to this declaration in the .ion source.
    /// Lines are joined with '\n'. Null when the declaration is undocumented.
    /// </summary>
    /// <remarks>
    /// Deliberately invisible to value equality — see the hand written <see cref="Equals(IonBase)"/> /
    /// <see cref="GetHashCode"/> below. Documentation must never affect semantic identity.
    /// </remarks>
    public string? Doc
    {
        get => _doc;
        set => _doc = value;
    }

    // The compiler generated record equality compares *every* instance field, which would drag
    // the mutable `_doc` backing field into Equals/GetHashCode. That is unacceptable here:
    //   * IonDependencyGraph keys Dictionary<IonType, ...> / HashSet<IonType> on these records;
    //   * RestoreUnresolvedTypeStage keys Dictionary<IonType, List<IonType>> and calls Distinct().
    // A doc-bearing type must stay equal (and hash equal) to the same type without docs, and
    // mutating Doc after a value has been used as a hash key must not corrupt the bucket.
    // These overrides reproduce the previously synthesized semantics exactly, minus `_doc`.
    // Derived records keep their generated Equals/GetHashCode, which chain into these.
    public virtual bool Equals(IonBase? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;
        return EqualityContract == other.EqualityContract
               && EqualityComparer<IonIdentifier>.Default.Equals(name, other.name)
               && EqualityComparer<IReadOnlyList<IonAttributeInstance>>.Default.Equals(attributes, other.attributes);
    }

    public override int GetHashCode() => HashCode.Combine(EqualityContract, name, attributes);
}

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
    IReadOnlyList<IonAttributeInstance> attributes, 
    IonArgumentModifiers mod = IonArgumentModifiers.None)
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
    public bool IsPartial => this.name.Identifier.Equals("Partial");

    public bool IsUnion => attributes.Any(x => x.IsUnion);
    public bool IsUnionCase => attributes.Any(x => x.IsUnionCase);
    public bool HasBitsAttribute => attributes.Any(x => x is IonBitAttributeInstance);


    public int Bits => (int)attributes.First(x => x is IonBitAttributeInstance).arguments.First();

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

public record IonUnion(IonIdentifier name,
    List<IonType> types,
    List<IonArgument> sharedFields,
    IReadOnlyList<IonAttributeInstance> attributes)
    : IonType(name, attributes, [], false);