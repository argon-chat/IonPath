namespace ion.compiler;

using syntax;

/// <summary>
/// One written type reference, together with the declaration it sits in.
/// </summary>
/// <param name="Role">
/// What the reference <em>is</em> to its owner — "field", "argument", "return type", "case",
/// "shared field", "base type", "underlying type", "parameter". A compile-time constant in every
/// case, so carrying it costs nothing for the callers that only want <see cref="Site"/>.
/// </param>
/// <param name="Declaration">The enclosing top level declaration.</param>
/// <param name="Container">
/// The intermediate member, when there is one: the method a parameter belongs to, or the union case
/// a field belongs to.
/// </param>
/// <param name="Member">
/// The member the type was written on, or <see langword="null"/> when the type belongs to the
/// declaration itself (an enum's base type, a typedef's underlying type, a method's return type).
/// </param>
public readonly record struct IonTypeSite(
    IonUnderlyingTypeSyntax Site,
    string Role,
    IonSyntaxMember Declaration,
    IonSyntaxMember? Container = null,
    IonSyntaxMember? Member = null)
{
    /// <summary>
    /// Every enclosing syntax node that could carry an attribute, innermost first.
    /// </summary>
    /// <remarks>
    /// Used to suppress a deprecation warning inside something that is itself deprecated: a field of
    /// a deprecated msg, or an argument of a deprecated method, does not need to be told.
    /// </remarks>
    public IEnumerable<IonSyntaxMember> Owners()
    {
        if (Member is not null)
            yield return Member;

        if (Container is not null)
            yield return Container;

        yield return Declaration;
    }
}

/// <summary>
/// The single traversal of "every position in a file where a type can be <em>written</em>".
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="PartialTypeValidationStage"/>, which owned it privately and filtered it
/// down to the <c>~</c> sites. Both that stage and <see cref="TypeModifierValidationStage"/> now walk
/// this one enumerator, so a type position added to the grammar cannot be picked up by one validator
/// and missed by the other.
/// </para>
/// <para>
/// The walk is over the <em>syntax</em> tree, not the IR, for the reasons spelled out on
/// <see cref="PartialTypeValidationStage"/>: the IR keeps no source position for a resolved type,
/// and <c>TransformStage.PrependMethods</c> copies a service's base arguments into every method, so
/// an IR walk would report one written declaration once per method. Here it appears exactly once.
/// </para>
/// <para>
/// The set of positions mirrors <c>UnusedSymbolDetectionStage.CollectReferencedTypeNames</c>.
/// </para>
/// </remarks>
public static class IonTypeSites
{
    /// <summary>
    /// Every written type <em>reference</em> in <paramref name="file"/>, in source order per
    /// declaration kind.
    /// </summary>
    /// <remarks>
    /// Declaration <em>names</em> are deliberately excluded even where the grammar parses them with
    /// the full type parser. A typedef's name side (<c>typedef Foo~ = Bar;</c>) and an inline union
    /// case name (<c>Ok~(x: i4)</c>) are places a type is being <em>introduced</em>, not referenced;
    /// a modifier there is meaningless rather than misordered, and the typedef case is already
    /// <see cref="IonAnalyticCodes.ION0015_TypedefNameModifier"/>'s territory. A referenced union
    /// case (<c>union U { Data~ }</c>) is a real reference and is included.
    /// </remarks>
    public static IEnumerable<IonUnderlyingTypeSyntax> Of(IonFileSyntax file) =>
        Sites(file).Select(site => site.Site);

