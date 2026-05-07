namespace ion.compiler;

using runtime;
using syntax;

public sealed class UnusedSymbolDetectionStage(CompilationContext context)
    : CompilationStage(context)
{
    public override string StageName => "Unused Symbol Detection";
    public override string StageDescription => "Checking for unused types and imports";
    public override bool StopOnError => false;

    public override void DoProcess()
    {
        DetectUnusedImports();
        DetectUnusedTypes();
    }

    private void DetectUnusedImports()
    {
        foreach (var file in Context.Files)
        {
            foreach (var use in file.useSyntaxes)
            {
                var importedPath = use.Path;
                var isUsed = false;

                // Check if any type from the imported file is referenced in this file
                var importedFile = Context.Files.FirstOrDefault(f =>
                    f.file.Name.Equals(importedPath, StringComparison.OrdinalIgnoreCase) ||
                    f.file.Name.Equals(importedPath + ".ion", StringComparison.OrdinalIgnoreCase));

                if (importedFile is null) continue;

                var importedTypeNames = importedFile.Definitions
                    .Select(GetDefinitionName)
                    .Where(n => n is not null)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Scan all type references in the current file
                var referencedTypes = CollectReferencedTypeNames(file);

                if (importedTypeNames.Any(name => referencedTypes.Contains(name!)))
                    isUsed = true;

                if (!isUsed)
                {
                    Info(IonAnalyticCodes.ION1002_UnusedImport, use, importedPath);
                }
            }
        }
    }

    private void DetectUnusedTypes()
    {
        // Collect all type names defined across all files
        var allDefinedTypes = new Dictionary<string, IonSyntaxMember>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Context.Files)
        {
            foreach (var def in file.Definitions)
            {
                var name = GetDefinitionName(def);
                if (name is not null)
                    allDefinedTypes.TryAdd(name, def);
            }
        }

        // Collect all referenced type names across all files
        var allReferencedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Context.Files)
        {
            foreach (var name in CollectReferencedTypeNames(file))
                allReferencedTypes.Add(name);
        }

        // Check which defined types are never referenced
        foreach (var (name, def) in allDefinedTypes)
        {
            // Services are entry points — never flag as unused
            if (def is IonServiceSyntax) continue;
            // Attribute definitions are metadata — never flag as unused
            if (def is IonAttributeDefSyntax) continue;

            if (!allReferencedTypes.Contains(name))
            {
                Info(IonAnalyticCodes.ION1001_UnusedType, def, name);
            }
        }
    }

    private static string? GetDefinitionName(IonSyntaxMember def) => def switch
    {
        IonMessageSyntax msg => msg.Name.Identifier,
        IonEnumSyntax e => e.Name.Identifier,
        IonFlagsSyntax f => f.Name.Identifier,
        IonServiceSyntax s => s.serviceName.Identifier,
        IonTypedefSyntax t => t.TypeName.Name.Identifier,
        IonUnionSyntax u => u.unionName.Identifier,
        IonAttributeDefSyntax a => a.Name.Identifier,
        _ => null
    };

    private static HashSet<string> CollectReferencedTypeNames(IonFileSyntax file)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var msg in file.messageSyntaxes)
        {
            foreach (var field in msg.Fields)
                CollectTypeRefs(field.Type, refs);
        }

        foreach (var svc in file.serviceSyntaxes)
        {
            foreach (var arg in svc.BaseArguments)
                CollectTypeRefs(arg.type, refs);

            foreach (var method in svc.Methods)
            {
                foreach (var arg in method.arguments)
                    CollectTypeRefs(arg.type, refs);
                if (method.returnType is not null)
                    CollectTypeRefs(method.returnType, refs);
            }
        }

        foreach (var union in file.unionSyntaxes)
        {
            foreach (var field in union.baseFields)
                CollectTypeRefs(field.type, refs);
            foreach (var c in union.cases)
            {
                refs.Add(c.caseName.Name.Identifier);
                foreach (var arg in c.arguments)
                    CollectTypeRefs(arg.type, refs);
            }
        }

        foreach (var td in file.typedefSyntaxes)
        {
            if (td.BaseType is not null)
                CollectTypeRefs(td.BaseType, refs);
        }

        return refs;
    }

    private static void CollectTypeRefs(IonUnderlyingTypeSyntax type, HashSet<string> refs)
    {
        refs.Add(type.Name.Identifier);
        foreach (var generic in type.generics)
            refs.Add(generic.Name.Identifier);
    }
}
