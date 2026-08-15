namespace ion.runtime;

/// <summary>
/// A declaration kind an attribute may be written on — the vocabulary of the <c>on</c> clause of an
/// attribute declaration (<c>attribute @deadline(ms: i4) on service, method;</c>).
/// </summary>
/// <remarks>
/// <para>
/// A declaration with <em>no</em> <c>on</c> clause is unrestricted; that is modelled as a
/// <see langword="null"/> <see cref="IonAttributeType.targets"/> rather than as "every member of
/// this enum", so adding a new target kind here cannot silently narrow an existing declaration.
/// </para>
/// <para>
/// The spellings are the source keywords and are matched ordinally — <c>enumMember</c> and
/// <c>unionCase</c> are camelCase exactly as written in Ion.
/// </para>
/// </remarks>
public enum IonAttributeTarget
{
    /// <summary><c>msg Foo { … }</c></summary>
    Msg,

    /// <summary>A field of a <c>msg</c>, a union's shared field, or a field of an inline union case.</summary>
    Field,

    /// <summary><c>enum E : i4 { … }</c></summary>
    Enum,

    /// <summary><c>flags F : u4 { … }</c></summary>
    Flags,

    /// <summary>A member of an <c>enum</c> or a <c>flags</c> declaration.</summary>
    EnumMember,

    /// <summary><c>union U { … }</c></summary>
    Union,

    /// <summary>A case of a union, whether inline (<c>Ok(x: i4)</c>) or a type reference (<c>Data</c>).</summary>
    UnionCase,

    /// <summary><c>service S(…) { … }</c></summary>
    Service,

    /// <summary>A method of a service.</summary>
    Method,

    /// <summary>A service base argument, a method argument, or an attribute declaration's parameter.</summary>
    Argument,

    /// <summary><c>typedef Alias = Underlying;</c></summary>
    Typedef,

    /// <summary><c>attribute @name(…);</c> — an attribute written on another attribute's declaration.</summary>
    Attribute
}

/// <summary>
/// Source spellings for <see cref="IonAttributeTarget"/> and the prose used to name a target in a
/// diagnostic.
/// </summary>
public static class IonAttributeTargets
{
    /// <summary>Every target keyword, in declaration order. This is the list quoted by ION0038.</summary>
    public static readonly IReadOnlyList<string> Keywords =
    [
        "msg", "field", "enum", "flags", "enumMember", "union", "unionCase",
        "service", "method", "argument", "typedef", "attribute"
    ];

    /// <summary>Every target, for declarations that mean "anywhere" but want to say so explicitly.</summary>
    public static readonly IReadOnlyList<IonAttributeTarget> All =
        Enum.GetValues<IonAttributeTarget>();

    private static readonly Dictionary<string, IonAttributeTarget> ByKeyword = new(StringComparer.Ordinal)
    {
        ["msg"] = IonAttributeTarget.Msg,
        ["field"] = IonAttributeTarget.Field,
        ["enum"] = IonAttributeTarget.Enum,
        ["flags"] = IonAttributeTarget.Flags,
        ["enumMember"] = IonAttributeTarget.EnumMember,
        ["union"] = IonAttributeTarget.Union,
        ["unionCase"] = IonAttributeTarget.UnionCase,
        ["service"] = IonAttributeTarget.Service,
        ["method"] = IonAttributeTarget.Method,
        ["argument"] = IonAttributeTarget.Argument,
        ["typedef"] = IonAttributeTarget.Typedef,
        ["attribute"] = IonAttributeTarget.Attribute
    };

    private static readonly Dictionary<IonAttributeTarget, string> ToKeyword =
        ByKeyword.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Prose naming a target so it reads on from "cannot be applied to …".</summary>
    private static readonly Dictionary<IonAttributeTarget, string> Prose = new()
    {
        [IonAttributeTarget.Msg] = "a msg",
        [IonAttributeTarget.Field] = "a field",
        [IonAttributeTarget.Enum] = "an enum",
        [IonAttributeTarget.Flags] = "a flags declaration",
        [IonAttributeTarget.EnumMember] = "an enum member",
        [IonAttributeTarget.Union] = "a union",
        [IonAttributeTarget.UnionCase] = "a union case",
        [IonAttributeTarget.Service] = "a service",
        [IonAttributeTarget.Method] = "a method",
        [IonAttributeTarget.Argument] = "an argument",
        [IonAttributeTarget.Typedef] = "a typedef",
        [IonAttributeTarget.Attribute] = "an attribute declaration"
    };

    public static bool TryParse(string keyword, out IonAttributeTarget target) =>
        ByKeyword.TryGetValue(keyword, out target);

    /// <summary>The source keyword for <paramref name="target"/> — <c>enumMember</c>, <c>msg</c>, …</summary>
    public static string Keyword(this IonAttributeTarget target) => ToKeyword[target];

    /// <summary>"a field", "an enum member", … — reads on from "cannot be applied to ".</summary>
    public static string Describe(this IonAttributeTarget target) => Prose[target];

    /// <summary>The <c>on</c> clause a target list would be written as: <c>msg, field</c>.</summary>
    public static string Format(IEnumerable<IonAttributeTarget> targets) =>
        string.Join(", ", targets.Select(Keyword));
}
