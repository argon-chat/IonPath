namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

public class IonDefinitionHandler(IonWorkspace workspace) : DefinitionHandlerBase
{
    protected override DefinitionRegistrationOptions CreateRegistrationOptions(
        DefinitionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion")
        };
    }

    public override Task<LocationOrLocationLinks> Handle(DefinitionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);

        if (content is null)
            return Task.FromResult(new LocationOrLocationLinks());

        var word = IonLspHelpers.GetWordAtPosition(content, request.Position.Line, request.Position.Character);
        if (string.IsNullOrEmpty(word))
            return Task.FromResult(new LocationOrLocationLinks());

        var defs = IonLspHelpers.FindDefinitions(word, workspace);
        if (defs.Count == 0)
            return Task.FromResult(new LocationOrLocationLinks());

        var locations = defs.Select(d => new LocationOrLocationLink(new Location
        {
            Uri = DocumentUri.FromFileSystemPath(d.fileUri),
            Range = IonLspHelpers.ToLspRange(d.node)
        })).ToArray();

        return Task.FromResult(new LocationOrLocationLinks(locations));
    }
}
