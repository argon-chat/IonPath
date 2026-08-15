namespace ion.compiler;

using syntax;

/// <summary>
/// Resolves every <c>with</c> clause and works out the exact field list each message ends up with.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A mixin is a field-set template, not a type.</strong> It has no wire identity: no entry
/// in <c>ion.lock.json</c> (<c>TransformStage</c> never puts one in <c>IonModule.Definitions</c>, so
/// <c>SchemaLockGenerator</c> and the generator definition loops never see it), no generated
/// declaration in any target, and it may not be written in type position — ION0066. What it has is
/// fields, and those fields become fields of every message that includes it, complete with their doc
/// comments and their attributes, because the expansion reuses the very same
/// <see cref="IonFieldSyntax"/> nodes the author wrote.
/// </para>
/// <para>
/// <strong>Field order is the contract.</strong> Ion messages are positional on the wire, so the
/// order this stage produces <em>is</em> the field numbering, and it is pinned:
/// </para>
/// <list type="number">
/// <item>the mixin fields, in <c>with</c> order;</item>
/// <item>within one mixin, that mixin's own expansion — base mixins first, transitively, then its
/// own fields in declaration order;</item>
/// <item>then the message's own fields, in declaration order.</item>
/// </list>
/// <para>
/// So for <c>mixin A { a1; a2; }</c>, <c>mixin B with A { b1; }</c>, <c>mixin C { c1; }</c> and
/// <c>msg M with B, C { m1; }</c>, <c>M</c> is exactly <c>a1, a2, b1, c1, m1</c>. Reading a
/// <c>with</c> clause left to right and each mixin top to bottom gives the wire order, which is the
/// only property worth having here: any rule that needed a linearisation algorithm to predict would
/// mean an author could not tell what index their own field had.
/// </para>
/// <para>
/// <strong>A mixin contributes once, however many paths reach it.</strong> The walk carries a
/// visited set of mixin names, so the diamond <c>msg Document with Audited, Traced</c> over
/// <c>mixin Traced with Audited</c> splices <c>Audited</c> exactly once. That is not a special case
/// bolted onto the order rule, it is what makes the rule well defined: a mixin's fields land at its
/// <em>first</em> position in the left-to-right transitive walk, so <c>with Audited, Traced</c> and
/// <c>with Traced, Audited</c> both produce <c>createdAt, createdBy, traceId</c>. The field
/// numbering does not depend on the order the includer happened to list them in.
/// </para>
/// <para>
/// <strong>Collisions are errors.</strong> What is left after that dedupe is a real conflict: two
/// <em>different</em> mixins declaring the same field name, or a mixin field colliding with the
/// message's own. Both are ION0065 and both name their two sources, because neither is obviously
/// the one to change and the other one is usually in a different file.
/// </para>
/// <para>
/// <strong>Where it runs.</strong> After <see cref="InlineTypeHoistingStage"/> — so an inline type
/// inside a mixin is hoisted once, named after the mixin, rather than once per includer — and before
/// <c>TransformStage</c>, which reads the expansion out of
/// <c>CompilationContext.ExpandedMessageFields</c>. The expansion is deliberately not written back
/// into <see cref="IonMessageSyntax.Fields"/>; see that property's docs for why.
/// </para>
/// </remarks>
public sealed class MixinExpansionStage(CompilationContext context) : CompilationStage(context)
{
    public override string StageName => "Mixin Expansion";
    public override string StageDescription => "Resolving 'with' clauses and splicing mixin fields";

    /// <summary>Collect every bad clause and every collision, don't stop at the first.</summary>
    public override bool StopOnError => false;

    private readonly HashSet<string> _reportedCycles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cyclic = new(StringComparer.Ordinal);

    /// <summary>
    /// Collisions already reported, so one mistake is stated once.
    /// </summary>
    /// <remarks>
    /// A conflict between two mixins is a property of that pair, not of whoever includes them: it is
    /// keyed by the field and the two origins alone and therefore reported at the first declaration
    /// that hits it — which, because mixins are validated before messages, is the mixin that
    /// introduced the pairing. A conflict with a declaration's <em>own</em> field is keyed by that
    /// declaration as well, since each one is a separate mistake in a separate place.
    /// <para>
    /// Both halves of that read on <see cref="CollisionKey"/>, and both used to be false. The key
    /// was <c>field | first | second</c> built from the two <em>display phrases</em> in the order
    /// the walk happened to meet them, so one conflict produced two reports twice over: once
    /// because <c>with A, B</c> and <c>with B, A</c> swap the two halves, and once because
    /// <see cref="Source"/> renders the same origin as <c>mixin 'A'</c> while expanding <c>A</c>'s
    /// includer directly and as <c>mixin 'A' (included by 'B')</c> one level further out — which
    /// put two identical diagnostics on one span. The key is now the field plus the two origins as
    /// an unordered pair, where an origin is the declaration that introduced the field
    /// (<c>mixin 'A'</c>, <c>msg 'M'</c>) and nothing about the path it arrived by.
    /// </para>
    /// </remarks>
    private readonly HashSet<string> _reportedCollisions = new(StringComparer.Ordinal);

