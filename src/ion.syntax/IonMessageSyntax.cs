namespace ion.syntax;

using Pidgin;

public record IonSyntaxBase
{
    public SourcePos StartPosition { get; set; }
    public SourcePos? EndPosition { get; set; }
    public FileInfo? SourceFile { get; set; }
}

public record IonSyntaxMember : IonSyntaxBase
{
    public string? Comments { get; set; }
    public List<IonAttributeSyntax> Attributes { get; set; } = [];
}

public record InvalidIonBlock(string block) : IonSyntaxMember
{

}

/// <summary>
/// A run of file level <c>//!</c> documentation. Emitted by the parser as a synthetic member and
/// lifted into <see cref="IonFileSyntax.ModuleDoc"/>; it never appears in a built file syntax.
/// </summary>
public record IonModuleDocSyntax(string Text) : IonSyntaxMember;

public record IonIdentifier(string Identifier) : IonSyntaxBase()
{
    public static implicit operator IonIdentifier(string val) => new(val) { StartPosition = new SourcePos(0, 0) };

    public override string ToString() => Identifier;
}
/// <summary>
/// A written type reference: a name, optional generic arguments, and the trailing modifier suffixes
/// <c>~</c> / <c>[]</c> / <c>[N]</c> / <c>?</c>.
/// <para>
/// A reference is <em>either</em> named (<see cref="Name"/> plus <see cref="generics"/>) <em>or</em>
/// an inline anonymous message (<see cref="InlineBody"/>). Consumers must test
/// <see cref="InlineBody"/> first: for an inline type <see cref="Name"/> is the synthetic
/// <see cref="InlineTypeName"/> placeholder, which resolves to nothing.
/// </para>
/// </summary>
/// <param name="ModifierTokens">
/// The modifier suffixes in source order — e.g. <c>["~", "[]", "?"]</c> for <c>Data~[]?</c>, and
/// <c>["~", "~"]</c> for <c>Data~~</c>.
/// <para>
/// <see cref="IsArray"/> / <see cref="IsOptional"/> / <see cref="IsPartial"/> are a lossy reduction
/// of this list: they answer "was it present at all", so they cannot distinguish <c>Data~</c> from
/// <c>Data~~</c> (a repeat, which is unrepresentable — <c>Partial&lt;Partial&lt;T&gt;&gt;</c> and
/// <c>T[][]</c> have no wire form) nor <c>Data~?</c> from <c>Data?~</c> (a written order that does
/// not match the inside-out order <c>CompilationContext.WrapModifiers</c> actually applies). This
/// list is the evidence <c>ion.compiler.TypeModifierValidationStage</c> needs to diagnose both; the
/// three bools stay exactly as they were, so nothing downstream had to change.
/// </para>
/// <para>
/// A sized array suffix is <b>normalized</b> to <c>"[]"</c> here, with the size carried separately in
/// <see cref="ArraySize"/>. That is deliberate and load-bearing: the repeat check groups these
/// strings by ordinal equality and the order check ranks them against a fixed
/// <c>["~", "[]", "?"]</c> table, so an <c>"[16]"</c> token would make <c>f4[16][8]</c> look like two
/// different modifiers (no ION0019) and <c>Data~[16]</c> look out of order (a spurious ION0010).
/// Normalized, <c>f4[16][8]</c> is diagnosed exactly like <c>f4[][]</c>. The cost is that a
/// diagnostic echoing the written form renders the sizes as <c>[]</c> — only ever in input that is
/// already an error, since a legal type carries at most one array suffix and
/// <see cref="ArraySize"/> then reproduces it exactly.
/// </para>
/// <para>
/// Optional and trailing on purpose: every existing positional construction site — including
/// synthesized nodes such as the implicit <c>u4</c> base type of an <c>enum</c> — keeps compiling
/// unchanged. <see langword="null"/> means "no modifier evidence recorded" and is treated as an
/// empty list, i.e. a synthesized node is never diagnosed.
/// </para>
/// </param>
/// <param name="ArraySize">
/// The size written on a fixed-size array suffix — <c>16</c> for <c>f4[16]</c>. <see langword="null"/>
/// means no size was written, which is both the unsized <c>T[]</c> and the no-array-at-all case;
/// <see cref="IsArray"/> is unchanged in meaning and still answers which of those two it is.
/// <para>
/// The grammar validates the <em>shape</em> only. <c>0</c> and negatives parse and are carried here
/// for the compiler to diagnose in context, exactly as a repeated modifier is: failing in the parser
/// aborts the enclosing declaration and loses error recovery for everything after it. A literal that
/// does not fit in <see cref="int"/> is the one exception — there is no value to carry, and no
/// semantic rule under which it could be legal, so it is a parse error.
/// </para>
/// <para>
/// When more than one array suffix is written (already an ION0019 repeat) this is the size of the
/// first suffix that carried one; the rest are dropped.
/// </para>
/// </param>
/// <param name="InlineBody">
/// The body of an inline anonymous message written in type position — <c>shipping: msg { … }</c> —
/// or <see langword="null"/> for an ordinary named reference. The compiler hoists it to a derived
/// name; until then it is not resolvable, and <see cref="Name"/> is <see cref="InlineTypeName"/>.
/// The modifier suffixes apply to it exactly as they do to a named type, so
/// <c>msg { … }[]</c> is an array of the hoisted message.
/// </param>
public record IonUnderlyingTypeSyntax(
    IonIdentifier Name,
    IReadOnlyList<IonTypeParameterSyntax> generics,
    bool IsArray,
    bool IsOptional,
    bool IsPartial = false,
    IReadOnlyList<string>? ModifierTokens = null,
    int? ArraySize = null,
    IonInlineMessageSyntax? InlineBody = null) : IonSyntaxBase
{
    /// <summary>
    /// The placeholder <see cref="Name"/> of an inline anonymous type. Deliberately unlexable — no
    /// identifier can start with <c>$</c> — so it can never collide with a declared type, and a
    /// consumer that forgets to branch on <see cref="InlineBody"/> fails loudly on a name nobody
    /// declared rather than silently binding to something real.
    /// </summary>
    public const string InlineTypeName = "$inline";

    /// <summary>Whether this reference is an inline anonymous message rather than a named type.</summary>
    public bool IsInline => InlineBody is not null;

    /// <summary>
    /// Start of the <c>[N]</c> suffix that produced <see cref="ArraySize"/>, so a diagnostic about
    /// the size anchors on the size and not on the whole type. Set only when a size was written.
    /// </summary>
    /// <remarks>
    /// A settable property rather than a constructor parameter, following
    /// <see cref="IonImportSyntax.ModuleNameStart"/>: it is diagnostic metadata about one sub-token,
    /// not part of the shape of the node.
    /// </remarks>
    public SourcePos? ArraySizeStart { get; set; }

    /// <summary>End of the <c>[N]</c> suffix. See <see cref="ArraySizeStart"/>.</summary>
    public SourcePos? ArraySizeEnd { get; set; }
}

