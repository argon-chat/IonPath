namespace ion.runtime;

using Pidgin;

public sealed record IonDiagnostic(
    string Code,
    IonDiagnosticSeverity Severity,
    string Message,
    SourcePos Position
);

public enum IonDiagnosticSeverity
{
    Info,
    Warning,
    Error
}