    /// <summary>
    /// Where a field in an expansion came from: the declaration that introduced it
    /// (<see cref="Origin"/>, which identifies the conflict) and the phrase a diagnostic names it by
    /// (<see cref="Display"/>, which may add "(included by 'B')" and therefore cannot be a key).
    /// </summary>
    private readonly record struct FieldSource(string Origin, string Display);

    /// <summary>
    /// The identity of one conflict: a field name and the unordered pair of declarations that both
    /// want it. Order-insensitive so that the same pair met from either side is one mistake.
    /// </summary>
    private static string CollisionKey(string field, string first, string second) =>
        string.CompareOrdinal(first, second) <= 0
            ? $"{field}|{first}|{second}"
            : $"{field}|{second}|{first}";

    public override void DoProcess()
    {
        foreach (var file in Context.Files)
        foreach (var mixin in file.mixinSyntaxes)
            Context.RegisterMixin(mixin);

        DetectCycles();

        // Every mixin is walked, used or not: a bad `with` clause or an internal collision is still
        // a mistake in the file. Doing it before the messages is also what fixes the report site —
        // a conflict two mixins have with each other belongs on the mixin that pairs them, not on
        // each of the ten messages that then include it.
        foreach (var file in Context.Files)
        foreach (var mixin in file.mixinSyntaxes)
            ExpandFrom(mixin, mixin.Fields);

        foreach (var file in Context.Files)
        foreach (var message in file.messageSyntaxes)
            Context.ExpandedMessageFields[message] =
                message.Mixins is null ? message.Fields : ExpandFrom(message, message.Fields);

        RejectMixinsInTypePosition();
    }

    // ── Cycles ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reports ION0064 for every <c>mixin</c> that transitively includes itself.
    /// </summary>
    /// <remarks>
    /// Shaped after <c>RestoreUnresolvedTypeStage.ReportTypedefCycle</c>: a three-colour DFS, and
    /// each cycle reported once at the name it closes on, because the same cycle is reachable from
    /// every member of it and from every message that includes any of them.
    /// <para>
    /// Runs before any expansion so that <see cref="Splice"/> can report the cycle once, here, and
    /// then simply contribute nothing for the mixins involved. The visited set it carries would
    /// terminate anyway; a stage must not hang on input it has already diagnosed.
    /// </para>
    /// </remarks>
    private void DetectCycles()
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();