    /// <summary>
    /// The same walk as <see cref="Of"/>, with the owning declaration attached.
    /// </summary>
    /// <remarks>
    /// <see cref="Of"/> is a projection of this, not a parallel implementation — the whole point of
    /// this class is that there is exactly one list of "places a type can be written", so a position
    /// added to the grammar cannot be picked up by one validator and missed by another.
    /// <see cref="DeprecatedUsageStage"/> needs the owner (to name the reference in its message, and
    /// to stay quiet inside something already deprecated); the modifier validators do not.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// Every <c>yield</c> below goes through <see cref="Nested"/>, which also emits the sub-types a
    /// written reference contains: its generic arguments, transitively, and the field types of an
    /// inline anonymous body. Before that, three whole regions of the tree were invisible here — the
    /// element of a <c>Map&lt;string, Data~&gt;</c>, the body of a <c>msg { … }</c> and the fields of
    /// a <c>mixin</c> — so <see cref="TypeModifierValidationStage"/> and
    /// <see cref="PartialTypeValidationStage"/> silently skipped them while claiming to be the one
    /// list of "places a type can be written".
    /// </para>
    /// <para>
    /// A nested site inherits its parent's <see cref="IonTypeSite.Role"/>, <c>Container</c> and
    /// <c>Member</c> rather than inventing new ones, so a diagnostic about the <c>Data</c> in
    /// <c>items: Map&lt;string, Data&gt;</c> still reads "the field 'items' of msg 'Cart'" — which is
    /// what the reader has to go and edit.
    /// </para>
    /// </remarks>
    public static IEnumerable<IonTypeSite> Sites(IonFileSyntax file)
    {
        foreach (var msg in file.messageSyntaxes)
        foreach (var field in msg.Fields)
        foreach (var site in Nested(new(field.Type, "field", msg, Member: field)))
            yield return site;

        // A mixin's fields are real written type positions. They are visited here, at the
        // declaration, and never again at the messages that include the mixin — the expansion in
        // `MixinExpansionStage` reuses these very IonFieldSyntax nodes, so walking the expansion too
        // would report one written mistake once per including message.
        foreach (var mixin in file.mixinSyntaxes)
        foreach (var field in mixin.Fields)
        foreach (var site in Nested(new(field.Type, "field", mixin, Member: field)))
            yield return site;

        foreach (var svc in file.serviceSyntaxes)
        {
            // Base arguments are declared once and prepended to every method — yield them once.
            foreach (var arg in svc.BaseArguments)
            foreach (var site in Nested(new(arg.type, "argument", svc, Member: arg)))
                yield return site;

            foreach (var method in svc.Methods)
            {
                foreach (var arg in method.arguments)
                foreach (var site in Nested(new(arg.type, "argument", svc, method, arg)))
                    yield return site;

                if (method.returnType is not null)
                    foreach (var site in Nested(new(method.returnType, "return type", svc, method)))
                        yield return site;
            }
        }

        foreach (var union in file.unionSyntaxes)
        {
            foreach (var shared in union.baseFields)
            foreach (var site in Nested(new(shared.type, "shared field", union, Member: shared)))
                yield return site;

            foreach (var @case in union.cases)
            {
                if (@case.IsTypeRef)
                    foreach (var site in Nested(new(@case.caseName, "case", union, Member: @case)))
                        yield return site;

                foreach (var arg in @case.arguments)
                foreach (var site in Nested(new(arg.type, "field", union, @case, arg)))
                    yield return site;
            }
        }

        foreach (var typedef in file.typedefSyntaxes)
            if (typedef.BaseType is not null)
                foreach (var site in Nested(new(typedef.BaseType, "underlying type", typedef)))
                    yield return site;

        // `enum E : i4` / `flags F : u4` — the base type uses the full Type parser, so a modifier
        // can be written there even though TransformStage resolves it with ResolveBuiltinType and
        // never calls WrapModifiers.
        foreach (var @enum in file.enumSyntaxes)
        foreach (var site in Nested(new(@enum.Type, "base type", @enum)))
            yield return site;

        foreach (var flags in file.flagsSyntaxes)
        foreach (var site in Nested(new(flags.Type, "base type", flags)))
            yield return site;

        foreach (var attr in file.attributeDefSyntaxes)
        foreach (var arg in attr.Args)
        foreach (var site in Nested(new(arg.type, "parameter", attr, Member: arg)))
            yield return site;
    }

    /// <summary>
    /// <paramref name="site"/> followed by every type written inside it — generic arguments,
    /// transitively, and the field types of an inline anonymous body.
    /// </summary>
    /// <remarks>
    /// Outermost first, so a validator that reports on both an enclosing type and one of its
    /// arguments reports them in the order they were written.
    /// <para>
    /// <c>IonTypeParameterSyntax.Type</c> is the whole argument and <c>.Name</c> is only its head
    /// name, so recursing on <c>Type</c> is what makes <c>Map&lt;string, Array&lt;User&gt;&gt;</c>
    /// reachable at all. <c>Type</c> is nullable purely so the one-argument positional constructor
    /// still compiles; the parser always fills it in, and the fallback keeps a hand-synthesized node
    /// from throwing.
    /// </para>
    /// </remarks>
    private static IEnumerable<IonTypeSite> Nested(IonTypeSite site)
    {
        yield return site;

        foreach (var generic in site.Site.generics)
        {
            if (generic.Type is not { } argument)
                continue;

            foreach (var inner in Nested(site with { Site = argument }))
                yield return inner;
        }

        // Only reachable for an inline body that `InlineTypeHoistingStage` could not hoist (it
        // reports ION0068 and leaves the body in place). After hoisting there are none left in a
        // well-formed file, but a validator must not go quiet just because another stage failed.
        if (site.Site.InlineBody is not { } body)
            yield break;

        foreach (var field in body.Fields)
        foreach (var inner in Nested(site with
                 {
                     Site = field.Type,
                     Container = site.Member ?? site.Container,
                     Member = field
                 }))
            yield return inner;
    }

