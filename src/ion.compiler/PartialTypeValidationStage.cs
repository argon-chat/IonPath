namespace ion.compiler;

using runtime;
using syntax;

/// <summary>
/// Validates the target of the partial modifier — <c>T~</c> in source, lowered to
/// <c>Partial&lt;T&gt;</c> by <see cref="CompilationContext"/>'s <c>WrapModifiers</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Partial&lt;T&gt;</c> is a sparse patch over <c>T</c>: for each field of <c>T</c>, either
/// untouched, modified, or cleared. It is the one Ion type whose wire form is a CBOR <em>map</em>
/// keyed by field name rather than a positional array (see <c>tests/golden/partial.golden.json</c>).
/// That only means anything for a type that <em>has</em> a field set, so the modifier is legal over
/// a user-defined <c>msg</c> and over nothing else. Everything rejected here used to compile: the
/// old formatter enumerated the CLR type's properties, found none, and wrote an empty map forever.
/// </para>
/// <para>
/// The walk is over the <em>syntax</em> tree rather than the IR, for three reasons:
/// </para>
/// <list type="bullet">
/// <item>
/// It keys off <see cref="IonUnderlyingTypeSyntax.IsPartial"/> — the literal <c>~</c> token — and
/// never off <see cref="IonType.IsPartial"/>, which is a bare name check
/// (<c>name.Identifier == "Partial"</c>) that a user-defined <c>msg Partial { … }</c> would trip.
/// </item>
/// <item>
/// Positions. The IR keeps no source position for a resolved type, so an IR walk could only point
/// at the owning field/argument name. A syntax node points at the type itself.
/// </item>
/// <item>
/// Service base arguments are prepended to <em>every</em> method by
/// <c>TransformStage.PrependMethods</c>, so the same declaration appears once per method in the IR
/// and would be reported once per method. In the syntax tree it appears exactly once.
/// </item>
/// </list>
/// <para>
/// The set of visited positions mirrors <c>UnusedSymbolDetectionStage.CollectReferencedTypeNames</c>,
/// which is the existing syntax-level "every place a type can be written" traversal.
/// </para>
/// <para>
/// The stage must run after <see cref="RestoreUnresolvedTypeStage"/>: typedefs are transparent
/// aliases erased there, so <c>typedef UserId = u4; x: UserId~;</c> has to be reported as a patch
/// over a scalar, and by that point a typedef declaration's own <c>Value</c> field already holds
/// the fully collapsed underlying type.
/// </para>
/// <para>
/// <strong>Not this stage's job.</strong> <c>Data~~</c> still lowers to a plain
/// <c>Partial&lt;Data&gt;</c> — <c>WrapModifiers</c> reads a <see cref="bool"/>, so the second tilde
/// changes nothing — and is reported as
/// <see cref="IonAnalyticCodes.ION0019_DuplicateTypeModifier"/> by
/// <see cref="TypeModifierValidationStage"/>, off the raw
/// <see cref="IonUnderlyingTypeSyntax.ModifierTokens"/> the parser now keeps. That is a spelling
/// check and needs no name resolution, so it runs long before this stage.
/// <c>Partial&lt;Partial&lt;T&gt;&gt;</c> remains unrepresentable; the one route that does reach it
/// — a partial over an alias of a partial, <c>typedef P = Data~; z: P~;</c> — is rejected below.
/// </para>
/// </remarks>
public sealed class PartialTypeValidationStage(CompilationContext context) : CompilationStage(context)
{
    public override string StageName => "Partial Validation";
    public override string StageDescription => "Checking the targets of the partial modifier '~'";

    /// <summary>Collect every bad partial, don't stop at the first.</summary>
    public override bool StopOnError => false;

    /// <summary>Guards a typedef cycle that <see cref="IonAnalyticCodes.ION0017_CircularTypedef"/> already reported.</summary>
    private const int MaxAliasHops = 32;

    public override void DoProcess()
    {
        foreach (var file in Context.Files)
        foreach (var site in PartialSites(file))
            Validate(site);
    }

    // ── Traversal ──────────────────────────────────────────────────────

    /// <summary>
    /// Every position in <paramref name="file"/> where a type was written with a <c>~</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk itself lives in <see cref="IonTypeSites"/>, shared with
    /// <see cref="TypeModifierValidationStage"/> so the two validators cannot drift apart about
    /// where a type may appear. This stage only filters it.
    /// </para>
    /// <para>
    /// <c>?</c> and <c>[]</c> need no separate handling: <c>Data~</c>, <c>Data~?</c>, <c>Data~[]</c>
    /// and <c>Data~[]?</c> are all a single <see cref="IonUnderlyingTypeSyntax"/> whose
    /// <see cref="IonUnderlyingTypeSyntax.Name"/> is <c>Data</c>. That matches the lowering exactly
    /// — <c>WrapModifiers</c> applies <c>Partial</c> innermost, so the partial always targets the
    /// written name and never the <c>Array</c> / <c>Maybe</c> wrapper around it.
    /// </para>
    /// </remarks>
    private static IEnumerable<IonUnderlyingTypeSyntax> PartialSites(IonFileSyntax file) =>
        IonTypeSites.Of(file).Where(site => site.IsPartial);

    // ── Checking ───────────────────────────────────────────────────────

