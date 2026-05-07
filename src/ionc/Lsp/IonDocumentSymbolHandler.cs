namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ion.syntax;

public class IonDocumentSymbolHandler(IonWorkspace workspace) : DocumentSymbolHandlerBase
{
    protected override DocumentSymbolRegistrationOptions CreateRegistrationOptions(
        DocumentSymbolCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentSymbolRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion")
        };
    }

    public override Task<SymbolInformationOrDocumentSymbolContainer> Handle(
        DocumentSymbolParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var file = workspace.ParsedFiles
            .FirstOrDefault(f => workspace.GetFileUri(f).Equals(uri, StringComparison.OrdinalIgnoreCase));

        if (file is null)
            return Task.FromResult(new SymbolInformationOrDocumentSymbolContainer());

        var symbols = new List<SymbolInformationOrDocumentSymbol>();

        foreach (var msg in file.messageSyntaxes)
        {
            var children = msg.Fields.Select(f => MakeSymbol(
                f.Name.Identifier, SymbolKind.Field, ToRange(f))).ToList();
            symbols.Add(MakeSymbol(msg.Name.Identifier, SymbolKind.Struct, ToRange(msg), children));
        }

        foreach (var svc in file.serviceSyntaxes)
        {
            var children = svc.Methods.Select(m => MakeSymbol(
                m.methodName.Identifier, SymbolKind.Method, ToRange(m))).ToList();
            symbols.Add(MakeSymbol(svc.serviceName.Identifier, SymbolKind.Interface, ToRange(svc), children));
        }

        foreach (var en in file.enumSyntaxes)
        {
            var children = en.Entries.Select(e => MakeSymbol(
                e.Name.Identifier, SymbolKind.EnumMember, ToRange(e))).ToList();
            symbols.Add(MakeSymbol(en.Name.Identifier, SymbolKind.Enum, ToRange(en), children));
        }

        foreach (var fl in file.flagsSyntaxes)
        {
            var children = fl.Entries.Select(e => MakeSymbol(
                e.Name.Identifier, SymbolKind.EnumMember, ToRange(e))).ToList();
            symbols.Add(MakeSymbol(fl.Name.Identifier, SymbolKind.Enum, ToRange(fl), children));
        }

        foreach (var un in file.unionSyntaxes)
        {
            var children = un.cases.Select(c => MakeSymbol(
                c.caseName.Name.Identifier, SymbolKind.EnumMember, ToRange(c))).ToList();
            symbols.Add(MakeSymbol(un.unionName.Identifier, SymbolKind.Class, ToRange(un), children));
        }

        foreach (var td in file.typedefSyntaxes)
            symbols.Add(MakeSymbol(td.TypeName.Name.Identifier, SymbolKind.TypeParameter, ToRange(td)));

        foreach (var attr in file.attributeDefSyntaxes)
            symbols.Add(MakeSymbol(attr.Name.Identifier, SymbolKind.Property, ToRange(attr)));

        return Task.FromResult(new SymbolInformationOrDocumentSymbolContainer(symbols));
    }

    private static DocumentSymbol MakeSymbol(
        string name, SymbolKind kind, Range range,
        List<DocumentSymbol>? children = null)
    {
        return new DocumentSymbol
        {
            Name = name,
            Kind = kind,
            Range = range,
            SelectionRange = range,
            Children = children is not null
                ? new Container<DocumentSymbol>(children)
                : null
        };
    }

    private static Range ToRange(IonSyntaxBase node)
    {
        var start = new Position(
            Math.Max(0, node.StartPosition.Line - 1),
            Math.Max(0, node.StartPosition.Col - 1));
        var end = node.EndPosition is { } ep
            ? new Position(Math.Max(0, ep.Line - 1), Math.Max(0, ep.Col - 1))
            : start;
        return new Range(start, end);
    }
}
