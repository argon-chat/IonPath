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
            TriggerCharacters = new Container<string>(":", "<", "#", "{", ",", "\""),
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

                if (prefix.StartsWith("#import") || lineText.TrimStart().StartsWith("#import"))
                {
                    var trimmedLine = lineText.TrimStart();
                    var cursorInLine = request.Position.Character;

                    // Find where { and } are relative to cursor
                    var braceOpen = trimmedLine.IndexOf('{');
                    var braceClose = trimmedLine.IndexOf('}');
                    var fromIdx = trimmedLine.IndexOf("from", StringComparison.Ordinal);

                    if (fromIdx >= 0 && cursorInLine > lineText.IndexOf("from", StringComparison.Ordinal))
                    {
                        // After "from" — suggest module names
                        items = GetModuleNameCompletions();
                    }
                    else if (braceOpen >= 0 && (braceClose < 0 || cursorInLine <= lineText.IndexOf('}'))
                             && cursorInLine > lineText.IndexOf('{'))
                    {
                        // Inside { } — suggest type names
                        var moduleName = ExtractModuleNameFromLine(lineText);
                        if (moduleName is not null)
                            items = GetModuleTypeCompletions(moduleName);
                        else
                            items = GetAllExternalTypeCompletions();
                    }
                    else
                    {
                        // Just typed #import, offer snippet
                        items =
                        [
                            new CompletionItem
                            {
                                Label = "#import",
                                Kind = CompletionItemKind.Keyword,
                                InsertText = "#import { $1 } from \"$2\"",
                                InsertTextFormat = InsertTextFormat.Snippet,
                                Detail = "Import types from module"
                            }
                        ];
                    }
                }
                else if (prefix.StartsWith("#use"))
                {
                    // Don't suggest types after #use
                    items = [new CompletionItem
                    {
                        Label = "#use",
                        Kind = CompletionItemKind.Snippet,
                        InsertText = "#use \"$1\"",
                        InsertTextFormat = InsertTextFormat.Snippet,
                        Detail = "Import module (deprecated, use #import)"
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
                            Label = "#import",
                            Kind = CompletionItemKind.Keyword,
                            InsertText = "#import { $1 } from \"$2\"",
                            InsertTextFormat = InsertTextFormat.Snippet,
                            Detail = "Import types from module"
                        },
                        new CompletionItem
                        {
                            Label = "#use",
                            Kind = CompletionItemKind.Keyword,
                            InsertText = "#use \"$1\"",
                            InsertTextFormat = InsertTextFormat.Snippet,
                            Detail = "Import module (deprecated)"
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

    private List<CompletionItem> GetModuleNameCompletions()
    {
        return workspace.ExternalModules
            .Where(m => m.SourceModule is not null)
            .Select(m => m.SourceModule!)
            .Distinct()
            .Select(name => new CompletionItem
            {
                Label = name,
                Kind = CompletionItemKind.Module,
                Detail = "External module"
            })
            .ToList();
    }

    private List<CompletionItem> GetModuleTypeCompletions(string moduleName)
    {
        return workspace.ExternalModules
            .Where(m => m.SourceModule == moduleName)
            .SelectMany(m => m.Definitions)
            .Select(d => new CompletionItem
            {
                Label = d.name.Identifier,
                Kind = CompletionItemKind.Class,
                Detail = $"from \"{moduleName}\""
            })
            .ToList();
    }

    private List<CompletionItem> GetAllExternalTypeCompletions()
    {
        return workspace.ExternalModules
            .Where(m => m.SourceModule is not null)
            .SelectMany(m => m.Definitions.Select(d => (Module: m.SourceModule!, Type: d)))
            .Select(x => new CompletionItem
            {
                Label = x.Type.name.Identifier,
                Kind = CompletionItemKind.Class,
                Detail = $"from \"{x.Module}\""
            })
            .ToList();
    }

    private static string? ExtractModuleNameFromLine(string lineText)
    {
        // Try to find: from "moduleName"
        var fromIdx = lineText.IndexOf("from", StringComparison.Ordinal);
        if (fromIdx < 0) return null;

        var afterFrom = lineText[(fromIdx + 4)..].Trim();
        if (afterFrom.Length < 3 || afterFrom[0] != '"') return null;

        var endQuote = afterFrom.IndexOf('"', 1);
        if (endQuote < 0) return null;

        return afterFrom[1..endQuote];
    }

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }
}
