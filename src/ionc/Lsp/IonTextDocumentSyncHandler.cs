namespace ion.compiler.Lsp;

using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using ion.runtime;

public class IonTextDocumentSyncHandler(IonWorkspace workspace, ILanguageServerFacade server) : TextDocumentSyncHandlerBase
{
    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TextDocumentSyncRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion"),
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions { IncludeText = true }
        };
    }

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        return new TextDocumentAttributes(uri, "ion");
    }

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        workspace.OpenDocument(uri, request.TextDocument.Text);
        PublishDiagnostics();
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        // Full sync — take last content change
        var content = request.ContentChanges.LastOrDefault()?.Text;
        if (content is not null)
        {
            workspace.UpdateDocument(uri, content);
            PublishDiagnostics();
        }
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        workspace.CloseDocument(uri);

        // Clear diagnostics for closed file
        server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = request.TextDocument.Uri,
            Diagnostics = new Container<Diagnostic>()
        });
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        if (request.Text is not null)
        {
            var uri = request.TextDocument.Uri.GetFileSystemPath();
            workspace.UpdateDocument(uri, request.Text);
        }
        PublishDiagnostics();
        return Unit.Task;
    }

    private void PublishDiagnostics()
    {
        var allDiags = workspace.CompileAll();

        foreach (var (fileUri, diags) in allDiags)
        {
            var lspDiags = diags.Select(ConvertDiagnostic).ToList();

            server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
            {
                Uri = DocumentUri.FromFileSystemPath(fileUri),
                Diagnostics = new Container<Diagnostic>(lspDiags)
            });
        }
    }

    private static Diagnostic ConvertDiagnostic(IonDiagnostic diag)
    {
        // Pidgin SourcePos is 1-based, LSP Position is 0-based
        var startLine = Math.Max(0, diag.StartPosition.Line - 1);
        var startCol = Math.Max(0, diag.StartPosition.Col - 1);

        var endLine = startLine;
        var endCol = startCol + 1;
        if (diag.EndPosition is { } endPos)
        {
            endLine = Math.Max(0, endPos.Line - 1);
            endCol = Math.Max(0, endPos.Col - 1);
        }

        return new Diagnostic
        {
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                new Position(startLine, startCol),
                new Position(endLine, endCol)
            ),
            Severity = diag.Severity switch
            {
                IonDiagnosticSeverity.Error => DiagnosticSeverity.Error,
                IonDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
                IonDiagnosticSeverity.Info => DiagnosticSeverity.Information,
                _ => DiagnosticSeverity.Hint
            },
            Code = diag.Code,
            Source = "ionc",
            Message = diag.Message
        };
    }
}
