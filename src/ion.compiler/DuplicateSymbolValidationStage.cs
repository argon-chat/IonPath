namespace ion.compiler;

using runtime;
using syntax;

public sealed class DuplicateSymbolValidationStage(CompilationContext context)
    : CompilationStage(context)
{
    public void Run(IEnumerable<IonFileSyntax> modules)
    {
        foreach (var module in modules)
        {
            var nameToDef = new Dictionary<string, IonSyntaxMember>(StringComparer.OrdinalIgnoreCase);

            foreach (var def in module.Definitions)
            {
                switch (def)
                {
                    case IonTypedefSyntax typeDef when nameToDef.TryGetValue(typeDef.TypeName.Name, out var existing):
                        Error(IonAnalyticCodes.ION0002_DuplicateDefinition, def, typeDef.TypeName.Name, module.file.FullName, existing);
                        break;
                    case IonTypedefSyntax typeDef:
                        nameToDef[typeDef.TypeName.Name] = def;
                        break;
                    case IonMessageSyntax msg when nameToDef.TryGetValue(msg.Name, out var existing):
                        Error(IonAnalyticCodes.ION0002_DuplicateDefinition, def, msg.Name, module.file.FullName, existing);
                    break;
                    case IonMessageSyntax msg:
                        nameToDef[msg.Name] = def;
                        break;
                }
            }
        }
    }
}