    /// <summary>
    /// Names a site the way a diagnostic should refer to it — "the field 'owner' of msg 'Doc'",
    /// "the argument 'id' of method 'Get' of service 'Api'".
    /// </summary>
    public static string Describe(in IonTypeSite site)
    {
        var name = NameOf(site.Member);
        var parts = new List<string>(3)
        {
            name is null ? $"the {site.Role}" : $"the {site.Role} '{name}'"
        };

        if (site.Container is not null)
            parts.Add(Describe(site.Container));

        parts.Add(Describe(site.Declaration));

        return string.Join(" of ", parts);
    }

    /// <summary>Names any declaration or member — "msg 'Doc'", "field 'owner'", "attribute '@old'".</summary>
    public static string Describe(IonSyntaxMember member) => member switch
    {
        IonMessageSyntax m => $"msg '{m.Name.Identifier}'",
        IonServiceSyntax s => $"service '{s.serviceName.Identifier}'",
        IonUnionSyntax u => $"union '{u.unionName.Identifier}'",
        IonTypedefSyntax t => $"typedef '{t.TypeName.Name.Identifier}'",
        IonEnumSyntax e => $"enum '{e.Name.Identifier}'",
        IonFlagsSyntax f => $"flags '{f.Name.Identifier}'",
        IonAttributeDefSyntax a => $"attribute '@{a.Name.Identifier}'",
        IonMethodSyntax m => $"method '{m.methodName.Identifier}'",
        IonMixinSyntax x => $"mixin '{x.Name.Identifier}'",
        IonUnionTypeCaseSyntax c => $"case '{c.caseName.Name.Identifier}'",
        IonFieldSyntax f => $"field '{f.Name.Identifier}'",
        IonArgumentSyntax a => $"argument '{a.argName.Identifier}'",
        IonFlagEntrySyntax e => $"member '{e.Name.Identifier}'",
        _ => "declaration"
    };

    private static string? NameOf(IonSyntaxMember? member) => member switch
    {
        IonFieldSyntax f => f.Name.Identifier,
        IonArgumentSyntax a => a.argName.Identifier,
        IonUnionTypeCaseSyntax c => c.caseName.Name.Identifier,
        IonMethodSyntax m => m.methodName.Identifier,
        _ => null
    };

    /// <summary>The type as the author wrote it, minus its own trailing modifier suffixes.</summary>
    /// <remarks>
    /// <para>
    /// Recurses through <see cref="IonTypeParameterSyntax.Type"/>, not
    /// <see cref="IonTypeParameterSyntax.Name"/>. <c>Name</c> is the argument's <em>head</em> name
    /// only, so reading it rendered <c>Map&lt;string, Array&lt;User&gt;&gt;</c> as
    /// <c>Map&lt;string, Array&gt;</c> and any nested modifier vanished — which meant every ION0010
    /// / ION0018 / ION0019 message about a nested argument quoted a type the author had not written.
    /// </para>
    /// <para>
    /// An inline body renders as <c>msg { … }</c> rather than as the unlexable
    /// <see cref="IonUnderlyingTypeSyntax.InlineTypeName"/> placeholder. Nested modifiers <em>are</em>
    /// included, unlike the outermost ones, because they are part of the argument as written.
    /// </para>
    /// </remarks>
    public static string NameAsWritten(IonUnderlyingTypeSyntax site)
    {
        var head = site.IsInline ? "msg { … }" : site.Name.Identifier;

        return site.generics.Count == 0
            ? head
            : $"{head}<{string.Join(", ", site.generics.Select(ArgumentAsWritten))}>";
    }

    /// <summary>One generic argument as written, its own modifier suffixes included.</summary>
    private static string ArgumentAsWritten(IonTypeParameterSyntax generic) =>
        generic.Type is { } argument
            ? NameAsWritten(argument) + string.Concat(argument.ModifierTokens ?? [])
            : generic.Name.Identifier;

    /// <summary>The type as written, its own modifier suffixes included — <c>Data?~</c>.</summary>
    public static string AsWritten(IonUnderlyingTypeSyntax site) =>
        NameAsWritten(site) + string.Concat(site.ModifierTokens ?? []);
}

