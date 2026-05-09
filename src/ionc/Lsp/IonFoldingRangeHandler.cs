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

        if (file is null)
            return Task.FromResult<Container<FoldingRange>?>(null);

        var ranges = new List<FoldingRange>();

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

        // Comment blocks
        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);
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

    private static void AddCommentRanges(List<FoldingRange> ranges, string content)
    {
        var lines = content.Split('\n');
        int? commentStart = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//"))
            {
                commentStart ??= i;
            }
            else
            {
                if (commentStart is not null && i - commentStart.Value >= 2)
                {
                    ranges.Add(new FoldingRange
                    {
                        StartLine = commentStart.Value,
                        EndLine = i - 1,
                        Kind = FoldingRangeKind.Comment
                    });
                }
                commentStart = null;
            }
        }

        // Trailing comment block
        if (commentStart is not null && lines.Length - commentStart.Value >= 2)
        {
            ranges.Add(new FoldingRange
            {
                StartLine = commentStart.Value,
                EndLine = lines.Length - 1,
                Kind = FoldingRangeKind.Comment
            });
        }

        // Block comments /* ... */
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("/*"))
            {
                var start = i;
                while (i < lines.Length && !lines[i].Contains("*/"))
                    i++;
                if (i > start)
                {
                    ranges.Add(new FoldingRange
                    {
                        StartLine = start,
                        EndLine = i,
                        Kind = FoldingRangeKind.Comment
                    });
                }
            }
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
