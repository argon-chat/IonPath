namespace ion.runtime;

using Pidgin;
using syntax;

public sealed record IonDiagnostic(
    string Code,
    IonDiagnosticSeverity Severity,
    string Message,
    IonSyntaxBase @base
)
{
    public SourcePos StartPosition => @base.StartPosition;
    public FileInfo? SourceFile => @base.SourceFile;
    public SourcePos? EndPosition => @base.EndPosition;
}

public enum IonDiagnosticSeverity
{
    Info,
    Warning,
    Error
}