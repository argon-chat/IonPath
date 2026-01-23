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

                    newDef = union with { sharedFields = newUnionSharedFields };
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

    private IonType ResolveTypeDeep(IonType type)
    {
        switch (type)
        {
            case IonUnresolvedType u:
            {
                var resolvedBase = ctx.ResolveType(u);
                if (resolvedBase is null)
                {
                    Error(IonAnalyticCodes.ION0009_UnresolvedTypeReference, u.name, u.name.Identifier);
                    return type; 
                }

                if (resolvedBase is IonGenericType gdef)
                {
                    var unresolvedArgs = gdef.TypeArguments ?? [];

                    if (unresolvedArgs.Count > 0)
                    {
                        var resolvedArgs = unresolvedArgs.Select(ResolveTypeDeep).ToList();
                        var instantiated = gdef with { TypeArguments = resolvedArgs };
                        return instantiated;
                    }
                }
                return ResolveTypeDeep(resolvedBase);
            }

            case IonGenericType { TypeArguments.Count: > 0 } g:
            {
                var newArgs = g.TypeArguments.Select(ResolveTypeDeep).ToList();

                var changed = !newArgs.SequenceEqual(g.TypeArguments, ReferenceEqualityComparer.Default);
                return changed ? g with { TypeArguments = newArgs } : g;
            }

            default:
                return type;
        }
    }

    private static void CollectReferencedTypes(IonType type, List<IonType> acc)
    {
        acc.Add(type);

        if (type is not IonGenericType { TypeArguments.Count: > 0 } g) return;
        foreach (var ta in g.TypeArguments)
            CollectReferencedTypes(ta, acc);
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<IonBase>
    {
        public static readonly ReferenceEqualityComparer Default = new();

        public bool Equals(IonBase? x, IonBase? y)
        {
            return ReferenceEquals(x!.name.Identifier, y!.name.Identifier);
        }

        public int GetHashCode(IonBase obj) => obj.name.GetHashCode();
    }
}