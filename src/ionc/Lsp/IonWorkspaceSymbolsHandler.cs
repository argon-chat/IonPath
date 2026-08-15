namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol;
using ion.syntax;

public class IonWorkspaceSymbolsHandler(IonWorkspace workspace) : WorkspaceSymbolsHandlerBase
{
    protected override WorkspaceSymbolRegistrationOptions CreateRegistrationOptions(
        WorkspaceSymbolCapability capability, ClientCapabilities clientCapabilities)
    {
        return new WorkspaceSymbolRegistrationOptions();
    }

    public override Task<Container<WorkspaceSymbol>?> Handle(WorkspaceSymbolParams request,
        CancellationToken cancellationToken)
    {
        var query = request.Query ?? "";
        var symbols = new List<WorkspaceSymbol>();

        foreach (var file in workspace.ParsedFiles)
        {
            var fileUri = DocumentUri.FromFileSystemPath(workspace.GetFileUri(file));

            foreach (var msg in file.messageSyntaxes)
            {
                if (Matches(msg.Name.Identifier, query))
                    symbols.Add(MakeSymbol(msg.Name.Identifier, SymbolKind.Struct, fileUri, msg));
            }

            foreach (var svc in file.serviceSyntaxes)
            {
                if (Matches(svc.serviceName.Identifier, query))
                    symbols.Add(MakeSymbol(svc.serviceName.Identifier, SymbolKind.Interface, fileUri, svc));

                foreach (var m in svc.Methods)
                {
                    if (Matches(m.methodName.Identifier, query))
                        symbols.Add(MakeSymbol(m.methodName.Identifier, SymbolKind.Method, fileUri, m,
                            svc.serviceName.Identifier));
                }
            }

            foreach (var en in file.enumSyntaxes)
            {
                if (Matches(en.Name.Identifier, query))
                    symbols.Add(MakeSymbol(en.Name.Identifier, SymbolKind.Enum, fileUri, en));
            }

            foreach (var fl in file.flagsSyntaxes)
            {
                if (Matches(fl.Name.Identifier, query))
                    symbols.Add(MakeSymbol(fl.Name.Identifier, SymbolKind.Enum, fileUri, fl));
            }

            foreach (var un in file.unionSyntaxes)
            {
                if (Matches(un.unionName.Identifier, query))
                    symbols.Add(MakeSymbol(un.unionName.Identifier, SymbolKind.Class, fileUri, un));
            }

            foreach (var td in file.typedefSyntaxes)
            {
                if (Matches(td.TypeName.Name.Identifier, query))
                    symbols.Add(MakeSymbol(td.TypeName.Name.Identifier, SymbolKind.TypeParameter, fileUri, td));
            }

            foreach (var attr in file.attributeDefSyntaxes)
            {
                if (Matches(attr.Name.Identifier, query))
                    symbols.Add(MakeSymbol(attr.Name.Identifier, SymbolKind.Property, fileUri, attr));
            }

            // `Interface`, matching the document outline: a mixin is a contract, not a value type.
            foreach (var mixin in file.mixinSyntaxes)
            {
                if (Matches(mixin.Name.Identifier, query))
                    symbols.Add(MakeSymbol(mixin.Name.Identifier, SymbolKind.Interface, fileUri, mixin));
            }
        }

        return Task.FromResult<Container<WorkspaceSymbol>?>(new Container<WorkspaceSymbol>(symbols));
    }

    private static bool Matches(string name, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        return name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkspaceSymbol MakeSymbol(
        string name, SymbolKind kind, DocumentUri fileUri, IonSyntaxBase node,
        string? containerName = null)
    {
        var range = IonLspHelpers.ToLspRange(node);
        return new WorkspaceSymbol
        {
            Name = name,
            Kind = kind,
            ContainerName = containerName,
            Location = new Location
            {
                Uri = fileUri,
                Range = range
            }
        };
    }
}
