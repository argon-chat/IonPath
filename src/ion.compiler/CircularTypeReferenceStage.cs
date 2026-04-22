namespace ion.compiler;

using ion.runtime;

/// <summary>
/// Detects circular type references that would cause infinite recursion during serialization.
/// For example: msg A { x: B; } + msg B { x: A; } → A → B → A (cycle).
/// Must run after RestoreUnresolvedTypeStage so all types are resolved.
/// </summary>
public sealed class CircularTypeReferenceStage(CompilationContext context)
    : CompilationStage(context)
{
    public override string StageName => "Circular Reference Detection";
    public override string StageDescription => "Checking for circular type references";
    public override bool StopOnError => false;

    private enum VisitState { White, Gray, Black }

    public override void DoProcess()
    {
        // Build adjacency: type name → set of referenced type names (non-builtin, non-scalar)
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
                    CollectDirectReferences(field.type, name, adjacency);

                if (def is IonUnion union)
                {
                    foreach (var sf in union.sharedFields)
                        CollectDirectReferences(sf.type, name, adjacency);
                    foreach (var caseType in union.types)
                    {
                        if (!caseType.IsUnionCase)
                            CollectDirectReferences(caseType, name, adjacency);
                    }
                }
            }
        }

        // DFS cycle detection
        var state = new Dictionary<string, VisitState>();
        foreach (var key in adjacency.Keys)
            state[key] = VisitState.White;

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
                if (!state.TryGetValue(neighbor, out var ns))
                    continue; // Builtin or unknown type — skip

                switch (ns)
                {
                    case VisitState.Gray:
                    {
                        // Found cycle — extract cycle path
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
    /// Collect non-builtin, non-scalar, non-wrapper type names referenced by a type.
    /// Unwraps Maybe/Array/Partial generics to find the actual referenced type.
    /// </summary>
    private static void CollectDirectReferences(IonType type, string ownerName, Dictionary<string, List<string>> adjacency)
    {
        // Unwrap wrappers (Maybe<T>, Array<T>, Partial<T>)
        var inner = UnwrapType(type);
        if (inner.IsBuiltin || inner.IsScalar || inner.name.Identifier == ownerName)
            return;

        var refName = inner.name.Identifier;
        if (!string.IsNullOrEmpty(refName) && refName != "void")
        {
            if (!adjacency.ContainsKey(ownerName))
                adjacency[ownerName] = [];
            adjacency[ownerName].Add(refName);
        }
    }

    private static IonType UnwrapType(IonType type)
    {
        if (type is IonGenericType { TypeArguments.Count: > 0 } gt &&
            (gt.IsMaybe || gt.IsArray || gt.IsPartial))
        {
            return UnwrapType(gt.TypeArguments[0]);
        }

        return type;
    }
}
