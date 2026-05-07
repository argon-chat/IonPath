namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

public class IonReferencesHandler(IonWorkspace workspace) : ReferencesHandlerBase
{
    protected override ReferenceRegistrationOptions CreateRegistrationOptions(
        ReferenceCapability capability, ClientCapabilities clientCapabilities)
    {
        return new ReferenceRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion")
        };
    }

    public override Task<LocationContainer> Handle(ReferenceParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);

        if (content is null)
            return Task.FromResult(new LocationContainer());

        var word = IonLspHelpers.GetWordAtPosition(content, request.Position.Line, request.Position.Character);
        if (string.IsNullOrEmpty(word))
            return Task.FromResult(new LocationContainer());

        var includeDeclaration = request.Context.IncludeDeclaration;
        var refs = IonLspHelpers.FindReferences(word, workspace, includeDeclaration);

        var locations = refs.Select(r => new Location
        {
            Uri = DocumentUri.FromFileSystemPath(r.fileUri),
            Range = IonLspHelpers.ToLspRange(r.node)
        }).ToArray();

        return Task.FromResult(new LocationContainer(locations));
    }
}
