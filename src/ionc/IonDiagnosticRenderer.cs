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
            
            var severityText = diagnostic.Severity.ToString().ToLower();
            var header = $"[{color} bold]{severityText.EscapeMarkup()}[/][{color}]{$"[{diagnostic.Code}]".EscapeMarkup()}[/]: {diagnostic.Message.EscapeMarkup()}";
            var location = diagnostic.SourceFile != null
                ? $"--> {diagnostic.SourceFile.FullName.EscapeMarkup()}:{diagnostic.StartPosition.Line}:{diagnostic.StartPosition.Col}"
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
                    // Show context: 1 line before, error lines, 1 line after
                    var contextStart = Math.Max(0, startLine - 1);
                    var contextEnd = Math.Min(lines.Length - 1, endLine + 1);

                    AnsiConsole.MarkupLine("   |");
                    
                    for (var i = contextStart; i <= contextEnd; i++)
                    {
                        var line = lines[i];
                        var isErrorLine = i >= startLine && i <= endLine;
                        
                        if (isErrorLine)
                        {
                            // Highlight error line
                            AnsiConsole.MarkupLine($"[{color}]{i + 1,3}[/] | [white]{line.EscapeMarkup()}[/]");

                            // Show underline
                            var underline = BuildUnderlineMultiline(line, i, diagnostic.StartPosition, diagnostic.EndPosition, color);
                            AnsiConsole.MarkupLine($"    | {underline}");
                        }
                        else
                        {
                            // Context line
                            AnsiConsole.MarkupLine($"[dim]{i + 1,3} | {line.EscapeMarkup()}[/]");
                        }
                    }
                }
            }
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

        var length = endCol - startCol;
        
        // Special case: point caret (start == end on same line)
        // Show caret at exact position, no underline
        if (end.HasValue && start.Line == end.Value.Line && start.Col == end.Value.Col)
        {
            sb.Append(' ', startCol);
            sb.Append($"[{color}]^ expected here[/]");
            return sb.ToString();
        }

        // Normal case: underline from start to end
        if (length < 1)
            length = 1;

        sb.Append(' ', startCol);
        sb.Append($"[{color}]");
        sb.Append('^');
        if (length > 1)
            sb.Append('~', length - 1);
        sb.Append("[/]");
        return sb.ToString();
    }
}