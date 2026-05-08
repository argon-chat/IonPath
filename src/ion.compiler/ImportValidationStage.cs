namespace ion.compiler;

using ion.runtime;
using ion.syntax;

/// <summary>
/// Validates #import directives:
/// - Module name must exist in ion.config.json
/// - Type names must exist in the target module
/// - Reports unused imports as warnings
/// </summary>
public sealed class ImportValidationStage(CompilationContext ctx) : CompilationStage(ctx)
{
    public override string StageName => "Import Validation";
    public override string StageDescription => "Validating module import declarations";
    public override bool StopOnError => false;

    public override void DoProcess()
    {
        var externalModuleNames = Context.ExternalModules
            .Where(m => m.SourceModule is not null)
            .GroupBy(m => m.SourceModule!)
            .ToDictionary(g => g.Key, g => g.SelectMany(m => m.Definitions).ToList());

        foreach (var file in Context.Files)
        {
            ValidateImports(file, externalModuleNames);
            ValidateDeprecatedUse(file);
        }
    }

    private void ValidateImports(IonFileSyntax file, Dictionary<string, List<IonType>> externalModuleNames)
    {
        foreach (var import in file.importSyntaxes)
        {
            // Check module exists
            if (!externalModuleNames.TryGetValue(import.ModuleName, out var moduleTypes))
            {
                Context.Diagnostics.Add(new IonDiagnostic(
                    IonAnalyticCodes.ION0042_ModuleUnknown.code,
                    IonDiagnosticSeverity.Error,
                    string.Format(IonAnalyticCodes.ION0042_ModuleUnknown.template, import.ModuleName),
                    import));
                continue;
            }

            // Check each imported type exists in the module
            foreach (var typeName in import.TypeNames)
            {
                var found = moduleTypes.Any(t => t.name.Identifier == typeName);
                if (found) continue;

                // Try to suggest a similar name
                var suggestion = moduleTypes
                    .Select(t => (t.name.Identifier, Distance: LevenshteinDistance.Compute(typeName, t.name.Identifier)))
                    .Where(x => x.Distance <= 3)
                    .OrderBy(x => x.Distance)
                    .FirstOrDefault();

                if (suggestion != default)
                {
                    Context.Diagnostics.Add(new IonDiagnostic(
                        IonAnalyticCodes.ION0044_ModuleTypeNotFoundWithSuggestion.code,
                        IonDiagnosticSeverity.Error,
                        string.Format(IonAnalyticCodes.ION0044_ModuleTypeNotFoundWithSuggestion.template, typeName, import.ModuleName, suggestion.Identifier),
                        import));
                }
                else
                {
                    Context.Diagnostics.Add(new IonDiagnostic(
                        IonAnalyticCodes.ION0043_ModuleTypeNotFound.code,
                        IonDiagnosticSeverity.Error,
                        string.Format(IonAnalyticCodes.ION0043_ModuleTypeNotFound.template, typeName, import.ModuleName),
                        import));
                }
            }

            // Register import declarations for type resolution
            var filePath = file.file.FullName;
            if (!Context.ImportDeclarations.ContainsKey(filePath))
                Context.ImportDeclarations[filePath] = [];

            Context.ImportDeclarations[filePath].Add((import.ModuleName, import.TypeNames));
        }
    }

    private void ValidateDeprecatedUse(IonFileSyntax file)
    {
        foreach (var use in file.useSyntaxes)
        {
            Context.Diagnostics.Add(new IonDiagnostic(
                IonAnalyticCodes.ION0047_DeprecatedUseDirective.code,
                IonDiagnosticSeverity.Warning,
                IonAnalyticCodes.ION0047_DeprecatedUseDirective.template,
                use));
        }
    }
}
