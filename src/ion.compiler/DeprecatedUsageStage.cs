namespace ion.compiler;

using ion.runtime;
using syntax;

/// <summary>
/// Warns (ION1004) wherever a schema references something marked <c>@deprecated</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>@deprecated</c> was a declared attribute with no parameters and no consequences: it parsed,
/// it lowered, and nothing ever read it. This is the half that makes it mean something — a
/// deprecated declaration that nobody is told about is a comment.
/// </para>
/// <para>
/// <strong>What is flagged.</strong> Every <em>written type reference</em>, from the shared
/// <see cref="IonTypeSites"/> walk: a field's type, a method's return type or argument, a service
/// base argument, a union's shared field, a union case (both <c>case Data</c> and the fields of an
/// inline case), a typedef's underlying type, an enum or flags base type, and an attribute
/// parameter's type. Plus one thing that is not a type: a <em>use</em> of an attribute whose own
/// declaration is deprecated, which is what makes <c>@deprecated</c> on <c>on attribute</c> mean
/// something.
/// </para>
/// <para>
/// <strong>What is not.</strong> The deprecated declaration itself — <see cref="IonTypeSites"/>
/// yields references, never declaration names, so <c>@deprecated msg Old { … }</c> is silent about
/// itself. Neither is a reference made from inside something that is itself deprecated: a field of
/// a deprecated msg, an argument of a deprecated method, a deprecated typedef's underlying type.
/// Whoever wrote the deprecation already knows, and the alternative is that deprecating a type
/// floods its own body with warnings about itself. Nor is a *service method call*, because Ion has
/// no call sites — a service is the boundary, and the consumer is in another language.
/// </para>
/// <para>
/// <strong>Position.</strong> Runs after <see cref="RestoreUnresolvedTypeStage"/> so that every
/// name has a definition to look at, but it deliberately does <em>not</em> erase typedef aliases:
/// the warning belongs on the name that was written. <c>typedef Alias = OldMsg;</c> is warned once,
/// at the typedef; the fields that then use <c>Alias</c> are using a live alias and are not the ones
/// that need changing.
/// </para>
/// </remarks>
public sealed class DeprecatedUsageStage(CompilationContext context) : CompilationStage(context)
{
    public override string StageName => "Deprecation";
    public override string StageDescription => "Reporting references to deprecated declarations";

    /// <summary>Advisory only — a deprecated schema is a valid schema.</summary>
    public override bool StopOnError => false;

    /// <summary>Attribute declarations carrying <c>@deprecated</c>, by name.</summary>
    private readonly Dictionary<string, IonAttributeInstance> _deprecatedAttributes = new(StringComparer.Ordinal);

    public override void DoProcess()
    {
        CollectDeprecatedAttributes();

        // Nothing in the project is deprecated: skip the per-site name resolution entirely. This is
        // the overwhelmingly common case and the stage should cost nothing there.
        var anyDeprecatedType = Context.ProcessedModules
            .Concat(Context.ExternalModules)
            .SelectMany(module => module.Definitions)
            .Any(definition => definition.attributes.Any(a => a.IsDeprecated));

        if (!anyDeprecatedType && _deprecatedAttributes.Count == 0)
            return;

        foreach (var file in Context.Files)
        {
            if (anyDeprecatedType)
                foreach (var site in IonTypeSites.Sites(file))
                    CheckTypeReference(site);

            if (_deprecatedAttributes.Count > 0)
                foreach (var site in IonAttributeSites.Of(file))
                    CheckAttributeUse(site);
        }
    }

    // ── Type references ────────────────────────────────────────────────

    private void CheckTypeReference(in IonTypeSite site)
    {
        if (site.Owners().Any(IsDeprecated))
            return;

        var declared = Lookup(site.Site.Name.Identifier);

        if (declared is null || declared.IsUnresolved)
            return;

        var marker = declared.attributes.FirstOrDefault(a => a.IsDeprecated);

        if (marker is null)
            return;

        Warn(IonAnalyticCodes.ION1004_DeprecatedSymbolUsage, site.Site.Name,
            declared.name.Identifier, Since(marker), IonTypeSites.Describe(site), Reason(marker));
    }

    // ── Attribute uses ─────────────────────────────────────────────────

    private void CheckAttributeUse(IonAttributeSite site)
    {
        if (!_deprecatedAttributes.TryGetValue(site.Attribute.Name.Identifier, out var marker))
            return;

        if (IsDeprecated(site.Owner))
            return;

        Warn(IonAnalyticCodes.ION1004_DeprecatedSymbolUsage, site.Attribute,
            $"@{site.Attribute.Name.Identifier}", Since(marker),
            IonTypeSites.Describe(site.Owner), Reason(marker));
    }

    private void CollectDeprecatedAttributes()
    {
        foreach (var file in Context.Files)
        foreach (var declaration in file.attributeDefSyntaxes)
        {
            var marker = declaration.Attributes.FirstOrDefault(IsDeprecatedAttribute);

            if (marker is null)
                continue;

            // Bound rather than read raw, so `since` / `reason` come back positionally resolved
            // whether the author wrote them by name or by position.
            var bound = Context.ResolveAttributeInstance(marker);

            if (bound is not null)
                _deprecatedAttributes[declaration.Name.Identifier] = bound;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a syntax node carries <c>@deprecated</c>, read off the source rather than the IR.
    /// </summary>
    /// <remarks>
    /// The IR is not usable for the suppression check: an enum member, a flags declaration and an
    /// attribute declaration all drop their attribute instances during lowering, and a syntax member
    /// has no back-pointer to whatever it became.
    /// </remarks>
    private static bool IsDeprecated(IonSyntaxMember member) => member.Attributes.Any(IsDeprecatedAttribute);

    private static bool IsDeprecatedAttribute(IonAttributeSyntax attribute) =>
        string.Equals(attribute.Name.Identifier, "deprecated", StringComparison.Ordinal);

    /// <summary>" since '2.0'", or empty when the attribute carried no version.</summary>
    private static string Since(IonAttributeInstance marker) =>
        marker.Get<string>("since") is { Length: > 0 } since ? $" since '{since}'" : "";

    /// <summary>The reason as a trailing sentence, or empty.</summary>
    private static string Reason(IonAttributeInstance marker)
    {
        if (marker.Get<string>("reason")?.Trim() is not { Length: > 0 } reason)
            return "";

        return reason[^1] is '.' or '!' or '?' ? $" {reason}" : $" {reason}.";
    }

    /// <summary>
    /// Resolves a written type name with the same precedence as the type checker — builtins first,
    /// then the project, then imported modules. Mirrors
    /// <c>PartialTypeValidationStage.Lookup</c>; a builtin can never be deprecated, so the first arm
    /// is also the fast exit for every primitive reference.
    /// </summary>
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
}