/// <summary>
/// The body of an inline anonymous message written in type position:
/// <c>shipping: msg { address: string; postcode: string; };</c>.
/// </summary>
/// <remarks>
/// A field list and nothing else — the same production as a <c>msg</c> body, so doc comments and
/// attributes on the fields work identically. It carries no name and no <c>with</c> clause: it is
/// not a declaration, and keeping <c>msg</c>-in-type-position recognised by a single character of
/// lookahead (<c>{</c>) is what lets a type that happens to be spelled <c>msg</c> keep parsing as it
/// always did.
/// <para>
/// <see cref="IonSyntaxBase.StartPosition"/> is the <c>msg</c> keyword and
/// <see cref="IonSyntaxBase.EndPosition"/> the closing <c>}</c>, so the hoisting pass can name the
/// derived type after where it was written.
/// </para>
/// </remarks>
public record IonInlineMessageSyntax(List<IonFieldSyntax> Fields) : IonSyntaxMember;

public record IonFieldSyntax(IonIdentifier Name, IonUnderlyingTypeSyntax Type) : IonSyntaxMember;

/// <param name="Mixins">
/// The names in the <c>with</c> clause, in source order, each carrying its own position.
/// <see langword="null"/> — the default — means <em>no <c>with</c> clause was written</em>, which is
/// what keeps every message declared before mixins existed constructible unchanged. An empty list is
/// unreachable: <c>with</c> requires at least one name.
/// <para>
/// The names are unvalidated. Whether they resolve, resolve to a <c>mixin</c> rather than a type,
/// cycle, or collide on a field is the compiler's job — representing the mistake is what lets it say
/// so precisely instead of the declaration dying as a parse error.
/// </para>
/// </param>
public record IonMessageSyntax(
    IonIdentifier Name,
    List<IonFieldSyntax> Fields,
    List<IonIdentifier>? Mixins = null) : IonSyntaxMember;

