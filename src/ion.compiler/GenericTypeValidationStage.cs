namespace ion.compiler;

using runtime;
using syntax;

/// <summary>
/// Checks the two things a generic <em>use</em> can get wrong: how many arguments it was given
/// (ION0060), and — for <c>Map&lt;K, V&gt;</c> — whether <c>K</c> can actually be a key (ION0061).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Arity.</strong> There was no arity check anywhere before this. <c>Maybe&lt;A, B&gt;</c>
/// resolved silently, writing two arguments into a one-parameter definition, and a bare
/// <c>Array</c> lowered to an open generic with no element type that every generator then had to
/// guess at. The rule reads <c>IonGenericType.TypeParameters.Count</c> off the definition, so
/// <c>Map</c> and <c>Set</c> are checked by exactly the same code as the three wrappers that
/// predate them.
/// </para>
/// <para>
/// <strong>Map keys.</strong> <c>IonMapFormatter&lt;TKey, TValue&gt;</c> encodes whatever the key
/// formatter emits and orders the entries by those bytes. It never asks whether the encoding is
/// canonical, or whether the decoded key has value equality in the target language — so ION0061 is
/// the only thing that can. The line, and why floats are on the wrong side of it despite being
/// scalar builtins, is on <see cref="IonModule.MapKeyBuiltins"/>.
/// </para>
/// <para>
/// <strong>Why the syntax walk.</strong> Same three reasons as
/// <see cref="PartialTypeValidationStage"/>: the check is about what was <em>written</em>, the IR
/// keeps no source position, and a service's base arguments appear once per method in the IR and
/// exactly once here. <see cref="IonTypeSites"/> now yields nested generic arguments as sites of
/// their own, so the <c>Set&lt;i4, i4&gt;</c> inside a <c>Map&lt;string, Set&lt;i4, i4&gt;&gt;</c>
/// is reached on the same pass.
/// </para>
/// <para>
/// <strong>Position.</strong> After <see cref="RestoreUnresolvedTypeStage"/>, so that a typedef used
/// as a key (<c>typedef UserId = string; Map&lt;UserId, V&gt;</c>) is judged on what it erases to —
/// a typedef is transparent, and the wire sees the underlying type.
/// </para>
/// </remarks>
public sealed class GenericTypeValidationStage(CompilationContext context) : CompilationStage(context)
{
    public override string StageName => "Generic Validation";
    public override string StageDescription => "Checking generic arity and Map key types";

    /// <summary>Collect every bad use, don't stop at the first.</summary>
    public override bool StopOnError => false;

    /// <summary>Guards a typedef cycle that ION0017 already reported. Mirrors <see cref="PartialTypeValidationStage"/>.</summary>
    private const int MaxAliasHops = 32;

    public override void DoProcess()
    {
        foreach (var file in Context.Files)
        foreach (var site in IonTypeSites.Of(file))
        {
            ValidateArity(site);
            ValidateMapKey(site);
        }
    }

    // ── Arity ──────────────────────────────────────────────────────────

    private void ValidateArity(IonUnderlyingTypeSyntax site)
    {
        // An inline body that could not be hoisted carries the unlexable `$inline` placeholder and
        // resolves to nothing; ION0068 is already on it.
        if (site.IsInline)
            return;

        var declared = Lookup(site.Name.Identifier);

        // Unknown (ION0009) or a mixin in type position (ION0066) — never stack on either.
        if (declared is null || declared.IsUnresolved)
            return;

        var written = site.generics.Count;
        var expected = declared is IonGenericType generic ? generic.TypeParameters.Count : 0;

        if (written == expected)
            return;

        if (expected == 0)
        {
            Error(IonAnalyticCodes.ION0060_TypeIsNotGeneric, site.Name, site.Name.Identifier, written);
            return;
        }

        Error(IonAnalyticCodes.ION0060_GenericArityMismatch, site.Name,
            site.Name.Identifier, expected, written, Signature((IonGenericType)declared));
    }

    /// <summary>The definition's own spelling — <c>Map&lt;K, V&gt;</c> — so the fix names the slots.</summary>
    private static string Signature(IonGenericType definition) =>
        $"{definition.name.Identifier}<{string.Join(", ", definition.TypeParameters.Select(p => p.Name.Identifier))}>";

    // ── Map keys ───────────────────────────────────────────────────────

    private void ValidateMapKey(IonUnderlyingTypeSyntax site)
    {
        if (site.IsInline || site.generics.Count == 0)
            return;

        // Resolved rather than name-matched: `IsMap` is a bare name test, and the check must agree
        // with what the type checker thinks `Map` means at this site.
        if (Lookup(site.Name.Identifier) is not IonGenericType { IsMap: true, IsBuiltin: true })
            return;

        if (site.generics[0].Type is not { } key)
            return;

        // The same early return `ValidateArity` makes, and for the same reason. An inline body
        // written as a generic argument is not a hoistable position at all — ION0068 owns it, on
        // this very span — so asking whether it would make a good key answers a question the author
        // is not being asked to fix, and answered it in a second diagnostic on the same characters.
        if (key.IsInline)
            return;

        if (DescribeKey(key) is not { } reason)
            return;

        Error(IonAnalyticCodes.ION0061_MapKeyTypeNotAllowed, key.Name, IonTypeSites.AsWritten(key), reason);
    }

