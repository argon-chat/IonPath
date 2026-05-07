namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

public class IonCompletionHandler(IonWorkspace workspace) : CompletionHandlerBase
{
    protected override CompletionRegistrationOptions CreateRegistrationOptions(
        CompletionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CompletionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion"),
            TriggerCharacters = new Container<string>(":", "<", "#"),
            ResolveProvider = false
        };
    }

    public override Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);

        var items = IonLspHelpers.GetCompletionItems(workspace);

        // Check if we're inside a directive context (#)
        if (content is not null)
        {
            var lines = content.Split('\n');
            var line = request.Position.Line;
            if (line >= 0 && line < lines.Length)
            {
                var lineText = lines[line].TrimEnd('\r');
                var prefix = lineText[..Math.Min(request.Position.Character, lineText.Length)].TrimStart();

                if (prefix.StartsWith("#use"))
                {
                    // Don't suggest types after #use
                    items = [new CompletionItem
                    {
                        Label = "#use",
                        Kind = CompletionItemKind.Snippet,
                        InsertText = "#use \"$1\"",
                        InsertTextFormat = InsertTextFormat.Snippet,
                        Detail = "Import module"
                    }];
                }
                else if (prefix.StartsWith("#feature"))
                {
                    items =
                    [
                        new CompletionItem { Label = "std", Kind = CompletionItemKind.Value, Detail = "Standard library" },
                        new CompletionItem { Label = "vector", Kind = CompletionItemKind.Value, Detail = "Vector types" },
                        new CompletionItem { Label = "orleans", Kind = CompletionItemKind.Value, Detail = "Orleans integration" },
                    ];
                }
                else if (prefix.StartsWith("#"))
                {
                    items =
                    [
                        new CompletionItem
                        {
                            Label = "#use",
                            Kind = CompletionItemKind.Keyword,
                            InsertText = "#use \"$1\"",
                            InsertTextFormat = InsertTextFormat.Snippet,
                            Detail = "Import module"
                        },
                        new CompletionItem
                        {
                            Label = "#feature",
                            Kind = CompletionItemKind.Keyword,
                            InsertText = "#feature \"$1\"",
                            InsertTextFormat = InsertTextFormat.Snippet,
                            Detail = "Enable feature"
                        },
                    ];
                }
            }
        }

        return Task.FromResult(new CompletionList(items));
    }

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }
}