/// <summary>
/// A field-set template: <c>mixin Audited { createdAt: datetime; createdBy: guid; }</c>.
/// </summary>
/// <remarks>
/// Structurally a <see cref="IonMessageSyntax"/> — same body production, same <c>with</c> clause —
/// but a separate node because a mixin is not a type. It has no wire identity and may not be written
/// in type position; rejecting that is the compiler's job, the grammar only produces the node.
/// <para>
/// Deliberately <b>not</b> included in <see cref="IonFileSyntax.Definitions"/>: every stage that
/// walks that list treats its members as types. Mixins are reachable through
/// <see cref="IonFileSyntax.mixinSyntaxes"/>, so a stage opts in rather than being handed one by
/// surprise.
/// </para>
/// </remarks>
/// <param name="Mixins">See <see cref="IonMessageSyntax.Mixins"/> — a mixin may itself compose others.</param>
public record IonMixinSyntax(
    IonIdentifier Name,
    List<IonFieldSyntax> Fields,
    List<IonIdentifier>? Mixins = null) : IonSyntaxMember;

public record IonFlagEntrySyntax(IonIdentifier Name, Maybe<IonExpression> ValueExpression) : IonSyntaxMember;
public record IonExpression(string value) : IonSyntaxBase;

public record IonFlagsSyntax(IonIdentifier Name, IonUnderlyingTypeSyntax Type, List<IonFlagEntrySyntax> Entries)
    : IonSyntaxMember;

public record IonEnumSyntax(IonIdentifier Name, IonUnderlyingTypeSyntax Type, List<IonFlagEntrySyntax> Entries)
    : IonSyntaxMember;

#region literals

/// <summary>
/// A literal value, as written in source. Shared by every construct that takes a value —
/// today attribute arguments, and (roadmap 1.3) default values, constants and enum/flags values.
/// <para>
/// Every literal carries <see cref="IonSyntaxBase.StartPosition"/>/<see cref="IonSyntaxBase.EndPosition"/>
/// so a diagnostic can point at the value itself rather than at the construct containing it.
/// </para>
/// <para>
/// The grammar is intentionally value-only: it does <b>not</b> accept a bare identifier. A single
/// identifier is a constant reference, which has no node here yet; <c>Type.Member</c> is an
/// <see cref="IonEnumRefLiteralSyntax"/> and nothing else.
/// </para>
/// </summary>
public abstract record IonLiteralSyntax : IonSyntaxBase;

/// <summary>
/// An integer literal: decimal, <c>0x</c>/<c>0X</c> hex or <c>0b</c>/<c>0B</c> binary, with
/// optional <c>_</c> digit separators and an optional leading <c>-</c>.
/// </summary>
/// <param name="Value">
/// The exact value, as a <see cref="System.Numerics.BigInteger"/> so that nothing is lost before
/// the semantic layer range-checks it against the declared parameter type.
/// </param>
/// <param name="Raw">
/// The author's spelling, sign and prefix included (<c>0xFF</c>, <c>1_000</c>, <c>-0</c>), so a
/// hover or a diagnostic can echo back what was actually written.
/// </param>
public record IonIntegerLiteralSyntax(System.Numerics.BigInteger Value, string Raw) : IonLiteralSyntax;

/// <summary>
/// A floating point literal: <c>1.5</c>, <c>-0.25</c>, <c>1e10</c>, <c>1.5e-3</c>.
/// A fraction is only recognised when the <c>.</c> is directly followed by a digit, so the
/// <c>.</c> of an enum member reference is never swallowed.
/// </summary>
/// <param name="Raw">The author's spelling. See <see cref="IonIntegerLiteralSyntax.Raw"/>.</param>
public record IonFloatLiteralSyntax(double Value, string Raw) : IonLiteralSyntax;

/// <summary>
/// A double quoted string literal. <paramref name="Value"/> is the <em>decoded</em> text:
/// escapes (<c>\" \\ \n \r \t \0 \uXXXX</c>) are already resolved.
/// </summary>
public record IonStringLiteralSyntax(string Value) : IonLiteralSyntax;

/// <summary><c>true</c> or <c>false</c>, terminated by a word boundary.</summary>
public record IonBoolLiteralSyntax(bool Value) : IonLiteralSyntax;

/// <summary><c>null</c>, terminated by a word boundary.</summary>
public record IonNullLiteralSyntax() : IonLiteralSyntax;

/// <summary>
/// An enum member reference, <c>Status.Active</c>. Purely syntactic: whether
/// <paramref name="TypeName"/> names an enum and whether it has that member is resolved later.
/// </summary>
public record IonEnumRefLiteralSyntax(IonIdentifier TypeName, IonIdentifier Member) : IonLiteralSyntax;