/// <summary>
/// Validates the <em>spelling</em> of the type modifier suffixes <c>~</c>, <c>[]</c> and <c>?</c>:
/// no suffix may repeat, and they must be written in the canonical order <c>~</c>, <c>[]</c>,
/// <c>?</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>CompilationContext.WrapModifiers</c> applies Partial innermost, then Array, then Maybe, and it
/// reads three <see cref="bool"/>s. Two things follow, and both used to be silent:
/// </para>
/// <list type="bullet">
/// <item>
/// A repeat is swallowed. <c>Data~~</c>, <c>Data??</c> and <c>Data[][]</c> all collapse to the
/// single-modifier form, so the author gets a different type than they wrote with no indication.
/// <c>Partial&lt;Partial&lt;T&gt;&gt;</c> and <c>T[][]</c> are simply unrepresentable —
/// <see cref="IonAnalyticCodes.ION0019_DuplicateTypeModifier"/>.
/// </item>
/// <item>
/// Written order is discarded. <c>Data?~</c>, <c>Data~?</c>, <c>Data?[]~</c> and <c>Data~[]?</c> all
/// lower identically, so three of those four spellings describe something the compiler does not do.
/// Canonical order is fixed so that reading left to right matches the inside-out wrapping —
/// <c>Data~[]?</c> is "partial, then an array of them, then optional" =
/// <c>Maybe&lt;Array&lt;Partial&lt;Data&gt;&gt;&gt;</c>, which is exactly what
/// <c>WrapModifiers</c> builds — <see cref="IonAnalyticCodes.ION0010_TypeModifierOutOfOrder"/>.
/// </item>
/// </list>
/// <para>
/// <strong>Why a stage and not the grammar.</strong> Both are diagnosable at parse time, and both
/// would be the wrong thing to fail on: a Pidgin failure aborts the enclosing declaration, so a
/// stray second <c>~</c> would swallow every remaining field and cascade into a wall of unrelated
/// errors. The parser records the evidence
/// (<see cref="IonUnderlyingTypeSyntax.ModifierTokens"/>) and keeps going.
/// </para>
/// <para>
/// <strong>Why it runs early.</strong> The check is purely syntactic — it never resolves a name — so
/// it is registered before <c>TransformStage</c>. A misspelled type name and a doubled modifier are
/// independent mistakes and both are worth reporting on the same run.
/// </para>
/// <para>
/// <strong>And before <see cref="InlineTypeHoistingStage"/>.</strong> Every message here echoes the
/// type as written, and <see cref="IonTypeSites.NameAsWritten"/> renders an inline body as
/// <c>msg { … }</c> — which it can only do while the body is still attached. Registered after
/// hoisting, as it was, this stage read the name the hoist had just derived and told the author to
/// "write 'MM~?'" about a type that appears nowhere in their file, while an inline body that
/// happened to be un-hoistable (<c>Array&lt;msg { … }[0]&gt;</c>, already ION0068) echoed correctly
/// beside it. Judging the written tree before anything rewrites it is what makes the two agree.
/// </para>
/// </remarks>
public sealed class TypeModifierValidationStage(CompilationContext context) : CompilationStage(context)
{
    public override string StageName => "Type Modifier Validation";
    public override string StageDescription => "Checking the order and repetition of type modifiers '~', '[]' and '?'";

    /// <summary>Collect every bad spelling, don't stop at the first.</summary>
    public override bool StopOnError => false;

    /// <summary>
    /// Canonical order, outermost wrapper last. Index doubles as the sort rank.
    /// </summary>
    /// <remarks>
    /// Mirrors the wrapping order in <c>CompilationContext.WrapModifiers</c> exactly: Partial, then
    /// Array, then Maybe. If that ever changes, this array changes with it.
    /// </remarks>
    private static readonly string[] CanonicalOrder = ["~", "[]", "?"];

    public override void DoProcess()
    {
        foreach (var file in Context.Files)
        foreach (var site in IonTypeSites.Of(file))
        {
            Validate(site);
            ValidateArraySize(site);
        }
    }

    /// <summary>
    /// Rejects <c>T[0]</c> and <c>T[-3]</c>.
    /// </summary>
    /// <remarks>
    /// Lives beside the suffix spelling rules because <c>[N]</c> <em>is</em> a suffix and the check
    /// needs no name resolution — a size is wrong at whatever it is an array of.
    /// <para>
    /// Reported even when the suffixes are also misspelled: a repeat or a bad order is about which
    /// wrappers were built, and the size is about a number, so neither message subsumes the other.
    /// </para>
    /// </remarks>
    private void ValidateArraySize(IonUnderlyingTypeSyntax site)
    {
        if (site.ArraySize is not { } size || size >= 1)
            return;

        Error(IonAnalyticCodes.ION0062_FixedArraySizeNotPositive, SizeAnchor(site), AsWrittenWithSize(site, size),
            size);
    }