    private void Validate(IonUnderlyingTypeSyntax site)
    {
        var declared = Lookup(site.Name.Identifier);

        // Unknown name: ION0009 is already on it. Never stack a second error on the same typo.
        if (declared is null || declared.IsUnresolved)
            return;

        var (target, alias) = Erase(declared);

        // Unresolvable alias chain (ION0009) or a typedef cycle (ION0017) — already reported.
        if (target is null || target.IsUnresolved)
            return;

        var reason = Describe(target);
        if (reason is null)
            return; // A user-defined msg: the one legal target.

        var detail = alias is null
            ? $"it is {reason}"
            : $"it is an alias for '{SchemaLockGenerator.GetCanonicalTypeName(target)}', which is {reason}";

        Error(IonAnalyticCodes.ION0018_PartialOverNonMessage, site.Name,
            IonTypeSites.NameAsWritten(site), detail);
    }

    /// <summary>
    /// Why <paramref name="type"/> cannot be patched, as a phrase that reads on from "it is " /
    /// "which is ", or <see langword="null"/> when it is a user-defined <c>msg</c> and therefore
    /// a legal target.
    /// </summary>
    /// <remarks>
    /// A <c>msg</c> is what is left over: a non-builtin, non-enum, non-flags, non-union,
    /// non-generic <see cref="IonType"/>. Note that <c>isTypedef</c> is useless as a discriminator
    /// here — every std builtin is declared with <c>isTypedef: true</c>, because that flag is the
    /// fourth positional argument of <c>IonType(name, attributes, fields, isTypedef)</c> and
    /// <c>IonModule.GetStdModule</c> passes <c>true</c> for all of them. Aliases are stripped by
    /// <see cref="Erase"/> before this is ever called.
    /// </remarks>
    private static string? Describe(IonType type) => type switch
    {
        IonUnion => "a union, and a union's shape is a discriminated case rather than a field set",
        IonEnum => "an enum and has no fields to patch",
        IonFlags => "a flags type and has no fields to patch",

        // Order matters: Maybe / Array / Partial are builtin *generics*, so they must be classified
        // before the plain builtin arms below.
        IonGenericType { IsPartial: true } => "already a partial, and 'Partial<Partial<T>>' has no wire representation",
        IonGenericType { IsMaybe: true } => "an optional type; write 'T~?' to make the patch itself optional",
        IonGenericType { IsArray: true } => "an array type; write 'T~[]' for an array of patches",
        IonGenericType => "a generic type",

        { IsVoid: true } => "the void type and has no fields to patch",
        { IsScalar: true, IsBuiltin: true } => "a builtin scalar type and has no fields to patch",
        { IsBuiltin: true } => "a builtin type and has no fields to patch",

        _ => null
    };

    /// <summary>
    /// Resolves a written type name the same way the type checker does.
    /// </summary>
    /// <remarks>
    /// The precedence deliberately mirrors <see cref="CompilationContext.ResolveTypeFor"/>: builtins
    /// from the std / feature modules win over everything declared in the project. That is why a
    /// user-defined <c>msg Partial { … }</c> cannot shadow the builtin <c>Partial&lt;T&gt;</c>
    /// wrapper — a bare <c>Partial</c> reference resolves to the wrapper both here and in the type
    /// checker, so validation and lowering can never disagree about what a name means.
    /// </remarks>
    private IonType? Lookup(string name)
    {
        var builtin = Context.GlobalModules
            .SelectMany(m => m.Definitions)
            .FirstOrDefault(d => d.IsBuiltin && d.name.Identifier == name);

        if (builtin is not null)
            return builtin;

        var local = Context.ProcessedModules
            .SelectMany(m => m.Definitions)
            .FirstOrDefault(d => d.name.Identifier == name);

        return local ?? Context.ExternalModules
            .SelectMany(m => m.Definitions)
            .FirstOrDefault(d => d.name.Identifier == name);
    }

    /// <summary>
    /// Collapses a typedef alias to the type it stands for.
    /// </summary>
    /// <returns>
    /// The erased target and the alias it was reached through (<see langword="null"/> when
    /// <paramref name="declared"/> was not an alias). A <see langword="null"/> target means the
    /// chain did not terminate — a typedef cycle, already reported as ION0017.
    /// </returns>
    /// <remarks>
    /// One hop is normally enough: <see cref="RestoreUnresolvedTypeStage"/> rewrites a typedef
    /// declaration's own <c>Value</c> field to the fully collapsed underlying type, so
    /// <c>typedef A = B; typedef B = u4;</c> leaves <c>u4</c> in <c>A</c>. The loop and
    /// <see cref="MaxAliasHops"/> exist so a cycle that survived erasure cannot spin here.
    /// </remarks>
    private static (IonType? target, IonType? alias) Erase(IonType declared)
    {
        if (!RestoreUnresolvedTypeStage.IsTypedefAlias(declared))
            return (declared, null);

        var current = declared;

        for (var hop = 0; hop < MaxAliasHops && RestoreUnresolvedTypeStage.IsTypedefAlias(current); hop++)
            current = current.fields[0].type;

        return RestoreUnresolvedTypeStage.IsTypedefAlias(current)
            ? (null, declared)
            : (current, declared);
    }
}