/// <summary>
/// An array literal, <c>[1, 2, 3]</c> or <c>[]</c>. Nesting is allowed
/// (<see cref="IonParser.MaxLiteralNestingDepth"/> levels); element types are not checked here.
/// </summary>
public record IonArrayLiteralSyntax(List<IonLiteralSyntax> Items) : IonLiteralSyntax;

#endregion

/// <summary>
/// One argument at an attribute use site: <c>3</c> (positional) or <c>maxAttempts: 3</c> (named).
/// </summary>
/// <param name="Name">
/// <see langword="null"/> for a positional argument.
/// </param>
public record IonAttributeArgumentSyntax(IonIdentifier? Name, IonLiteralSyntax Value) : IonSyntaxBase;

/// <summary>
/// An attribute use site, <c>@Cache(30, key: "x")</c>.
/// <para>
/// The grammar deliberately accepts a positional argument after a named one so that the semantic
/// layer can emit a targeted "positional arguments must precede named arguments" diagnostic
/// instead of a generic parse error.
/// </para>
/// </summary>
public record IonAttributeSyntax(IonIdentifier Name, List<IonAttributeArgumentSyntax> Args) : IonSyntaxBase;

public record IonUseSyntax(string Path) : IonSyntaxMember;

public record IonImportSyntax(List<string> TypeNames, string ModuleName) : IonSyntaxMember
{
    /// <summary>
    /// Position of the module name string literal (for precise error reporting).
    /// </summary>
    public SourcePos ModuleNameStart { get; set; }
    public SourcePos ModuleNameEnd { get; set; }
}

public record IonTypedefSyntax(IonUnderlyingTypeSyntax TypeName, IonUnderlyingTypeSyntax? BaseType) : IonSyntaxMember;

public record IonArgumentSyntax(IonIdentifier argName, IonUnderlyingTypeSyntax type, IonArgumentModifiers modifiers = IonArgumentModifiers.None) : IonSyntaxMember;

/// <summary>
/// An attribute declaration, <c>attribute @idx(n: u4) on field, unionCase;</c>.
/// </summary>
/// <param name="Targets">
/// The declaration sites this attribute may be applied to, as written in the <c>on</c> clause.
/// <see langword="null"/> — the default — means <em>no <c>on</c> clause was written</em>, which is
/// "any target"; that is what keeps every attribute declared before the clause existed legal.
/// An empty list is unreachable: <c>on</c> requires at least one target.
/// <para>
/// The identifiers are unvalidated. The legal set is closed — <c>msg</c>, <c>field</c>,
/// <c>enum</c>, <c>flags</c>, <c>enumMember</c>, <c>union</c>, <c>unionCase</c>, <c>service</c>,
/// <c>method</c>, <c>argument</c>, <c>typedef</c>, <c>attribute</c> — but an unknown keyword and a
/// duplicate are both representable so the semantic layer can diagnose them precisely instead of
/// the declaration dying as a parse error.
/// </para>
/// </param>
public record IonAttributeDefSyntax(
    IonIdentifier Name,
    List<IonArgumentSyntax> Args,
    List<IonIdentifier>? Targets = null) : IonSyntaxMember;

public record IonFeatureSyntax(string featureName) : IonSyntaxMember;

/// <summary>
/// One argument of a generic argument list — the <c>string</c> and the <c>Array&lt;User&gt;</c> of
/// <c>Map&lt;string, Array&lt;User&gt;&gt;</c>.
/// </summary>
/// <param name="Name">
/// The <em>head</em> name of the argument: <c>Array</c> for <c>Array&lt;User&gt;</c>,
/// <see cref="IonUnderlyingTypeSyntax.InlineTypeName"/> for an inline anonymous one. Unchanged in
/// meaning, so every existing <c>g.Name.Identifier</c> reader keeps working — but for anything other
/// than a bare name it is a summary, and <see cref="Type"/> is the whole argument.
/// </param>
/// <param name="Type">
/// The argument as written, in full: its own generic arguments, its modifier suffixes, and its
/// inline body if it has one. This is what makes nesting representable —
/// <c>Map&lt;string, Array&lt;User&gt;&gt;</c> used to be a parse error because an argument could
/// only ever be a bare identifier.
/// <para>
/// Nullable and trailing so the one-argument positional constructor keeps compiling, but the parser
/// always fills it in: it is <see langword="null"/> only on a node somebody synthesized by hand.
/// </para>
/// </param>
/// <remarks>
/// The optional <c>: constraint, constraint</c> clause is still accepted here and still discarded,
/// exactly as before — the arguments of a <em>use</em> site are not the place for constraints, but
/// removing the syntax would be an unrelated break.
/// </remarks>
public sealed record IonTypeParameterSyntax(
    IonIdentifier Name,
    IonUnderlyingTypeSyntax? Type = null
) : IonSyntaxBase;

