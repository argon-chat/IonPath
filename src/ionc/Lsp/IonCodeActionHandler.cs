namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ion.runtime;

/// <summary>
/// Provides quick fixes for diagnostics, e.g., "Did you mean X?" suggestions.
/// </summary>
public class IonCodeActionHandler(IonWorkspace workspace) : CodeActionHandlerBase
{
    protected override CodeActionRegistrationOptions CreateRegistrationOptions(
        CodeActionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CodeActionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion"),
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix)
        };
    }

    public override Task<CommandOrCodeActionContainer> Handle(CodeActionParams request, CancellationToken cancellationToken)
    {
        var actions = new List<CommandOrCodeAction>();

        foreach (var diag in request.Context.Diagnostics)
        {
            // ION0009 — "Did you mean 'X'?"
            if (diag.Code?.String is "ION0009" && diag.Message.Contains("Did you mean '"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    diag.Message, @"Did you mean '([^']+)'\?");
                if (match.Success)
                {
                    var suggestion = match.Groups[1].Value;
                    var edit = new WorkspaceEdit
                    {
                        Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                        {
                            [request.TextDocument.Uri] = [new TextEdit
                            {
                                Range = diag.Range,
                                NewText = suggestion
                            }]
                        }
                    };

                    actions.Add(new CodeAction
                    {
                        Title = $"Replace with '{suggestion}'",
                        Kind = CodeActionKind.QuickFix,
                        Diagnostics = new Container<Diagnostic>(diag),
                        Edit = edit,
                        IsPreferred = true
                    });
                }
            }

            // ION1001 — Unused type: offer to remove (just prefix with _)
            if (diag.Code?.String is "ION1001")
            {
                // No auto-fix for unused types - they just need the warning
            }

            // ION0029 — Non-nullable field added: offer to make nullable
            if (diag.Code?.String is "ION0029" && diag.Message.Contains("Consider using '"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    diag.Message, @"Consider using '([^']+)'");
                if (match.Success)
                {
                    var suggestion = match.Groups[1].Value;
                    actions.Add(new CodeAction
                    {
                        Title = $"Make field nullable: {suggestion}",
                        Kind = CodeActionKind.QuickFix,
                        Diagnostics = new Container<Diagnostic>(diag),
                        // This would need more context to build a proper edit
                    });
                }
            }
        }

        return Task.FromResult(new CommandOrCodeActionContainer(actions));
    }

    public override Task<CodeAction> Handle(CodeAction request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }
}
