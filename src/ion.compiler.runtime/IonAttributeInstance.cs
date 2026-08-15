namespace ion.runtime;

using ion.syntax;

/// <summary>
/// One <em>use</em> of an attribute — <c>@deprecated("2.0")</c> written on a declaration.
/// </summary>
/// <param name="arguments">
/// The argument values, always in <em>declaration</em> order and always exactly as long as the
/// declaration's parameter list.
/// <para>
/// Two normalisations happen before an instance is built, so no consumer ever has to know how the
/// use site was written:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Named arguments are resolved to their declared position.</b> <c>@Cache(key: "u", duration: 30)</c>
/// and <c>@Cache(30, "u")</c> produce the identical <c>[30, "u"]</c>.
/// </item>
/// <item>
/// <b>An omitted trailing optional is <see langword="null"/>.</b> A parameter declared <c>T?</c> may
/// be left off at the use site; its slot is still present and holds <see langword="null"/>. So for
/// <c>attribute @deprecated(since: string?, reason: string?)</c>, a bare <c>@deprecated</c> is
/// <c>[null, null]</c>, <c>@deprecated("2.0")</c> is <c>["2.0", null]</c>. An explicitly written
/// <c>null</c> is indistinguishable from an omission, which is deliberate: both mean "no value".
/// </item>
/// </list>
/// <para>
/// Element CLR types follow the declared parameter type: <c>bool</c>, <c>sbyte</c>/<c>short</c>/
/// <c>int</c>/<c>long</c>, <c>byte</c>/<c>ushort</c>/<c>uint</c>/<c>ulong</c>,
/// <see cref="System.Numerics.BigInteger"/> (<c>i16</c>, <c>u16</c>, <c>bigint</c>),
/// <see cref="Half"/>/<see cref="float"/>/<see cref="double"/>, <see cref="string"/>,
/// <see cref="Guid"/>, <see cref="DateTime"/>, <see cref="DateOnly"/>, <see cref="TimeOnly"/>,
/// <see cref="Uri"/>, <see cref="TimeSpan"/>. An array parameter (<c>T[]</c>) holds a
/// <c>List&lt;object?&gt;</c> of those.
/// </para>
/// </param>
public record IonAttributeInstance(IonIdentifier name, List<object?> arguments)
{
    private readonly IReadOnlyList<string> _parameterNames = [];

    /// <summary>
    /// The declaration's parameter names, positionally aligned with <see cref="arguments"/>.
    /// </summary>
    /// <remarks>
    /// Populated whenever the instance was produced by binding a use site against a declaration.
    /// Empty for the hand-built instances the std module attaches to its own builtin definitions,
    /// which nothing looks up by name. Deliberately invisible to value equality — see the hand
    /// written <see cref="Equals(IonAttributeInstance)"/> below; it is derived data, and two
    /// instances of the same attribute with the same argument values are the same attribute.
    /// </remarks>
    public IReadOnlyList<string> parameterNames
    {
        get => _parameterNames;
        init => _parameterNames = value;
    }

    /// <summary>Compares the attribute's name, ignoring source position.</summary>
    /// <remarks>
    /// <see cref="IonIdentifier"/> derives from <see cref="IonSyntaxBase"/>, so its generated record
    /// equality also compares <c>StartPosition</c> / <c>EndPosition</c> / <c>SourceFile</c>. That
    /// makes <c>name.Equals("bits")</c> true only for an identifier synthesized from a string literal
    /// and false for one the parser produced. Every "is this the X attribute" predicate below goes
    /// through here so that a parsed <c>@bits(8)</c> and a synthesized one answer the same.
    /// </remarks>
    public bool Is(string attributeName) => string.Equals(name.Identifier, attributeName, StringComparison.Ordinal);

    public bool IsBuiltinAttribute => Is(IonReservedAttributes.Builtin);
    public bool IsScalarAttribute => Is(IonReservedAttributes.Scalar);
    public bool IsUnion => Is(IonReservedAttributes.Union);
    public bool IsUnionCase => Is(IonReservedAttributes.UnionCase);

    /// <summary>Whether this is <c>@deprecated</c> — see <c>ion.compiler.DeprecatedUsageStage</c>.</summary>
    public bool IsDeprecated => Is("deprecated");

    /// <summary>
    /// The value bound to the parameter called <paramref name="parameterName"/>, or
    /// <see langword="null"/> when the parameter does not exist or was omitted.
    /// </summary>
    /// <remarks>
    /// Reading positionally is always correct (see <see cref="arguments"/>); this exists so call
    /// sites can say <c>attr["reason"]</c> instead of <c>attr.arguments[1]</c>.
    /// </remarks>
    public object? this[string parameterName]
    {
        get
        {
            var index = IndexOf(parameterName);
            return index < 0 ? null : arguments[index];
        }
    }

