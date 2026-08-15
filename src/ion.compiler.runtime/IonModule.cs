namespace ion.runtime;

using syntax;
using Target = IonAttributeTarget;

public sealed class IonModule
{
    #region Builtin attribute signature helpers

    // Attribute parameter types are always std builtins (ION0003 / ION0004 enforce that), so they
    // are spelled out here rather than resolved — GetStdModule is what *defines* the std module,
    // and cannot look itself up while it is still being constructed.
    private static readonly IonType I4 = new("i4", ["scalar", "builtin"], [], true);
    private static readonly IonType Str = new("string", ["builtin"], [], true);

    /// <summary>
    /// <c>T?</c> — the optional form. Mirrors <c>CompilationContext.WrapModifiers</c> exactly: an
    /// optional parameter is a <c>Maybe&lt;T&gt;</c> instance, which is what
    /// <c>IonAttributeBinder.IsOptional</c> tests for.
    /// </summary>
    private static IonType Opt(IonType inner) => new IonGenericType("Maybe", ["builtin"], [], ["T"], [inner]);

    private static IonArgument Arg(string name, IonType type) => new(name, type, []);

    #endregion

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

            // `decimal` is exact base-10 arithmetic, wire-encoded as CBOR tag 4 (a definite-length
            // two element array of exponent and mantissa) — see ion.runtime/formatter.decimal.cs.
            //
            // Deliberately NOT marked "scalar". In this module "scalar" means "one fixed-width CBOR
            // head, one machine register": every i*/u*/f*, `bool` and `duration` carry it; `bigint`,
            // `string`, `guid`, `datetime` and `bytes` do not. A tag wrapping a two element array is
            // not one head and does not fit a register, so calling it scalar would be a lie that two
            // live consumers read — `PartialTypeValidationStage.Describe` would call it "a builtin
            // scalar type" in ION0018, and `CircularTypeReferenceStage` treats `IsScalar` as a leaf.
            // Exactness is not what the flag records; width is.
            new("decimal", ["builtin"], [], true),

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