        foreach (var name in Context.Mixins.Keys)
            Walk(name, done, path);
    }

    private void Walk(string name, HashSet<string> done, List<string> path)
    {
        if (done.Contains(name))
            return;

        var closes = path.IndexOf(name);

        if (closes >= 0)
        {
            _cyclic.UnionWith(path.Skip(closes));

            if (_reportedCycles.Add(name) && Context.Mixins.TryGetValue(name, out var declaration))
                Error(IonAnalyticCodes.ION0064_CircularMixin, declaration.Name,
                    string.Join(" → ", path.Skip(closes).Append(name)));

            return;
        }

        path.Add(name);

        foreach (var included in Context.Mixins[name].Mixins ?? [])
            if (Context.Mixins.ContainsKey(included.Identifier))
                Walk(included.Identifier, done, path);

        path.RemoveAt(path.Count - 1);
        done.Add(name);
    }

    // ── Expansion ──────────────────────────────────────────────────────

    /// <summary>
    /// The full field list of one declaration: everything its <c>with</c> clause brings in, in the
    /// pinned order, then <paramref name="own"/>.
    /// </summary>
    /// <remarks>
    /// A single depth-first walk with one visited set for the whole expansion. That set is what
    /// makes a mixin contribute exactly once — see the class remarks — and it is also what bounds
    /// the recursion independently of <see cref="DetectCycles"/>, so a stage can never hang on input
    /// it has already diagnosed.
    /// <para>
    /// The <see cref="IonFieldSyntax"/> nodes in the result are the mixin's own, shared with every
    /// other declaration that includes it. <c>TransformStage.PrepareFields</c> materialises a fresh
    /// <c>IonField</c> per message, so the mutable <c>Doc</c> of one message's copy cannot leak into
    /// another — the same arrangement, and the same reason, as a service's base arguments in
    /// <c>PrependMethods</c>. Sharing the syntax node is exactly what carries a mixin field's doc
    /// comment and its attributes into every expansion.
    /// </para>
    /// </remarks>
    private List<IonFieldSyntax> ExpandFrom(IonSyntaxMember declaration, List<IonFieldSyntax> own)
    {
        var described = IonTypeSites.Describe(declaration);
        var fields = new List<IonFieldSyntax>();

        // field name → where it came from.
        var byName = new Dictionary<string, FieldSource>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var included in Included(declaration))
            Splice(included, included.Name.Identifier, visited, fields, byName, described);

        foreach (var field in own)
        {
            var name = field.Name.Identifier;

            if (byName.TryGetValue(name, out var source))
            {
                // The own field is dropped rather than the mixin's, so the expansion still has
                // unique field names even while the compile is failing: a duplicate would reach the
                // generators, which emit one property per field, and IonPartial, which keys its
                // patch map by field name.
                //
                // `described` is the origin as well as the display here — for a mixin it is
                // `mixin 'B'`, exactly what `Splice` records when the same mixin is spliced into
                // somebody else, so "A's x versus B's own x" is one conflict whether it is met
                // while expanding B or while expanding a message that includes B. For a message it
                // is `msg 'M'`, which is unique per declaration, which is what keeps a collision
                // with a *message's* own field one report per message.
                if (Report(CollisionKey(name, source.Origin, described)))
                    Error(IonAnalyticCodes.ION0065_MixinFieldCollision, field.Name, name, source.Display,
                        described);

                continue;
            }

            byName[name] = new FieldSource(described, described);
            fields.Add(field);
        }

        return fields;
    }

    /// <summary>
    /// Appends one mixin's transitive contribution — base mixins first, then its own fields.
    /// </summary>
    /// <remarks>
    /// <paramref name="listed"/> is the name written in the root declaration's <c>with</c> clause,
    /// carried down so a collision can say <c>mixin 'A' (included by 'B')</c>; without it a field
    /// two levels down is reported against a mixin the reader cannot see in the clause they wrote.
    /// </remarks>
    private void Splice(IonMixinSyntax mixin, string listed, HashSet<string> visited,
        List<IonFieldSyntax> fields, Dictionary<string, FieldSource> byName, string described)
    {
        var name = mixin.Name.Identifier;

        // Already spliced on another path (a diamond), or part of a cycle ION0064 already reported.
        if (_cyclic.Contains(name) || !visited.Add(name))
            return;

        // Base mixins first, so a chain reads outside-in on the wire: `mixin B with A` puts A's
        // fields ahead of B's own, transitively.
        foreach (var included in Included(mixin))
            Splice(included, listed, visited, fields, byName, described);

        // The origin is the mixin itself and says nothing about how it was reached; the display
        // adds "(included by 'B')" so the reader can find it in the clause they wrote. Only the
        // first may be keyed on — see `_reportedCollisions`.
        var origin = Source(name, name);
        var display = Source(name, listed);

        foreach (var field in mixin.Fields)
        {
            var fieldName = field.Name.Identifier;

            if (byName.TryGetValue(fieldName, out var existing))
            {
                // Keyed on the pair, not on the includer: two mixins that conflict with each other
                // do so once, wherever they are first put together, rather than at every message
                // downstream of them.
                if (Report(CollisionKey(fieldName, existing.Origin, origin)))
                    Error(IonAnalyticCodes.ION0065_MixinFieldCollisionBetweenMixins, field.Name,
                        fieldName, existing.Display, display, described);

                continue;
            }

            byName[fieldName] = new FieldSource(origin, display);
            fields.Add(field);
        }
    }

    private bool Report(string key) => _reportedCollisions.Add(key);

    /// <summary>
    /// "mixin 'A'", or "mixin 'A' (included by 'B')" when the field arrived through a chain.
    /// </summary>
    private static string Source(string origin, string listed) =>
        origin == listed ? $"mixin '{origin}'" : $"mixin '{origin}' (included by '{listed}')";

    // ── `with` clause resolution ───────────────────────────────────────

    /// <summary>
    /// The mixins a declaration's <c>with</c> clause actually names, in source order, deduplicated,
    /// with every bad entry reported — exactly once, however many declarations later reach it.
    /// </summary>
    /// <remarks>
    /// The memo is what keeps ION0063 to one report per written clause. A mixin's clause is resolved
    /// when <c>DoProcess</c> walks the mixin declarations, and then read again by every message that
    /// includes it, directly or transitively; without this, a single misspelled name in a widely used
    /// mixin would produce one error per consumer. Keyed by name because a mixin is only ever
    /// reachable by name, and a second declaration of the same name is already ION0002.
    /// </remarks>
    private List<IonMixinSyntax> Included(IonSyntaxMember declaration)
    {
        if (declaration is not IonMixinSyntax mixin)
            return ResolveWith(declaration).ToList();

        if (_included.TryGetValue(mixin.Name.Identifier, out var cached))
            return cached;

        return _included[mixin.Name.Identifier] = ResolveWith(declaration).ToList();
    }

    private readonly Dictionary<string, List<IonMixinSyntax>> _included = new(StringComparer.Ordinal);

    private IEnumerable<IonMixinSyntax> ResolveWith(IonSyntaxMember declaration)
    {
        var names = declaration switch
        {
            IonMessageSyntax message => message.Mixins,
            IonMixinSyntax mixin => mixin.Mixins,
            _ => null
        };

        if (names is null)
            yield break;

        var described = IonTypeSites.Describe(declaration);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var written in names)
        {
            var name = written.Identifier;

            if (Context.Mixins.TryGetValue(name, out var mixin))
            {
                if (!seen.Add(name))
                {
                    Error(IonAnalyticCodes.ION0063_DuplicateMixinInWithClause, written, name, described);
                    continue;
                }

                // A cyclic mixin contributes nothing (ION0064); yielding it anyway keeps a second,
                // unrelated entry in the same clause from being skipped.
                yield return mixin;
                continue;
            }

            if (KindOf(name) is { } kind)
                Error(IonAnalyticCodes.ION0063_WithClauseNamesNonMixin, written, name, kind, described);
            else
                Error(IonAnalyticCodes.ION0063_MixinNotFound, written, name, described);
        }
    }

    /// <summary>
    /// What a name in a <c>with</c> clause turns out to be, when it is not a mixin — or
    /// <see langword="null"/> when nothing declares it at all.
    /// </summary>
    /// <remarks>
    /// The split between "resolves to the wrong thing" and "resolves to nothing" is the same one
    /// ION0004 and ION0003 make about a type: the two can never both be right about one token, and
    /// the fixes are different.
    /// </remarks>
    private string? KindOf(string name)
    {
        foreach (var module in Context.GlobalModules)
        foreach (var definition in module.Definitions)
            if (definition.IsBuiltin && definition.name.Identifier == name)
                return $"the builtin type '{name}'";

        foreach (var file in Context.Files)
        foreach (var definition in file.Definitions)
        {
            var kind = definition switch
            {
                IonMessageSyntax m when m.Name.Identifier == name => "a msg",
                IonEnumSyntax e when e.Name.Identifier == name => "an enum",
                IonFlagsSyntax f when f.Name.Identifier == name => "a flags declaration",
                IonUnionSyntax u when u.unionName.Identifier == name => "a union",
                IonTypedefSyntax t when t.TypeName.Name.Identifier == name => "a typedef",
                IonServiceSyntax s when s.serviceName.Identifier == name => "a service",
                IonAttributeDefSyntax a when a.Name.Identifier == name => "an attribute declaration",
                _ => null
            };

            if (kind is not null)
                return kind;
        }

        return null;
    }

    // ── Type position ──────────────────────────────────────────────────

    /// <summary>
    /// Reports ION0066 wherever a mixin name is written where a type belongs.
    /// </summary>
    /// <remarks>
    /// Off the syntax walk, so each written position is reported exactly once — an IR walk would see
    /// a service's base argument once per method. <c>RestoreUnresolvedTypeStage</c> recognises the
    /// same names and stays silent about them, so this replaces ION0009 instead of stacking on it:
    /// the name resolves perfectly well, it is the position that rejects it.
    /// </remarks>
    private void RejectMixinsInTypePosition()
    {
        if (Context.Mixins.Count == 0)
            return;

        foreach (var file in Context.Files)
        foreach (var site in IonTypeSites.Of(file))
            if (Context.IsMixinName(site.Name.Identifier))
                Error(IonAnalyticCodes.ION0066_MixinInTypePosition, site.Name, site.Name.Identifier);
    }
}
