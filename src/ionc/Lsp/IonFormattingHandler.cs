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

    /// <summary>
    /// Re-indents an Ion document.
    /// <para>
    /// Brace depth is derived exclusively from characters the scanner classified as code, so
    /// neither a comment nor a string literal can move the indent level: a comment line that
    /// happens to end in <c>{</c> no longer indents the rest of the file, and <c>"}"</c>
    /// inside a string literal no longer dedents it. Comment text is never rewritten — a
    /// full-line comment is only re-indented, a trailing comment stays on the line it
    /// annotates, and the interior of a block comment is copied through verbatim.
    /// </para>
    /// </summary>
    private static string FormatIon(string content, string indent)
    {
        var scanned = IonCommentScanner.Scan(content);
        var lines = scanned.Lines;
        var result = new List<string>();
        var depth = 0;
        var prevWasBlank = false;
        // How far the line that opened the current block comment moved, so its interior
        // keeps the same relative alignment (ASCII art, aligned `*` columns, ...).
        var blockShift = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i].TrimEnd('\r');
            var line = rawLine.Trim();

            var firstCode = scanned.FirstCodeChar(i);
            var lastCode = scanned.LastCodeChar(i);
            var hasCode = !scanned.HasNoCode(i);

            // Interior / closing line of a block comment that carries no code: copy the
            // author's text through untouched, shifted by the same amount as its opener.
            if (scanned.OpensInsideBlockComment[i] && !hasCode)
            {
                result.Add(Shift(rawLine, blockShift));
                prevWasBlank = false;
                continue;
            }

            // Empty lines — collapse multiple into one, and skip at file start
            if (line.Length == 0)
            {
                if (!prevWasBlank && result.Count > 0)
                    result.Add("");
                prevWasBlank = true;
                continue;
            }

            prevWasBlank = false;

            string emitted;

            if (!hasCode)
            {
                // Comment-only line: indent it with the surrounding block, but it must never
                // influence the brace depth.
                emitted = Indent(depth, indent) + line;
            }
            else
            {
                // Closing brace decreases depth before indenting
                if (firstCode >= 0 && rawLine[firstCode] is '}' or ')')
                    depth = Math.Max(0, depth - 1);

                // Directives at column 0
                emitted = firstCode >= 0 && rawLine[firstCode] == '#'
                    ? line
                    : Indent(depth, indent) + line;

                // Opening brace increases depth for next line
                if (lastCode >= 0 && rawLine[lastCode] is '{' or '(')
                    depth++;
            }

            result.Add(emitted);

            if (i + 1 < lines.Length && scanned.OpensInsideBlockComment[i + 1])
                blockShift = LeadingWidth(emitted) - LeadingWidth(rawLine);
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

    private static int LeadingWidth(string line)
    {
        var n = 0;
        while (n < line.Length && char.IsWhiteSpace(line[n])) n++;
        return n;
    }

    /// <summary>
    /// Moves a line horizontally without touching its content, so that the inside of a block
    /// comment follows its opening delimiter instead of being re-flowed.
    /// </summary>
    private static string Shift(string line, int amount)
    {
        if (amount == 0 || line.Length == 0) return line;
        if (amount > 0) return new string(' ', amount) + line;

        var removable = Math.Min(-amount, LeadingWidth(line));
        return line[removable..];
    }
}