            // `Map` and `Set` are ordinary builtin generics, declared beside the three wrappers so
            // that one arity rule (ION0060, off TypeParameters.Count) covers all five. Unlike the
            // wrappers they have no modifier suffix spelling — there is no `T{}` — so they are only
            // ever written by name, which is also why they are the first two generics whose
            // arguments actually have to survive `CompilationContext.ResolveTypeFor`.
            new IonGenericType("Map", ["builtin"], [], ["K", "V"], []),
            new IonGenericType("Set", ["builtin"], [], ["T"], []),
        ],
        Attributes =
        [
            // Compiler-internal markers. They are attached to the definitions above by hand rather
            // than parsed, but they stay declared so that a source `@builtin` resolves (ION0005)
            // and lands somewhere sane (ION0038) instead of being a silently accepted no-op.
            new("builtin", [], [Target.Msg, Target.Typedef]),
            new("scalar", [], [Target.Msg, Target.Typedef]),

            // `@union` / `@unionCase` are synthesized by TransformStage onto the union and its
            // inline cases; the targets below are exactly the two positions it attaches them to,
            // so the synthesized use can never contradict the declaration.
            new("union", [], [Target.Union]),
            new("unionCase", [], [Target.UnionCase]),

            // `@tag(n)` used to be declared here, modelled as IonTagAttributeInstance and read back
            // through IonType.Tag. Nothing ever read IonType.Tag: no generator and no runtime
            // emitted a CBOR semantic tag, so the attribute bound, validated, range-checked and then
            // did nothing at all. It is gone rather than left inert — an annotation that looks like
            // it controls the wire format but does not is worse than a missing feature, because the
            // schema author has no way to discover that it is a no-op. A deliberate CBOR tag feature
            // can reintroduce it together with the encoders that honour it.

            // Wire width of a numeric type. IonType.Bits reads it off the type, so it is a
            // type-level annotation.
            new("bits", [Arg("bitCount", I4)],
                [Target.Msg, Target.Enum, Target.Flags, Target.Typedef]),

            // A request timeout is a property of a call. `@deadline(500)` on a msg was silently
            // accepted before targets existed; it is now ION0038.
            new("deadline", [Arg("time", I4)], [Target.Service, Target.Method]),

            // Both parameters are optional and trailing, so `@deprecated`, `@deprecated("2.0")` and
            // `@deprecated("2.0", "use GetUserV2")` are all legal with no extra grammar. Every
            // declaration kind can be deprecated, spelled out rather than left unrestricted so the
            // signature documents itself.
            new("deprecated", [Arg("since", Opt(Str)), Arg("reason", Opt(Str))], IonAttributeTargets.All),

            new("internal", [],
            [
                Target.Msg, Target.Field, Target.Enum, Target.Flags, Target.EnumMember,
                Target.Union, Target.UnionCase, Target.Service, Target.Method, Target.Typedef
            ])
        ],
        Imports = [],
        Services = []
    });

    // `GetVectorModule` stood here: a builtin module declaring vec2f…vec4h behind the `vector`
    // feature. It is gone, along with the feature.
    //
    // The nine names resolved in the compiler and autocompleted in the editor, and that was the
    // entire implementation. No target mapped them — not C#, not TypeScript, not Rust, not Go — and
    // no runtime defined them, so a project that turned the feature on and used a `vec3f` got a
    // clean `ionc check` followed by generated code referencing a type that does not exist in any
    // of the four languages. That is the worst possible failure mode for a schema compiler: the
    // tooling actively recommends a type whose output does not build.
    //
    // Reintroducing vectors means adding the runtime representations and the four type mappings
    // first, and the feature last.

    public static readonly Lazy<IonModule> GetOrleansModule = new(() => new IonModule
    {
        Name = "orleans",
        Path = "ion://orleans",
        Features = ["builtin"],
        Definitions = [],
        Attributes =
        [
            // The grain key is carried by a single value — a service base argument (the usual
            // spelling, `service S(@grainId() id: guid)`) or a field of the message that stands in
            // for it.
            new("grainId", [], [Target.Argument, Target.Field]),

            // Orleans one-way dispatch is a property of the call.
            new("oneWay", [], [Target.Method])
        ],
        Imports = [],
        Services = []
    });

    /// <summary>
    /// Every feature name the compiler recognises, in the spelling <c>ion.config.json</c> uses.
    /// </summary>
    /// <remarks>
    /// The single authority for two things that must not drift apart: which modules
    /// <c>CompilationStage.CompilationContext.Create</c> switches on, and which names a
    /// <c>#feature</c> directive may write (ION0049). A feature that is not here maps to no module,
    /// so enabling it would silently do nothing.
    /// <para>
    /// <c>vector</c> was here. It resolved <c>vec2f</c>…<c>vec4h</c> in the compiler and the editor
    /// while no target mapped them and no runtime defined them, so enabling it produced code that
    /// did not build — the editor autocompleted a type whose generated output was broken. The
    /// feature and its module are removed.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> KnownFeatures = ["std", "orleans"];

    /// <summary>
    /// The builtin names that can carry an <c>enum</c> / <c>flags</c> member value.
    /// </summary>
    /// <remarks>
    /// Members are numbered — auto-incremented for an <c>enum</c>, bit-shifted for <c>flags</c> — so
    /// the base type has to be able to hold an integer. Every other builtin (<c>bool</c>, the floats,
    /// <c>string</c>, <c>guid</c>, <c>bigint</c>, the date/time types, <c>bytes</c>, <c>void</c>)
    /// would produce members whose declared type and whose value disagree.
    /// </remarks>
    public static readonly IReadOnlyList<string> IntegralBuiltins =
        ["i1", "i2", "i4", "i8", "i16", "u1", "u2", "u4", "u8", "u16"];

    private static readonly HashSet<string> IntegralBuiltinSet = new(IntegralBuiltins, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="type"/> is an integral builtin — see <see cref="IntegralBuiltins"/>.</summary>
    public static bool IsIntegralBuiltin(IonType type) =>
        type.IsBuiltin && IntegralBuiltinSet.Contains(type.name.Identifier);

    /// <summary>
    /// The builtin names that may stand in the key position of a <c>Map&lt;K, V&gt;</c>. Enums are
    /// additionally allowed and are not listed here because they are not builtins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runtime does <b>not</b> re-validate a key: <c>IonMapFormatter&lt;TKey, TValue&gt;</c>
    /// encodes whatever the key formatter emits and orders entries by those bytes. ION0061 is
    /// therefore the only thing standing between a schema and a map that cannot round-trip, so the
    /// line is drawn at two properties that must hold <em>together</em>:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>One value, one byte string.</b> Byte order is only a value order when the encoding is
    /// canonical. Fixed-width integers, <c>bool</c>, <c>duration</c>, definite-length UTF-8
    /// (<c>string</c>) and the 16 fixed bytes of a <c>guid</c> all qualify.
    /// </item>
    /// <item>
    /// <b>The generated key type has structural equality.</b> Every target has to put the decoded
    /// key into a real dictionary, and a key type with reference equality silently loses entries.
    /// </item>
    /// </list>
    /// <para>
    /// What that excludes, and why each one is a real defect rather than a taste call:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <c>f2</c> / <c>f4</c> / <c>f8</c> — the one place this list departs from a plain "scalar
    /// builtins" rule. Floats break property 1 in both directions: <c>-0.0</c> and <c>0.0</c> are
    /// different byte strings but the same dictionary key (a decode hits a duplicate key), and
    /// <c>NaN</c> is a byte string that is not equal to itself (a decode loses the entry). A map
    /// that cannot reproduce its own key set is not a map.
    /// </item>
    /// <item>
    /// <c>decimal</c> and <c>bigint</c> — arbitrary precision, so one value has many encodings
    /// (mantissa/exponent pairs; a bignum tag versus a head integer). Byte order is not value order.
    /// </item>
    /// <item>
    /// <c>bytes</c>, <c>datetime</c>, <c>dateonly</c>, <c>timeonly</c>, <c>uri</c> — property 2.
    /// These land on <c>byte[]</c> / <c>Uint8Array</c>, <c>Date</c>, <c>URL</c> and friends, whose
    /// default equality is by reference in at least one target, so a decoded map would never find
    /// its own keys.
    /// </item>
    /// <item>
    /// <c>msg</c>, <c>union</c>, <c>flags</c> and every generic (<c>Array</c>, <c>Map</c>,
    /// <c>Set</c>, <c>Maybe</c>, <c>Partial</c>) — aggregates. No canonical byte order, and the
    /// same reference-equality problem. <c>flags</c> is integer-backed and would technically
    /// satisfy both, but its value set is open (any bit combination) rather than the closed, named
    /// set an <c>enum</c> gives generated code, so it is held back with the aggregates until there
    /// is a use for it.
    /// </item>
    /// <item>
    /// <c>void</c> — not a value.
    /// </item>
    /// </list>
    /// </remarks>
    public static readonly IReadOnlyList<string> MapKeyBuiltins =
        [..IntegralBuiltins, "bool", "duration", "string", "guid"];

    private static readonly HashSet<string> MapKeyBuiltinSet = new(MapKeyBuiltins, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="type"/> is a builtin allowed in <c>Map</c> key position.</summary>
    public static bool IsMapKeyBuiltin(IonType type) =>
        type.IsBuiltin && MapKeyBuiltinSet.Contains(type.name.Identifier);
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


// `IonTypeConstraint` — an abstract record with no derived type, no construction site and no
// reader — was declared here. Generic constraints are not a language feature; the placeholder is
// removed rather than left to imply one exists.

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

    /// <summary>
    /// The <c>N</c> of a fixed-size array <c>T[N]</c>, on the <c>Array&lt;T&gt;</c> wrapper that
    /// <c>CompilationContext.WrapModifiers</c> built for it. <see langword="null"/> on every other
    /// generic and on an unsized <c>T[]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why here and not on <see cref="IonArray"/>.</b> <see cref="IonArray"/> has an
    /// <c>IsFixedSize</c> flag and a <c>rank</c> and has never been constructed by the compiler, and
    /// reviving it would have been the wrong move: it is declared
    /// <c>IonArray(...) : IonType(type.name, type.attributes, type.fields, …)</c>, i.e. it takes the
    /// <em>element's</em> name. An <c>IonArray</c> over <c>User</c> answers <c>name == "User"</c>,
    /// so <see cref="IonType.IsArray"/> is <see langword="false"/> on it, it is not an
    /// <see cref="IonGenericType"/>, and it has no <c>TypeArguments</c>. Every consumer of an array
    /// in this codebase identifies one exactly the other way round — <c>IsArray</c> plus
    /// <c>TypeArguments[0]</c>: <c>SchemaLockGenerator.GetCanonicalTypeName</c>,
    /// <c>CircularTypeReferenceStage.IsOwned</c>, <c>PartialTypeValidationStage.Describe</c>,
    /// <c>RestoreUnresolvedTypeStage.ResolveTypeDeep</c> and all four generators. An
    /// <see cref="IonArray"/> would be invisible to all of them; the
    /// <c>and not IonArray</c> clause already sitting in <c>IsTypedefAlias</c> is a scar from that
    /// exact hazard. Carrying the size on the wrapper keeps <c>T[N]</c> and <c>T[]</c> the same
    /// shape, differing only in one nullable field.
    /// </para>
    /// <para>
    /// An <c>init</c> property rather than a positional parameter so every existing construction
    /// site keeps compiling, and so <c>with { TypeArguments = … }</c> — which
    /// <c>RestoreUnresolvedTypeStage</c> does on every pass — carries the size through untouched.
    /// It <em>is</em> part of record equality (the synthesized <c>Equals</c> compares the backing
    /// field), which is required: <c>Array&lt;f4, 16&gt;</c> must not compare equal to
    /// <c>Array&lt;f4&gt;</c> or to <c>Array&lt;f4, 8&gt;</c>.
    /// </para>
    /// </remarks>
    public int? FixedSize { get; init; }

    /// <summary>Whether this is a fixed-size array <c>T[N]</c> rather than an unsized <c>T[]</c>.</summary>
    public bool IsFixedSizeArray => IsArray && FixedSize is not null;
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

    // `Tag` lived here and read the (now removed) `@tag` attribute. See GetStdModule for why the
    // whole feature is gone rather than left inert.

    public bool IsUnresolved => this is IonUnresolvedType;
    public bool IsGenericType => this is IonGenericType;
    public bool IsUnresolvedGenericType => this is IonUnresolvedType && name.Identifier.Equals("?");
    public bool IsVoid => this.name.Identifier.Equals("void");
    public bool IsMaybe => this.name.Identifier.Equals("Maybe");
    public bool IsArray => this.name.Identifier.Equals("Array");
    public bool IsPartial => this.name.Identifier.Equals("Partial");
    public bool IsMap => this.name.Identifier.Equals("Map");
    public bool IsSet => this.name.Identifier.Equals("Set");

    public bool IsUnion => attributes.Any(x => x.IsUnion);
    public bool IsUnionCase => attributes.Any(x => x.IsUnionCase);
    public bool HasBitsAttribute => attributes.Any(x => x is IonBitAttributeInstance);


    /// <remarks>
    /// Reads the typed <see cref="IonBitAttributeInstance"/> field rather than unboxing
    /// <c>arguments[0]</c>: an attribute argument slot is nullable (an omitted trailing optional is
    /// null), so the cast was an unboxing of a possibly-null value. The bit count lives on the node
    /// itself and cannot be absent.
    /// </remarks>
    public int Bits => attributes.OfType<IonBitAttributeInstance>().First().bitCount;

}

/// <summary>
/// Unused. Still declared only because <c>RestoreUnresolvedTypeStage.IsTypedefAlias</c>,
/// <c>CodeGeneratorBase</c> and <c>IonCSharpGenerator</c> all pattern-match <c>and not IonArray</c>,
/// and <c>ion.syntax.test/TypedefTests</c> constructs one to pin that guard.
/// </summary>
/// <remarks>
/// Fixed-size arrays did <b>not</b> revive this. It derives its <see cref="IonType.name"/> from the
/// element type, so it is not recognisable as an array by any of the array consumers in this
/// codebase; see <see cref="IonGenericType.FixedSize"/> for the full argument and for where the
/// size actually lives.
/// </remarks>
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