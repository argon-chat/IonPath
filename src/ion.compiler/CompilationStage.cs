namespace ion.compiler;

using ion.runtime;
using Pidgin;
using syntax;

public class CompilationContext
{
    public List<IonDiagnostic> Diagnostics { get; } = [];

    public bool HasErrors => Diagnostics.Any(d => d.Severity == IonDiagnosticSeverity.Error);

    public IonAttributeType ResolveAttributeType(string syntaxName)
    {
        
    }
}

public abstract class CompilationStage(CompilationContext context)
{
    public void Error(string code, string message, SourcePos pos) =>
        context.Diagnostics.Add(new(code, IonDiagnosticSeverity.Error, message, pos));
    public void Warning(string code, string message, SourcePos pos) =>
        context.Diagnostics.Add(new(code, IonDiagnosticSeverity.Warning, message, pos));
    public void Error(string code, string message, IonSyntaxBase @base) =>
        context.Diagnostics.Add(new(code, IonDiagnosticSeverity.Error, message, @base.Position));
    public void Warning(string code, string message, IonSyntaxBase @base) =>
        context.Diagnostics.Add(new(code, IonDiagnosticSeverity.Warning, message, @base.Position));
    public void Info(string code, string message, SourcePos pos) =>
        context.Diagnostics.Add(new(code, IonDiagnosticSeverity.Info, message, pos));
    public void Info(string code, string message, IonSyntaxBase @base) =>
        context.Diagnostics.Add(new(code, IonDiagnosticSeverity.Info, message, @base.Position));


    public void Error(IonAnalyticCode code, IonSyntaxBase @base, params object[] args) =>
        context.Diagnostics.Add(new(code.code, IonDiagnosticSeverity.Error, string.Format(code.template, args), @base.Position));
    public void Warn(IonAnalyticCode code, IonSyntaxBase @base, params object[] args) =>
        context.Diagnostics.Add(new(code.code, IonDiagnosticSeverity.Warning, string.Format(code.template, args), @base.Position));
    public void Info(IonAnalyticCode code, IonSyntaxBase @base, params object[] args) =>
        context.Diagnostics.Add(new(code.code, IonDiagnosticSeverity.Info, string.Format(code.template, args), @base.Position));
}

public sealed class ReferenceValidationStage(CompilationContext context) : CompilationStage(context)
{
}