    /// <summary>Typed by-name read; <see langword="null"/> when absent, omitted, or of another type.</summary>
    public T? Get<T>(string parameterName) => this[parameterName] is T value ? value : default;

    /// <summary>
    /// Whether the parameter exists <em>and</em> was given a value (an omitted trailing optional
    /// reports <see langword="false"/>).
    /// </summary>
    public bool Has(string parameterName) => this[parameterName] is not null;

    private int IndexOf(string parameterName)
    {
        for (var i = 0; i < _parameterNames.Count && i < arguments.Count; i++)
            if (string.Equals(_parameterNames[i], parameterName, StringComparison.Ordinal))
                return i;

        return -1;
    }

    public static implicit operator IonAttributeInstance(string value) => new(value, []);

    // Reproduces the previously synthesized record equality exactly, minus `_parameterNames`:
    // EqualityContract, `name` by value, `arguments` by reference (List<T> has no value equality).
    // IonType keys HashSet/Dictionary on its attribute list, so this must stay cheap and stable.
    public virtual bool Equals(IonAttributeInstance? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return EqualityContract == other.EqualityContract
               && EqualityComparer<IonIdentifier>.Default.Equals(name, other.name)
               && EqualityComparer<List<object?>>.Default.Equals(arguments, other.arguments);
    }

    public override int GetHashCode() => HashCode.Combine(EqualityContract, name, arguments);
}

/// <summary>
/// The attribute names the compiler owns: it attaches them to the IR itself, and source may not
/// write them (ION0038).
/// </summary>
/// <remarks>
/// They stay declared in <c>IonModule.GetStdModule</c> so that a stray use resolves to a real
/// declaration and is rejected with a message about what it is, rather than falling through to
/// ION0005 "attribute not found" — which would be false, and would suggest adding an import.
/// </remarks>
public static class IonReservedAttributes
{
    public const string Builtin = "builtin";
    public const string Scalar = "scalar";
    public const string Union = "union";
    public const string UnionCase = "unionCase";

    public static readonly IReadOnlyList<string> All = [Builtin, Scalar, Union, UnionCase];

    private static readonly HashSet<string> Set = new(All, StringComparer.Ordinal);

    public static bool IsReserved(string attributeName) => Set.Contains(attributeName);
}

public record IonBuiltinAttributeInstance() : IonAttributeInstance(IonReservedAttributes.Builtin, []);
public record IonScalarAttributeInstance() : IonAttributeInstance(IonReservedAttributes.Scalar, []);
public record IonUnionAttributeInstance() : IonAttributeInstance(IonReservedAttributes.Union, []);
public record IonUnionCaseAttributeInstance() : IonAttributeInstance(IonReservedAttributes.UnionCase, []);
public record IonBitAttributeInstance(int bitCount) : IonAttributeInstance("bits", [bitCount]);

public static class NumberBitEx
{
    public static IonBitAttributeInstance Bits(this int bytesCount) => new(bytesCount * 8);
}


/// <summary>
/// A user declared 'attribute' definition.
/// </summary>
/// <remarks>
/// Derives from <see cref="IonBase"/>, so <c>Doc</c> (the '///' documentation of the attribute
/// declaration) is inherited — it is intentionally NOT redeclared here, a shadowing member would
/// hide the base property and break polymorphic doc lookups.
/// </remarks>
/// <param name="arguments">
/// The declared parameters, in order. A parameter whose type is <c>Maybe&lt;T&gt;</c> (written
/// <c>T?</c>) is <em>optional</em> and may be omitted at the use site; optional parameters must be
/// trailing (ION0039), so "omitted" is always unambiguous.
/// </param>
/// <param name="targets">
/// The declaration kinds this attribute may be written on, from its <c>on</c> clause.
/// <see langword="null"/> means no <c>on</c> clause was written, i.e. the attribute is allowed
/// anywhere — which is not the same as an empty list (an empty list would forbid every position).
/// </param>
public record IonAttributeType(
    IonIdentifier name,
    List<IonArgument> arguments,
    IReadOnlyList<IonAttributeTarget>? targets = null) : IonBase(name, [])
{
    /// <summary>Whether <paramref name="target"/> is a legal position for this attribute.</summary>
    public bool Allows(IonAttributeTarget target) => targets is null || targets.Contains(target);
}