    /// <summary>
    /// Why <paramref name="key"/> cannot be a key, as a phrase reading on from "…: ", or
    /// <see langword="null"/> when it is a legal key.
    /// </summary>
    /// <remarks>
    /// The written modifiers are judged before the name is even resolved, because
    /// <c>WrapModifiers</c> is what turns <c>string?</c> into a <c>Maybe&lt;string&gt;</c> and the
    /// wrapper, not <c>string</c>, is what would go on the wire as the key.
    /// <para>
    /// There is deliberately no arm for an inline body. It would read well — "a field set with no
    /// value identity to key on" — but it was unreachable except behind an ION0068 that had already
    /// fired on the same span, because an inline type in a generic argument is rejected for having
    /// no name long before anyone asks what it would be a key for. <see cref="ValidateMapKey"/>
    /// returns before this is called.
    /// </para>
    /// </remarks>
    private string? DescribeKey(IonUnderlyingTypeSyntax key)
    {
        if (key.IsOptional)
            return "it is optional ('T?'), and an absent key is not a key";

        if (key.IsPartial)
            return "it is a partial ('T~'), a sparse patch over a message rather than a value";

        if (key.IsArray)
            return "it is an array, which has no canonical byte order and no value equality in generated code";

        var declared = Lookup(key.Name.Identifier);

        // Unknown name (ION0009) or a mixin (ION0066) — already reported, never stack.
        if (declared is null || declared.IsUnresolved)
            return null;

        var target = Erase(declared);

        if (target is null || target.IsUnresolved)
            return null; // Typedef cycle — ION0017 owns it.

        if (DescribeKeyTarget(target) is not { } reason)
            return null;

        // A typedef is transparent on the wire, so the key is judged on what it erases to — but the
        // author wrote the alias, so the message says which alias and what it stands for.
        return ReferenceEquals(target, declared)
            ? $"it is {reason}"
            : $"it is an alias for '{SchemaLockGenerator.GetCanonicalTypeName(target)}', which is {reason}";
    }

    /// <summary>
    /// The same question about a resolved, alias-free type, as a phrase reading on from "it is " /
    /// "which is ", or <see langword="null"/> when the type is a legal key.
    /// </summary>
    private static string? DescribeKeyTarget(IonType type) => type switch
    {
        // An enum is the one non-builtin key: an integral base type, and a closed set of named
        // values that every target can express as a dictionary key with value equality.
        IonEnum => null,

        IonUnion => "a union, whose value is one of several shapes rather than a single comparable value",
        IonFlags => "a flags type, whose value set is an open combination of bits rather than the closed, named set an enum gives generated code",

        // Order matters: the builtin generics must be classified before the plain builtin arms.
        IonGenericType { IsMap: true } => "a Map, an aggregate with no canonical byte order",
        IonGenericType { IsSet: true } => "a Set, an aggregate with no canonical byte order",
        IonGenericType { IsArray: true } => "an array, which has no canonical byte order and no value equality in generated code",
        IonGenericType { IsMaybe: true } => "optional, and an absent key is not a key",
        IonGenericType { IsPartial: true } => "a partial, a sparse patch over a message rather than a value",
        IonGenericType => "a generic type",

        { IsVoid: true } => "the void type, which has no values",

        _ when IonModule.IsMapKeyBuiltin(type) => null,

        { IsBuiltin: true } => DescribeKeyBuiltin(type.name.Identifier),

        // What is left is a user-defined msg.
        _ => "a message, and a message has neither a canonical byte encoding nor value equality in generated code"
    };

    private static string DescribeKeyBuiltin(string name) => name switch
    {
        "f2" or "f4" or "f8" =>
            "a floating point type — '-0.0' and '0.0' encode differently but compare equal, and 'NaN' " +
            "does not compare equal to itself, so a decoded map would gain or lose entries",

        "decimal" or "bigint" =>
            "arbitrary precision, so one value has more than one valid encoding and ordering by bytes " +
            "is not ordering by value",

        "bytes" =>
            "a byte string, which every target maps to a reference-equality type ('byte[]', " +
            "'Uint8Array') that cannot serve as a dictionary key",

        "datetime" or "dateonly" or "timeonly" or "uri" =>
            "a builtin whose generated form has reference equality in at least one target, so a " +
            "decoded map would not find its own keys",

        _ => "a builtin that is not in the key set"
    };

    // ── Resolution ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a written type name with the type checker's precedence — builtins first, then the
    /// project, then imported modules. Mirrors <c>PartialTypeValidationStage.Lookup</c>.
    /// </summary>
    /// <remarks>
    /// Memoized, unlike its two siblings, because this stage is the first to resolve <em>every</em>
    /// type site rather than a filtered subset — a partial site, a deprecated name — and the
    /// underlying lookup is a linear scan of every definition in scope.
    /// </remarks>
    private IonType? Lookup(string name) =>
        _lookups.TryGetValue(name, out var cached) ? cached : _lookups[name] = Resolve(name);

    private readonly Dictionary<string, IonType?> _lookups = new(StringComparer.Ordinal);

    private IonType? Resolve(string name)
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

    /// <summary>Collapses a typedef alias, or <see langword="null"/> when the chain does not terminate.</summary>
    private static IonType? Erase(IonType declared)
    {
        if (!RestoreUnresolvedTypeStage.IsTypedefAlias(declared))
            return declared;

        var current = declared;

        for (var hop = 0; hop < MaxAliasHops && RestoreUnresolvedTypeStage.IsTypedefAlias(current); hop++)
            current = current.fields[0].type;

        return RestoreUnresolvedTypeStage.IsTypedefAlias(current) ? null : current;
    }
}
