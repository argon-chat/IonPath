namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

public class IonRenameHandler(IonWorkspace workspace) : RenameHandlerBase
{
    protected override RenameRegistrationOptions CreateRegistrationOptions(
        RenameCapability capability, ClientCapabilities clientCapabilities)
    {
        return new RenameRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion"),
            PrepareProvider = true
        };
    }

    public override Task<WorkspaceEdit?> Handle(RenameParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);

        if (content is null)
            return Task.FromResult<WorkspaceEdit?>(null);

        // A word that merely appears inside a comment or a string literal is not a symbol.
        // Renaming from such a position would silently rewrite every real declaration with
        // that name while leaving the prose the user was pointing at untouched.
        if (IonLspHelpers.IsInCommentOrString(content, request.Position.Line, request.Position.Character))
            return Task.FromResult<WorkspaceEdit?>(null);

        var word = IonLspHelpers.GetWordAtPosition(content, request.Position.Line, request.Position.Character);
        if (string.IsNullOrEmpty(word))
            return Task.FromResult<WorkspaceEdit?>(null);

        var newName = request.NewName;

        // Find all references including definition
        var refs = IonLspHelpers.FindReferences(word, workspace, includeDefinition: true);
        if (refs.Count == 0)
            return Task.FromResult<WorkspaceEdit?>(null);

        // Group by file URI
        var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();
        foreach (var group in refs.GroupBy(r => r.fileUri, StringComparer.OrdinalIgnoreCase))
        {
            var edits = group.Select(r => new TextEdit
            {
                Range = IonLspHelpers.ToLspRange(r.node),
                NewText = newName
            }).ToList();

            changes[DocumentUri.FromFileSystemPath(group.Key)] = edits;
        }

        return Task.FromResult<WorkspaceEdit?>(new WorkspaceEdit { Changes = changes });
    }
}
