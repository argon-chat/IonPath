namespace ion.compiler;

using runtime;

public class RestoreUnresolvedTypeStage(CompilationContext ctx) : CompilationStage(ctx)
{
    public override string StageName => "Type Resolution";
    public override string StageDescription => "Resolving type references and dependencies";
    public override bool StopOnError => false; // Collect ALL unresolved types, don't stop

    public override void DoProcess()
    {
        var result = RebuildTypesGraph(Context.ProcessedModules.AsReadOnly(), out var graph);
        Context.ProcessedModules.AddRange(result);
    }

    private IReadOnlyList<IonModule> RebuildTypesGraph(
        IReadOnlyList<IonModule> modules,
        out Dictionary<IonType, List<IonType>> typeGraph)
    {
        typeGraph = new();

        foreach (var module in modules)
        {
            var updatedDefinitions = new List<IonType>();

            foreach (var def in module.Definitions)
            {
                var newFields = new List<IonField>();
                var referencedTypes = new List<IonType>();

                foreach (var field in def.fields)
                {
                    var resolvedType = ResolveTypeDeep(field.type);
                    newFields.Add(field with { type = resolvedType });
                    CollectReferencedTypes(resolvedType, referencedTypes);
                }

                var newDef = def;

                if (def is IonUnion union)
                {
                    var newUnionSharedFields = new List<IonArgument>();
                    foreach (var field in union.sharedFields)
                    {
                        var resolvedType = ResolveTypeDeep(field.type);
                        newUnionSharedFields.Add(field with { type = resolvedType });
                        CollectReferencedTypes(resolvedType, referencedTypes);
                    }

                    // An inline case (`union U { Ok(v: UserId) }`) is a synthetic IonType that
                    // lives only inside union.types — it is never added to module.Definitions, so
                    // the loop above will not reach its fields and they would keep their
                    // unresolved / un-erased types. A `case Foo` type reference is left alone:
                    // it points at a declaration that is rebuilt on its own pass.
                    var newCaseTypes = new List<IonType>(union.types.Count);
                    foreach (var caseType in union.types)
                    {
                        if (!caseType.IsUnionCase)
                        {
                            newCaseTypes.Add(caseType);
                            continue;
                        }

                        var caseFields = new List<IonField>(caseType.fields.Count);
                        foreach (var field in caseType.fields)
                        {
                            var resolvedType = ResolveTypeDeep(field.type);
                            caseFields.Add(field with { type = resolvedType });
                            CollectReferencedTypes(resolvedType, referencedTypes);
                        }

                        newCaseTypes.Add(caseType with { fields = caseFields.AsReadOnly() });
                    }

                    newDef = union with { sharedFields = newUnionSharedFields, types = newCaseTypes };
                }

                if (newDef is IonGenericType { TypeArguments.Count: > 0 } gdef)
                {
                    var updatedArgs = gdef.TypeArguments.Select(ResolveTypeDeep).ToList();
                    newDef = gdef with { TypeArguments = updatedArgs };
                    foreach (var a in updatedArgs) CollectReferencedTypes(a, referencedTypes);
                }

                var updatedType = newDef with { fields = newFields.AsReadOnly() };
                updatedDefinitions.Add(updatedType);

                typeGraph[updatedType] = referencedTypes.Distinct().ToList();
            }

            module.Definitions.Clear();
            module.Definitions.AddRange(updatedDefinitions);

            var updatedServices = new List<IonService>();

            foreach (var service in module.Services)
            {
                var updatedMethods = new List<IonMethod>();

                foreach (var method in service.methods)
                {
                    var resolvedArgs = new List<IonArgument>();
                    var referencedTypes = new List<IonType>();

                    foreach (var arg in method.arguments)
                    {
                        var resolvedType = ResolveTypeDeep(arg.type);
                        resolvedArgs.Add(arg with { type = resolvedType });
                        CollectReferencedTypes(resolvedType, referencedTypes);
                    }

                    var returnType = ResolveTypeDeep(method.returnType);
                    CollectReferencedTypes(returnType, referencedTypes);

                    var updatedMethod = method with { arguments = resolvedArgs, returnType = returnType };
                    updatedMethods.Add(updatedMethod);

                    typeGraph[updatedMethod.returnType] = referencedTypes.Distinct().ToList();
                }

                updatedServices.Add(service with { methods = updatedMethods });
            }

            module.Services.Clear();
            module.Services.AddRange(updatedServices);
        }

        return modules;
    }

