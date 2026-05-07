namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

public class IonFormattingHandler(IonWorkspace workspace) : DocumentFormattingHandlerBase
{
    protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentFormattingCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentFormattingRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion")
        };
    }

    public override Task<TextEditContainer?> Handle(DocumentFormattingParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);

        if (content is null)
            return Task.FromResult<TextEditContainer?>(null);

        var tabSize = (int)(request.Options.TabSize);
        var insertSpaces = request.Options.InsertSpaces;
        var indent = insertSpaces ? new string(' ', tabSize) : "\t";

        var formatted = FormatIon(content, indent);

        if (formatted == content)
            return Task.FromResult<TextEditContainer?>(null);

        var lines = content.Split('\n');
        var endLine = lines.Length - 1;
        var endCol = lines[endLine].TrimEnd('\r').Length;

        var edits = new List<TextEdit>
        {
            new()
            {
                Range = new Range(
                    new Position(0, 0),
                    new Position(endLine, endCol)),
                NewText = formatted
            }
        };

        return Task.FromResult<TextEditContainer?>(new TextEditContainer(edits));
    }

    private static string FormatIon(string content, string indent)
    {
        var lines = content.Split('\n');
        var result = new List<string>();
        var depth = 0;
        var prevWasBlank = false;
        var inBlockComment = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r').Trim();

            // Block comment tracking
            if (inBlockComment)
            {
                result.Add(Indent(depth, indent) + line);
                if (line.Contains("*/"))
                    inBlockComment = false;
                prevWasBlank = false;
                continue;
            }

            if (line.Contains("/*") && !line.Contains("*/"))
            {
                result.Add(Indent(depth, indent) + line);
                inBlockComment = true;
                prevWasBlank = false;
                continue;
            }

            // Empty lines — collapse multiple into one, and skip at file start
            if (string.IsNullOrEmpty(line))
            {
                if (!prevWasBlank && result.Count > 0)
                    result.Add("");
                prevWasBlank = true;
                continue;
            }

            prevWasBlank = false;

            // Closing brace decreases depth before indenting
            if (line.StartsWith('}') || line.StartsWith(')'))
            {
                depth = Math.Max(0, depth - 1);
            }

            // Indent the line
            if (line.StartsWith('#'))
            {
                // Directives at column 0
                result.Add(line);
            }
            else
            {
                result.Add(Indent(depth, indent) + line);
            }

            // Opening brace increases depth for next line
            if (line.EndsWith('{') || line.EndsWith('('))
            {
                depth++;
            }
        }

        // Remove trailing blank lines
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
            result.RemoveAt(result.Count - 1);

        // Ensure single trailing newline
        return string.Join("\n", result) + "\n";
    }

    private static string Indent(int depth, string indent)
    {
        if (depth <= 0) return "";
        return string.Concat(Enumerable.Repeat(indent, depth));
    }
}
