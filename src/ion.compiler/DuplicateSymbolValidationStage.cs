namespace ion.compiler;

using syntax;


public sealed class VerifyInvalidStatementsStage(CompilationContext context) : CompilationStage(context)
{
    public override void DoProcess()
    {
        foreach (var invalidStatement in context.Files.SelectMany(x => x.allTokens ?? []).OfType<InvalidIonBlock>())
            Error(IonAnalyticCodes.ION0010_InvalidStatement, invalidStatement);
    }
}

public sealed class DuplicateSymbolValidationStage(CompilationContext context)
    : CompilationStage(context)
{
    public override void DoProcess()
    {
        var nameToDef = new Dictionary<string, IonSyntaxMember>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in context.Files)
        {
            foreach (var def in module.Definitions)
            {
                switch (def)
                {
                    case IonTypedefSyntax typeDef
                        when nameToDef.TryGetValue(typeDef.TypeName.Name.Identifier, out var existing):
                        Error(IonAnalyticCodes.ION0002_DuplicateDefinition, def, typeDef.TypeName.Name.Identifier,
                            module.file.FullName, existing.SourceFile?.FullName ?? "unknown");
                        break;

                    case IonTypedefSyntax typeDef:
                        nameToDef[typeDef.TypeName.Name.Identifier] = def;
                        break;
                    case IonMessageSyntax msg when nameToDef.TryGetValue(msg.Name.Identifier, out var existing):
                        Error(IonAnalyticCodes.ION0002_DuplicateDefinition, def, msg.Name.Identifier,
                            module.file.FullName, existing.SourceFile?.FullName ?? "unknown");
                        break;
                    case IonMessageSyntax msg:
                        nameToDef[msg.Name.Identifier] = def;
                        break;
                    case IonServiceSyntax service
                        when nameToDef.TryGetValue(service.serviceName.Identifier, out var existing):
                        Error(IonAnalyticCodes.ION0002_DuplicateDefinition, def, service.serviceName.Identifier,
                            module.file.FullName, existing.SourceFile?.FullName ?? "unknown");
                    break;
                    case IonServiceSyntax service:
                        nameToDef[service.serviceName.Identifier] = def;
                        break;
                }
            }
        }
    }
}