    private IonType ResolveTypeDeep(IonType type) => ResolveTypeDeep(type, null);

    /// <param name="aliasPath">
    /// The chain of typedef names already expanded on the way to <paramref name="type"/>, or
    /// <see langword="null"/> at the root of a type reference. Used only to break typedef cycles;
    /// see <see cref="EraseTypedef"/>.
    /// </param>
    private IonType ResolveTypeDeep(IonType type, IReadOnlyList<string>? aliasPath)
    {
        switch (type)
        {
            case IonUnresolvedType u:
            {
                var resolvedBase = ctx.ResolveType(u);
                if (resolvedBase is null)
                {
                    // A mixin name resolves to a declaration; it is only the *position* that
                    // rejects it, and `MixinExpansionStage` has already said so once per written
                    // site (ION0066). Reporting ION0009 as well would name the wrong problem —
                    // "the type may be missing, misspelled, or not imported" about a name the
                    // author declared and can see — and would do it once per method for a service
                    // base argument, because this walk is over the IR.
                    if (ctx.IsMixinName(u.name.Identifier))
                        return type;

                    // Likewise for an inline body that `InlineTypeHoistingStage` refused to hoist
                    // (ION0068): it leaves the unlexable `$inline` placeholder behind, and no
                    // author has ever typed that name. The placeholder is deliberately impossible
                    // to write, so this can never swallow a real unresolved reference.
                    if (u.name.Identifier == syntax.IonUnderlyingTypeSyntax.InlineTypeName)
                        return type;

                    var knownTypes = ctx.GlobalModules
                        .Concat(ctx.ProcessedModules)
                        .SelectMany(m => m.Definitions)
                        .Select(d => d.name.Identifier)
                        .Distinct();
                    var suggestion = LevenshteinDistance.FindClosest(u.name.Identifier, knownTypes);
                    if (suggestion is not null)
                        Error(IonAnalyticCodes.ION0009_UnresolvedTypeReferenceWithSuggestion, u.name, u.name.Identifier, suggestion);
                    else
                        Error(IonAnalyticCodes.ION0009_UnresolvedTypeReference, u.name, u.name.Identifier);
                    return type;
                }

                if (resolvedBase is IonGenericType gdef)
                {
                    var unresolvedArgs = gdef.TypeArguments ?? [];

                    if (unresolvedArgs.Count > 0)
                    {
                        var resolvedArgs = unresolvedArgs.Select(x => ResolveTypeDeep(x, aliasPath)).ToList();
                        var instantiated = gdef with { TypeArguments = resolvedArgs };
                        return instantiated;
                    }
                }
                return ResolveTypeDeep(resolvedBase, aliasPath);
            }

            case IonGenericType { TypeArguments.Count: > 0 } g:
            {
                // The alias path is threaded into the arguments (rather than reset) so that a
                // typedef that reaches itself through a wrapper — `typedef A = B[]; typedef B = A;`
                // — still terminates. EraseTypedef copies the path before extending it, so sibling
                // arguments cannot see each other's entries and `Foo<A, A>` is not a false cycle.
                var newArgs = g.TypeArguments.Select(x => ResolveTypeDeep(x, aliasPath)).ToList();

                // Identity on the arguments themselves. Every arm of this method returns the very
                // instance it was given when there was nothing to do, so "some argument is a
                // different object" is exactly "something was resolved or erased".
                //
                // This used to compare `name.Identifier` *by reference*, one wrapper out. Both
                // halves of that were wrong in the same direction: the wrapper generics are `with`
                // copies of one shared builtin definition, so the outer `Array` / `Maybe` / `Map` /
                // `Partial` / `Set` compared equal to itself by reference on every single nesting,
                // the whole level was declared unchanged, and the *old* arguments were kept —
                // discarding the resolution that had just been computed one frame down. A
                // `Partial<T>` written under any wrapper (`T~[]`, `T~?`, `Map<K, T~>`,
                // `Set<T~>`) therefore reached code generation with an `IonUnresolvedType` still in
                // its target slot: the three `CollectPartialTargets` walks skip an unresolved
                // target on purpose, so the per-type patch schema was silently never registered and
                // `IonPartial` fell back at run time to reflection with best-effort field order —
                // the wrong wire order, with nothing said at build time. A bare `T~` escaped only
                // because nothing wrapped it, which is why the fixtures with a direct sibling use
                // hid the defect. The same fix erases a typedef under a wrapper (`UserId[]?`),
                // which was dropped for exactly the same reason.
                var changed = newArgs.Where((arg, i) => !ReferenceEquals(arg, g.TypeArguments[i])).Any();

                return changed ? g with { TypeArguments = newArgs } : g;
            }

            default:
                return EraseTypedef(type, aliasPath);
        }
    }

