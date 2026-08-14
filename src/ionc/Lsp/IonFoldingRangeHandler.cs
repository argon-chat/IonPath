namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ion.syntax;

public class IonFoldingRangeHandler(IonWorkspace workspace) : FoldingRangeHandlerBase
{
    protected override FoldingRangeRegistrationOptions CreateRegistrationOptions(
        FoldingRangeCapability capability, ClientCapabilities clientCapabilities)
    {
        return new FoldingRangeRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion")
        };
    }

    public override Task<Container<FoldingRange>?> Handle(FoldingRangeRequestParam request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var file = workspace.ParsedFiles
            .FirstOrDefault(f => workspace.GetFileUri(f).Equals(uri, StringComparison.OrdinalIgnoreCase));

        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);

        if (file is null && content is null)
            return Task.FromResult<Container<FoldingRange>?>(null);

        var ranges = new List<FoldingRange>();

        if (file is not null)
        {
            // Messages
            foreach (var msg in file.messageSyntaxes)
                AddRange(ranges, msg, FoldingRangeKind.Region);

            // Services
            foreach (var svc in file.serviceSyntaxes)
                AddRange(ranges, svc, FoldingRangeKind.Region);

            // Enums
            foreach (var en in file.enumSyntaxes)
                AddRange(ranges, en, FoldingRangeKind.Region);

            // Flags
            foreach (var fl in file.flagsSyntaxes)
                AddRange(ranges, fl, FoldingRangeKind.Region);

            // Unions
            foreach (var un in file.unionSyntaxes)
                AddRange(ranges, un, FoldingRangeKind.Region);
        }

        // Comment blocks
        if (content is not null)
            AddCommentRanges(ranges, content);

        // #import / #use / #feature directive blocks
        if (content is not null)
            AddDirectiveRanges(ranges, content);

        return Task.FromResult<Container<FoldingRange>?>(new Container<FoldingRange>(ranges));
    }

    private static void AddRange(List<FoldingRange> ranges, IonSyntaxBase node, FoldingRangeKind kind)
    {
        if (node.EndPosition is null) return;
        var startLine = node.StartPosition.Line - 1; // 0-based
        var endLine = node.EndPosition.Value.Line - 1;
        if (endLine <= startLine) return;

        ranges.Add(new FoldingRange
        {
            StartLine = startLine,
            EndLine = endLine,
            Kind = kind
        });
    }

    /// <summary>
    /// Folds comments. Driven by <see cref="IonCommentScanner"/> rather than raw
    /// <c>Contains("//")</c> / <c>Contains("/*")</c> checks, which treat a <c>/*</c> inside a
    /// string literal as the start of a block comment and then swallow every following line
    /// until some unrelated line happens to contain <c>*/</c>.
    /// </summary>
    private static void AddCommentRanges(List<FoldingRange> ranges, string content)
    {
        var scanned = IonCommentScanner.Scan(content);
        var lineCount = scanned.Lines.Length;

        // Block comments that actually span more than one line.
        var coveredByBlock = new bool[lineCount];
        foreach (var comment in scanned.Comments)
        {
            if (!comment.IsBlock) continue;

            for (var i = comment.StartLine; i <= comment.EndLine && i < lineCount; i++)
                coveredByBlock[i] = true;

            if (!comment.IsMultiLine) continue;

            ranges.Add(new FoldingRange
            {
                StartLine = comment.StartLine,
                EndLine = Math.Min(comment.EndLine, lineCount - 1),
                Kind = FoldingRangeKind.Comment
            });
        }

        // Runs of consecutive comment-only lines. A run of one line cannot be folded.
        int? runStart = null;
        for (var i = 0; i <= lineCount; i++)
        {
            var isRunLine = i < lineCount && !coveredByBlock[i] && scanned.IsCommentOnly(i);

            if (isRunLine)
            {
                runStart ??= i;
                continue;
            }

            if (runStart is not null && i - runStart.Value >= 2)
            {
                ranges.Add(new FoldingRange
                {
                    StartLine = runStart.Value,
                    EndLine = i - 1,
                    Kind = FoldingRangeKind.Comment
                });
            }
            runStart = null;
        }
    }

    private static void AddDirectiveRanges(List<FoldingRange> ranges, string content)
    {
        var lines = content.Split('\n');
        int? directiveStart = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("#use") || trimmed.StartsWith("#feature") || trimmed.StartsWith("#import"))
            {
                directiveStart ??= i;
            }
            else if (!string.IsNullOrWhiteSpace(trimmed))
            {
                if (directiveStart is not null && i - 1 > directiveStart.Value)
                {
                    ranges.Add(new FoldingRange
                    {
                        StartLine = directiveStart.Value,
                        EndLine = i - 1,
                        Kind = FoldingRangeKind.Imports
                    });
                }
                directiveStart = null;
            }
        }
    }
}