    /// <summary>
    /// The <c>[N]</c> suffix itself, so the squiggle lands on the size and not on the element type,
    /// which is not the part that is wrong.
    /// </summary>
    /// <remarks>
    /// Falls back to the type name when the parser recorded no sub-token span — a synthesized node
    /// cannot carry a bad size, but a diagnostic must never depend on that being true.
    /// </remarks>
    private static IonSyntaxBase SizeAnchor(IonUnderlyingTypeSyntax site) =>
        site.ArraySizeStart is { } start
            ? new IonSyntaxBase
            {
                StartPosition = start,
                EndPosition = site.ArraySizeEnd,
                SourceFile = site.SourceFile
            }
            : site.Name;

    /// <summary>
    /// The type as written with the size put back into the array suffix — <c>Data~[0]?</c>.
    /// </summary>
    /// <remarks>
    /// <c>ModifierTokens</c> normalizes <c>[0]</c> to <c>"[]"</c> so that the repeat and order rules
    /// above keep working (see <see cref="IonUnderlyingTypeSyntax.ModifierTokens"/>); that
    /// normalization is load-bearing and stays. This puts the size back for display only, into the
    /// first array token, which is the one <c>ArraySize</c> came from.
    /// </remarks>
    private static string AsWrittenWithSize(IonUnderlyingTypeSyntax site, int size)
    {
        var rendered = IonTypeSites.NameAsWritten(site);
        var replaced = false;

        foreach (var token in site.ModifierTokens ?? [])
        {
            if (token == "[]" && !replaced)
            {
                rendered += $"[{size}]";
                replaced = true;
                continue;
            }

            rendered += token;
        }

        return replaced ? rendered : $"{rendered}[{size}]";
    }

    private void Validate(IonUnderlyingTypeSyntax site)
    {
        // null = a synthesized node (e.g. an enum's implicit `u4` base type) that was never written
        // by anybody, so there is nothing to spell wrong. One modifier can be neither repeated nor
        // out of order.
        var tokens = site.ModifierTokens;
        if (tokens is null || tokens.Count < 2)
            return;

        var repeated = tokens
            .GroupBy(t => t, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(Rank)
            .ToList();

        if (repeated.Count > 0)
        {
            // One diagnostic per repeated token: `Data~~??` is two distinct mistakes. The ordering
            // check is deliberately skipped — the canonical spelling offered below is already the
            // de-duplicated *and* reordered one, so ION0010 would only restate the same fix.
            foreach (var token in repeated)
                Error(IonAnalyticCodes.ION0019_DuplicateTypeModifier, site.Name,
                    AsWritten(site), token, Lowered(site), Canonical(site));

            return;
        }

        // No repeats, so "non-decreasing" and "strictly increasing" coincide.
        var ranks = tokens.Select(Rank).ToList();
        var ordered = ranks.Zip(ranks.Skip(1)).All(pair => pair.First < pair.Second);

        if (!ordered)
            Error(IonAnalyticCodes.ION0010_TypeModifierOutOfOrder, site.Name,
                AsWritten(site), Lowered(site), Canonical(site));
    }

    private static int Rank(string token) => Array.IndexOf(CanonicalOrder, token);

    /// <summary>The type exactly as the author wrote it, modifiers included — <c>Data?~</c>.</summary>
    private static string AsWritten(IonUnderlyingTypeSyntax site) => IonTypeSites.AsWritten(site);

    /// <summary>
    /// The same set of modifiers, de-duplicated and in canonical order — <c>Data~?</c>. This is the
    /// spelling that produces <see cref="Lowered"/>, which is what the author already got.
    /// </summary>
    private static string Canonical(IonUnderlyingTypeSyntax site) =>
        IonTypeSites.NameAsWritten(site) +
        string.Concat(CanonicalOrder.Where(t => (site.ModifierTokens ?? []).Contains(t, StringComparer.Ordinal)));

    /// <summary>
    /// The IR the site actually lowers to. Mirrors <c>CompilationContext.WrapModifiers</c>: Partial
    /// innermost, then Array, then Maybe.
    /// </summary>
    private static string Lowered(IonUnderlyingTypeSyntax site)
    {
        var lowered = IonTypeSites.NameAsWritten(site);

        if (site.IsPartial)
            lowered = $"Partial<{lowered}>";

        if (site.IsArray)
            lowered = $"Array<{lowered}>";

        if (site.IsOptional)
            lowered = $"Maybe<{lowered}>";

        return lowered;
    }
}