    /// <summary>
    /// Replaces a typedef with the type it aliases, transitively.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single place where typedef semantics live. A typedef is a transparent alias —
    /// "on the wire, it is identical to the underlying type" — so after this stage no field,
    /// argument or return type anywhere in the IR still refers to one. Everything downstream
    /// (formatters, the schema lock, the dependency graph) therefore sees the underlying type and
    /// needs no typedef awareness of its own.
    /// </para>
    /// <para>
    /// Chains collapse fully: <c>typedef A = B; typedef B = u4;</c> leaves <c>u4</c> at every use
    /// of <c>A</c>. Cycles are broken by <paramref name="aliasPath"/> and reported as
    /// <see cref="IonAnalyticCodes.ION0017_CircularTypedef"/> rather than recursing forever —
    /// <c>CircularTypeReferenceStage</c> cannot catch them because it deliberately ignores
    /// self references.
    /// </para>
    /// </remarks>
    private IonType EraseTypedef(IonType type, IReadOnlyList<string>? aliasPath)
    {
        if (!IsTypedefAlias(type))
            return type;

        var name = type.name.Identifier;

        if (aliasPath is not null && aliasPath.Contains(name, StringComparer.Ordinal))
        {
            ReportTypedefCycle(type, aliasPath, name);
            return type;
        }

        var extended = aliasPath is null ? new List<string>(1) : [..aliasPath];
        extended.Add(name);

        return ResolveTypeDeep(type.fields[0].type, extended);
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a typedef <em>declaration</em> whose alias must be erased.
    /// </summary>
    /// <remarks>
    /// <see cref="IonArray"/> copies <c>isTypedef</c> straight off its element type, so an array
    /// whose element happens to be a typedef reports <c>isTypedef: true</c> while carrying the
    /// element's fields. Unwrapping such a value would replace the array with its element type.
    /// <see cref="IonUnresolvedType"/> is excluded for the same reason: it has no fields to read.
    /// </remarks>
    internal static bool IsTypedefAlias(IonType type)
        => type is { isTypedef: true, fields.Count: > 0 } and not IonArray and not IonUnresolvedType;

    private readonly HashSet<string> _reportedTypedefCycles = new(StringComparer.Ordinal);

    private void ReportTypedefCycle(IonType type, IReadOnlyList<string> aliasPath, string name)
    {
        // The same cycle is reachable from every use site; report each alias at most once.
        if (!_reportedTypedefCycles.Add(name))
            return;

        var start = aliasPath.ToList().IndexOf(name);
        var cycle = aliasPath.Skip(start).Append(name);

        Error(IonAnalyticCodes.ION0017_CircularTypedef, type.name, string.Join(" → ", cycle));
    }

    private static void CollectReferencedTypes(IonType type, List<IonType> acc)
    {
        acc.Add(type);

        if (type is not IonGenericType { TypeArguments.Count: > 0 } g) return;
        foreach (var ta in g.TypeArguments)
            CollectReferencedTypes(ta, acc);
    }
}