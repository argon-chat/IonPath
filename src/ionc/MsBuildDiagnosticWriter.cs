namespace ion.compiler;

using runtime;

/// <summary>
/// Writes diagnostics in MSBuild's canonical error format, one per line.
/// </summary>
/// <remarks>
/// <c>file(line,col[,endLine,endCol]): error|warning CODE: message</c> is the shape MSBuild's tool
/// tasks (<c>ToolTask</c>, <c>Exec</c>) recognise on a tool's stdout and re-raise as real build
/// errors and warnings: they fail the build, land in the IDE's error list, and navigate to the
/// <c>.ion</c> source.
/// Every other line is logged as a plain message, which is where an
/// <see cref="IonDiagnosticSeverity.Info"/> diagnostic belongs — it is written in the same shape
/// with <c>info</c>, so it stays readable without counting as a warning.
/// </remarks>
public static class MsBuildDiagnosticWriter
{
    public static void Write(IEnumerable<IonDiagnostic> diagnostics, TextWriter output)
    {
        foreach (var diagnostic in diagnostics.OrderBy(d => d.SourceFile?.FullName, StringComparer.Ordinal)
                     .ThenBy(d => d.StartPosition))
            output.WriteLine(Format(diagnostic));
    }

    public static string Format(IonDiagnostic diagnostic)
    {
        var category = diagnostic.Severity switch
        {
            IonDiagnosticSeverity.Error => "error",
            IonDiagnosticSeverity.Warning => "warning",
            _ => "info"
        };

        return $"{Origin(diagnostic)}: {category} {diagnostic.Code}: {SingleLine(diagnostic.Message)}";
    }

    private static string Origin(IonDiagnostic diagnostic)
    {
        // A diagnostic with no source at all (a lock or module error) is attributed to the tool;
        // MSBuild accepts any colon-free origin, not only a path.
        if (diagnostic.SourceFile is null)
            return "ionc";

        var path = diagnostic.SourceFile.FullName;
        var start = diagnostic.StartPosition;

        // A file-level diagnostic (an unparseable file, a config warning) carries no position.
        if (start.Line <= 0)
            return path;

        if (diagnostic.EndPosition is { } end && (end.Line > start.Line || (end.Line == start.Line && end.Col > start.Col)))
            return $"{path}({start.Line},{start.Col},{end.Line},{end.Col})";

        return $"{path}({start.Line},{start.Col})";
    }

    /// <summary>
    /// The canonical format is one line per diagnostic; a continuation line would be logged as a
    /// separate plain message, detached from the error it explains.
    /// </summary>
    private static string SingleLine(string message)
        => string.Join(' ', message.Split(['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
