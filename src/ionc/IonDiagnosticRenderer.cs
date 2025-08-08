using System.Text;
using ion.runtime;
using Pidgin;
using Spectre.Console;

public static class IonDiagnosticRenderer
{
    public static void RenderParseError<TToken>(
        ParseError<TToken> error,
        FileInfo? sourceFile,
        string code = "")
    {
        const string color = "red";

        var location = sourceFile != null
            ? $"--> {sourceFile.FullName}:{error.ErrorPos.Line}:{error.ErrorPos.Col}"
            : $"--> (unknown location)";

        AnsiConsole.MarkupLine($"[{color} bold]error[/] [{color}]{code}[/]");
        AnsiConsole.MarkupLine($"  [grey]{location}[/]");
        AnsiConsole.MarkupLine("   |");

        var lines = sourceFile is not null ? File.ReadAllLines(sourceFile.FullName).ToList() : [];
        var lineIdx = error.ErrorPos.Line - 1;
        if (lineIdx < lines.Count)
        {
            var line = lines[lineIdx].Replace("\t", "    ");
            AnsiConsole.MarkupLine($"{error.ErrorPos.Line,3}| {line.EscapeMarkup()}");

            var visualCol = GetVisualOffset(line, error.ErrorPos.Col - 1);
            var pointer = new string(' ', visualCol) + $"[{color}]^[/]";
            AnsiConsole.MarkupLine("   | " + pointer);
        }

        var message = error.Message
                      ?? BuildExpectedMessage(error.Expected.Select(e => e.ToString()).ToList());

        AnsiConsole.MarkupLine($"  [{color}]{message}[/]");
        AnsiConsole.WriteLine();
    }

    private static string BuildExpectedMessage(List<string> expected)
    {
        if (expected.Count == 0)
            return "Unknown parse error";
        if (expected.Count == 1)
            return $"expected {expected[0]}";
        var joined = string.Join(", ", expected.Take(expected.Count - 1)) + " or " + expected.Last();
        return $"expected {joined}";
    }

    private static int GetVisualOffset(string line, int col)
    {
        var visual = 0;
        for (var i = 0; i < col && i < line.Length; i++)
            visual += line[i] == '\t' ? 4 : 1;
        return visual;
    }

    public static void RenderDiagnostics(List<IonDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics.OrderBy(d => d.SourceFile?.FullName)
                     .ThenBy(d => d.StartPosition))
        {
            var color = diagnostic.Severity switch
            {
                IonDiagnosticSeverity.Error => "red",
                IonDiagnosticSeverity.Warning => "yellow",
                IonDiagnosticSeverity.Info => "blue",
                _ => "grey"
            };
            var header = $"[{color} bold]{diagnostic.Severity.ToString().ToLower()}[/] [{color}]{diagnostic.Code}[/] - {diagnostic.Message}";
            var location = diagnostic.SourceFile != null
                ? $"--> {diagnostic.SourceFile.FullName}:{diagnostic.StartPosition.Line}:{diagnostic.StartPosition.Col}"
                : "--> (unknown location)";

            AnsiConsole.MarkupLine(header);
            AnsiConsole.MarkupLine($"  [grey]{location}[/]");

            if (diagnostic.SourceFile != null && File.Exists(diagnostic.SourceFile.FullName))
            {
                var lines = File.ReadAllLines(diagnostic.SourceFile.FullName);
                var startLine = diagnostic.StartPosition.Line - 1;
                var endLine = (diagnostic.EndPosition?.Line ?? diagnostic.StartPosition.Line) - 1;

                if (startLine >= lines.Length)
                {
                    AnsiConsole.MarkupLine($"  [grey](line {diagnostic.StartPosition.Line} out of bounds)[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("   |");
                    for (var i = startLine; i <= endLine && i < lines.Length; i++)
                    {
                        var line = lines[i];
                        AnsiConsole.MarkupLine($"{i + 1,2} | [white]{line.EscapeMarkup()}[/]");

                        var underline = BuildUnderlineMultiline(line, i, diagnostic.StartPosition, diagnostic.EndPosition, color);
                        AnsiConsole.MarkupLine($"   | {underline}");
                    }
                }
            }
            AnsiConsole.MarkupLine($"  [{color}]{diagnostic.Message}[/]");
            AnsiConsole.WriteLine();
        }
    }

    private static string BuildUnderlineMultiline(string line, int currentLineIndex, SourcePos start, SourcePos? end, string color)
    {
        var sb = new StringBuilder();
        var startCol = 0;
        var endCol = line.Length;

        if (currentLineIndex == start.Line - 1)
            startCol = Math.Max(start.Col - 1, 0);

        if (end.HasValue && currentLineIndex == end.Value.Line - 1)
            endCol = Math.Max(Math.Min(end.Value.Col - 1, line.Length), startCol);

        var length = Math.Max(endCol - startCol, 1);

        sb.Append(' ', startCol);
        sb.Append($"[{color}]");
        sb.Append('^');
        if (length > 1)
            sb.Append('~', length - 1);
        sb.Append("[/]");
        return sb.ToString();
    }
}