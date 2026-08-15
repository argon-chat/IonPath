namespace ion.compiler;

using ion.runtime;

/// <summary>
/// Rejects type graphs whose recursion can never bottom out, e.g. <c>msg A { b: B; }</c> +
/// <c>msg B { a: A; }</c> — every A owns a B, every B owns an A, and no encoder can ever finish.
/// Must run after <c>RestoreUnresolvedTypeStage</c> so all types are resolved.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The rule: only unconditionally owned edges count.</strong> A bare <c>T</c> field is an
/// owned edge — every value of the owner contains exactly one <c>T</c>, always. <c>T?</c>,
/// <c>T[]</c>, <c>T~</c> and a union arm are <em>cycle-breaking</em>: each can terminate at runtime
/// (absent, empty, an empty patch, a different case), so a cycle running through one of them
/// describes a finite value and is perfectly encodable.
/// </para>
/// <para>
/// <strong>What that fixes.</strong> The previous implementation called <c>UnwrapType</c> — which
/// strips <c>Maybe</c> / <c>Array</c> / <c>Partial</c> — <em>before</em> the cycle test, so it saw
/// <c>Item[]</c> as a plain <c>Item</c>. Trees, graphs, comment threads and org charts were all
/// rejected, with a message advising the author to apply exactly the modifier that had just been
/// stripped. It also early-returned whenever a reference pointed at its own owner, which made a
/// direct self reference invisible: <c>msg A { a: A; }</c>, the one shape that is unarguably
/// infinite, compiled silently.
/// </para>
/// </remarks>
public sealed class CircularTypeReferenceStage(CompilationContext context)
    : CompilationStage(context)
{
    public override string StageName => "Circular Reference Detection";
    public override string StageDescription => "Checking for circular type references";
    public override bool StopOnError => false;

    private enum VisitState { White, Gray, Black }

    public override void DoProcess()
    {
        // Adjacency: type name → the type names it unconditionally owns.
        var adjacency = new Dictionary<string, List<string>>();
        var typeByName = new Dictionary<string, IonType>();

        foreach (var module in Context.ProcessedModules)
        {
            foreach (var def in module.Definitions)
            {
                if (def.IsBuiltin || def.IsScalar)
                    continue;

                var name = def.name.Identifier;
                typeByName.TryAdd(name, def);

                if (!adjacency.ContainsKey(name))
                    adjacency[name] = [];

                foreach (var field in def.fields)
                    CollectOwnedReference(field.type, name, adjacency);

                // A union's *arms* are not owned: a value is exactly one of them, so a cycle through
                // an arm ends as soon as a different arm is chosen. Its shared fields are owned —
                // they are present in every arm, so every value of the union carries them.
                if (def is IonUnion union)
                {
                    foreach (var sharedField in union.sharedFields)
                        CollectOwnedReference(sharedField.type, name, adjacency);
                }
            }
        }

        var state = adjacency.Keys.ToDictionary(key => key, _ => VisitState.White);
        var path = new List<string>();

        foreach (var node in adjacency.Keys)
        {
            if (state[node] == VisitState.White)
                Dfs(node, adjacency, state, path, typeByName);
        }
    }

    private void Dfs(
        string node,
        Dictionary<string, List<string>> adjacency,
        Dictionary<string, VisitState> state,
        List<string> path,
        Dictionary<string, IonType> typeByName)
    {
        state[node] = VisitState.Gray;
        path.Add(node);

        if (adjacency.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (!state.TryGetValue(neighbor, out var neighborState))
                    continue; // Builtin or unknown type — skip

                switch (neighborState)
                {
                    case VisitState.Gray:
                    {
                        // Found a cycle — report the path from where it closes back to itself. A
                        // direct self reference yields "A → A", which is the honest rendering.
                        var cycleStart = path.IndexOf(neighbor);
                        var cyclePath = path.Skip(cycleStart).Append(neighbor);
                        var cycleStr = string.Join(" → ", cyclePath);

                        var syntaxBase = typeByName.TryGetValue(neighbor, out var t)
                            ? t.name
                            : new syntax.IonSyntaxBase();

                        Error(IonAnalyticCodes.ION0030_CircularTypeReference, syntaxBase, cycleStr);
                        break;
                    }
                    case VisitState.White:
                        Dfs(neighbor, adjacency, state, path, typeByName);
                        break;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        state[node] = VisitState.Black;
    }

    /// <summary>
    /// Records the edge <paramref name="ownerName"/> → <paramref name="type"/>, but only when
    /// <paramref name="type"/> is unconditionally owned.
    /// </summary>
    /// <remarks>
    /// The self-reference guard the old version had here (<c>inner.name.Identifier == ownerName</c>,
    /// an early return) is gone on purpose: it hid the one cycle that is never in doubt. A self edge
    /// is recorded like any other and the DFS reports it, so <c>msg A { a: A; }</c> is now ION0030
    /// while <c>msg A { a: A?; }</c> and <c>msg A { children: A[]; }</c> stay clean.
    /// <para>
    /// Edges are deduplicated: <c>msg A { b1: B; b2: B; }</c> is one dependency, and adding it twice
    /// only produced the same diagnostic twice.
    /// </para>
    /// </remarks>
    private static void CollectOwnedReference(IonType type, string ownerName,
        Dictionary<string, List<string>> adjacency)
    {
        // A fixed-size array is the one wrapper that is an owned edge, and it is exactly the
        // asymmetry that a naive "arrays break cycles" rule gets wrong. `T[]` may be empty, so the
        // recursion can stop there; `T[N]` with N >= 1 holds N values of T unconditionally, so it
        // cannot. `msg Node { kids: Node[4]; }` is genuinely infinite and is ION0030, while
        // `msg Node { kids: Node[]; }` beside it stays clean.
        //
        // The recursion is on the *element*: the edge runs owner → T, not owner → the `Array`
        // wrapper, whose `name.Identifier` is the literal "Array" and appears in no adjacency list.
        // A size of 0 or less is ION0062 and is deliberately not treated as owned — the schema is
        // already failing, and a second, bogus ION0030 stacked on it would send the author looking
        // for a cycle instead of at the size.
        if (type is IonGenericType { IsArray: true, FixedSize: >= 1, TypeArguments.Count: > 0 } fixedArray)
        {
            CollectOwnedReference(fixedArray.TypeArguments[0], ownerName, adjacency);
            return;
        }

        if (!IsOwned(type))
            return;

        if (type.IsBuiltin || type.IsScalar)
            return;

        var refName = type.name.Identifier;
        if (string.IsNullOrEmpty(refName) || refName == "void")
            return;

        if (!adjacency.TryGetValue(ownerName, out var edges))
            adjacency[ownerName] = edges = [];

        if (!edges.Contains(refName))
            edges.Add(refName);
    }

    /// <summary>
    /// Whether a value of the owner always contains exactly one value of <paramref name="type"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// False for the five builtin generics, each of which is a place the recursion can stop:
    /// <c>Maybe&lt;T&gt;</c> may be absent, <c>Array&lt;T&gt;</c> may be empty,
    /// <c>Partial&lt;T&gt;</c> carries only the fields a patch actually sets (an empty patch is a
    /// legal, terminating value), and <c>Map&lt;K, V&gt;</c> and <c>Set&lt;T&gt;</c> may both be
    /// empty. This is the whole difference from the old <c>UnwrapType</c>, which looked *through*
    /// the wrappers instead of stopping at them.
    /// </para>
    /// <para>
    /// <c>Map</c> and <c>Set</c> are cycle-breaking on <em>both</em> sides — a
    /// <c>msg Tree { children: Map&lt;string, Tree&gt;; }</c> is a perfectly finite tree with no
    /// children, exactly like <c>Tree[]</c>. The one collection that is not cycle-breaking is
    /// <c>T[N]</c>, which never reaches this method: <see cref="CollectOwnedReference"/> intercepts
    /// it above and recurses into the element.
    /// </para>
    /// </remarks>
    private static bool IsOwned(IonType type) =>
        type is not IonGenericType { TypeArguments.Count: > 0 } wrapper ||
        !(wrapper.IsMaybe || wrapper.IsArray || wrapper.IsPartial || wrapper.IsMap || wrapper.IsSet);
}
