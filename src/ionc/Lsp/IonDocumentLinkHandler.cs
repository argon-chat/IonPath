namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ion.syntax;

public class IonDocumentLinkHandler(IonWorkspace workspace) : DocumentLinkHandlerBase
{
    protected override DocumentLinkRegistrationOptions CreateRegistrationOptions(
        DocumentLinkCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentLinkRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion"),
            ResolveProvider = false
        };
    }

    public override Task<DocumentLinkContainer> Handle(DocumentLinkParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);

        if (content is null)
            return Task.FromResult(new DocumentLinkContainer());

        var links = new List<DocumentLink>();
        var scanned = IonCommentScanner.Scan(content);
        var lines = scanned.Lines;
        var rootDir = Path.GetDirectoryName(uri) ?? "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (!trimmed.StartsWith("#use"))
                continue;

            // A `#use` that lives inside a comment (or a string literal) is not a directive.
            var directiveStart = line.Length - trimmed.Length;
            if (scanned.IsCommentOrString(i, directiveStart))
                continue;

            // Find the quoted path: #use "path/to/module"
            var quoteStart = line.IndexOf('"');
            var quoteEnd = quoteStart >= 0 ? line.IndexOf('"', quoteStart + 1) : -1;

            if (quoteStart < 0 || quoteEnd < 0)
                continue;

            var usePath = line[(quoteStart + 1)..quoteEnd];

            // Resolve to actual file
            var resolvedPath = ResolveUsePath(rootDir, usePath);
            if (resolvedPath is null)
                continue;

            links.Add(new DocumentLink
            {
                Range = new Range(
                    new Position(i, quoteStart),
                    new Position(i, quoteEnd + 1)),
                Target = new Uri($"file:///{resolvedPath.Replace('\\', '/')}"),
                // `//!` module doc of the target file, when it has one.
                Tooltip = IonDocMarkdown.ToSingleLine(workspace.FindFileByUri(resolvedPath)?.ModuleDoc)
            });
        }

        return Task.FromResult(new DocumentLinkContainer(links));
    }

    public override Task<DocumentLink> Handle(DocumentLink request, CancellationToken cancellationToken)
        => Task.FromResult(request);

    private static string? ResolveUsePath(string rootDir, string usePath)
    {
        // Try with .ion extension
        var candidate = Path.Combine(rootDir, usePath);
        if (!candidate.EndsWith(".ion", StringComparison.OrdinalIgnoreCase))
            candidate += ".ion";

        candidate = Path.GetFullPath(candidate);
        return File.Exists(candidate) ? candidate : null;
    }
}
