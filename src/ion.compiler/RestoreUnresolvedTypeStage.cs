namespace ion.compiler;

using runtime;

public class RestoreUnresolvedTypeStage(CompilationContext ctx) : CompilationStage(ctx)
{
    public override void DoProcess()
    {
        var result = RebuildTypesGraph(ctx.ProcessedModules.AsReadOnly(), out var graph);
        ctx.ProcessedModules.Clear();
        ctx.ProcessedModules.AddRange(result);
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
                    var resolvedType = field.type;

                    if (field.type is IonUnresolvedType unresolved)
                    {
                        resolvedType = ctx.ResolveType(unresolved);

                        if (resolvedType is null)
                        {
                            Error(IonAnalyticCodes.ION0009_UnresolvedTypeReference,
                                unresolved.name, unresolved.name.Identifier);
                            continue;
                        }
                    }

                    newFields.Add(field with { type = resolvedType });
                    referencedTypes.Add(resolvedType);
                }

                var updatedType = def with { fields = newFields.AsReadOnly() };
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
                        var resolvedType = arg.type;
                        if (arg.type is IonUnresolvedType unresolved)
                        {
                            resolvedType = ctx.ResolveType(unresolved);
                            if (resolvedType is null)
                            {
                                Error(IonAnalyticCodes.ION0009_UnresolvedTypeReference,
                                    unresolved.name, unresolved.name.Identifier);
                                continue;
                            }
                        }

                        resolvedArgs.Add(arg with { type = resolvedType });
                        referencedTypes.Add(resolvedType);
                    }

                    var returnType = method.returnType;
                    if (method.returnType is IonUnresolvedType unresolvedReturn)
                    {
                        var resolvedReturn = ctx.ResolveType(unresolvedReturn);
                        if (resolvedReturn is not null)
                        {
                            returnType = resolvedReturn;
                            referencedTypes.Add(resolvedReturn);
                        }
                        else
                        {
                            Error(IonAnalyticCodes.ION0009_UnresolvedTypeReference,
                                unresolvedReturn.name, unresolvedReturn.name.Identifier);
                        }
                    }
                    else
                    {
                        referencedTypes.Add(returnType);
                    }

                    var updatedMethod = method with
                    {
                        arguments = resolvedArgs,
                        returnType = returnType
                    };

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
}