public record IonMethodSyntax(
    IonIdentifier methodName,
    List<IonMethodModifiers> modifiers,
    List<IonArgumentSyntax> arguments,
    IonUnderlyingTypeSyntax? returnType) : IonSyntaxMember;

public record IonUnionSyntax(
    IonIdentifier unionName,
    List<IonArgumentSyntax> baseFields,
    List<IonUnionTypeCaseSyntax> cases) : IonSyntaxMember;

public record IonUnionTypeCaseSyntax(
    IonUnderlyingTypeSyntax caseName,
    List<IonArgumentSyntax> arguments,
    bool IsTypeRef) : IonSyntaxMember
{
}

public record IonServiceSyntax(IonIdentifier serviceName, List<IonArgumentSyntax> BaseArguments, List<IonMethodSyntax> Methods)
    : IonSyntaxMember;

public enum IonMethodModifiers
{
    Unary,
    Stream,
    Internal
}

public enum IonArgumentModifiers
{
    None,
    Stream
}

public record IonFileSyntax(
    string Name,
    FileInfo file,
    List<IonUseSyntax> useSyntaxes,
    List<IonImportSyntax> importSyntaxes,
    List<IonFeatureSyntax> featureSyntaxes,
    List<IonAttributeDefSyntax> attributeDefSyntaxes,
    List<IonEnumSyntax> enumSyntaxes,
    List<IonFlagsSyntax> flagsSyntaxes,
    List<IonMessageSyntax> messageSyntaxes,
    List<IonTypedefSyntax> typedefSyntaxes,
    List<IonServiceSyntax> serviceSyntaxes,
    List<IonUnionSyntax> unionSyntaxes,
    List<IonSyntaxMember>? allTokens = null,
    string? ModuleDoc = null)
{
    /// <summary>
    /// The <c>mixin</c> declarations in this file.
    /// </summary>
    /// <remarks>
    /// A body property rather than a constructor parameter on purpose: the positional list above is
    /// a public contract with a generated <c>Deconstruct</c>, and every existing construction site
    /// keeps compiling unchanged this way. Always non-null — <see cref="IonParser.Parse(string,string)"/>
    /// sets it, and it defaults to empty for anyone building a file syntax by hand.
    /// <para>
    /// Not folded into <see cref="Definitions"/>; see <see cref="IonMixinSyntax"/> for why.
    /// </para>
    /// </remarks>
    public List<IonMixinSyntax> mixinSyntaxes { get; init; } = [];

    public List<IonSyntaxMember> Definitions => attributeDefSyntaxes
        .OfType<IonSyntaxMember>()
        .Concat(flagsSyntaxes)
        .Concat(enumSyntaxes)
        .Concat(messageSyntaxes)
        .Concat(serviceSyntaxes)
        .Concat(unionSyntaxes)
        .Concat(typedefSyntaxes).ToList();
}

public static class IonFileProcessingScope
{
    private static readonly ThreadLocal<FileInfo?> currentFile = new(true);

    public static IDisposable Begin(FileInfo file)
    {
        if (currentFile.Value is not null) throw new InvalidOperationException($"Current thread already has locked file");
        currentFile.Value = file;
        return new Disposer();
    }


    private class Disposer : IDisposable
    {
        public void Dispose()
        {
            currentFile.Value = null;
        }
    }

    internal static FileInfo? Take() => currentFile.Value;
}


public static class IonSyntaxEx
{
    public static T WithComments<T>(this T t, string? comments) where T : IonSyntaxMember
    {
        t.Comments = comments;
        return t;
    }

    public static T WithComments<T>(this T t, Maybe<string> comments) where T : IonSyntaxMember
    {
        t.Comments = comments.GetValueOrDefault();
        return t;
    }

    public static T WithAttributes<T>(this T t, IEnumerable<IonAttributeSyntax> attributes) where T : IonSyntaxMember
    {
        t.Attributes.AddRange(attributes);
        return t;
    }

    public static T WithPos<T>(this T t, SourcePos pos) where T : IonSyntaxBase
    {
        t.StartPosition = pos;
        t.SourceFile = IonFileProcessingScope.Take();
        return t;
    }

    public static T WithPos<T>(this T t, SourcePos start, SourcePos end) where T : IonSyntaxBase
    {
        t.StartPosition = start;
        t.EndPosition = end;
        t.SourceFile = IonFileProcessingScope.Take();
        return t;
    }
}