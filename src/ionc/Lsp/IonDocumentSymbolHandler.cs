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

        // Hoisted inline types DO appear, deliberately.
        //
        // They are synthesized — the author wrote `shipping: msg { … }`, not `msg OrderShipping`
        // — so listing them is a judgement call rather than an obvious one. They are listed
        // because the derived name is a real top level declaration everywhere downstream: it has
        // a lock entry, a generated declaration in three languages, and it occupies the global type
        // namespace, which is exactly what ION0067 exists to police. An outline that hid it would
        // be hiding a name the author is responsible for. What the outline must not do is pretend
        // the author typed it, so the detail line says where it came from, and its range is the
        // `msg { … }` body it was lifted from — clicking it goes to the right place.
        foreach (var msg in file.messageSyntaxes)
        {
            var children = msg.Fields.Select(f => MakeSymbol(
                f.Name.Identifier, SymbolKind.Field, ToRange(f), f.Comments)).ToList();

            var synthesized = IonLspHelpers.IsHoistedInlineType(msg);

            symbols.Add(MakeSymbol(
                msg.Name.Identifier,
                SymbolKind.Struct,
                ToRange(msg),
                synthesized ? "hoisted from an inline msg { … }" : msg.Comments,
                children));
        }

        // Mixins. A mixin is not a type, so it is `Interface` rather than `Struct` — the same kind
        // a service gets, the other declaration that describes a contract rather than a value.
        foreach (var mixin in file.mixinSyntaxes)
        {
            var children = mixin.Fields.Select(f => MakeSymbol(
                f.Name.Identifier, SymbolKind.Field, ToRange(f), f.Comments)).ToList();
            symbols.Add(MakeSymbol(mixin.Name.Identifier, SymbolKind.Interface, ToRange(mixin),
                mixin.Comments, children));
        }

        foreach (var svc in file.serviceSyntaxes)
        {
            var children = svc.Methods.Select(m => MakeSymbol(
                m.methodName.Identifier, SymbolKind.Method, ToRange(m), m.Comments)).ToList();
            symbols.Add(MakeSymbol(svc.serviceName.Identifier, SymbolKind.Interface, ToRange(svc), svc.Comments, children));
        }

        foreach (var en in file.enumSyntaxes)
        {
            var children = en.Entries.Select(e => MakeSymbol(
                e.Name.Identifier, SymbolKind.EnumMember, ToRange(e), e.Comments)).ToList();
            symbols.Add(MakeSymbol(en.Name.Identifier, SymbolKind.Enum, ToRange(en), en.Comments, children));
        }

        foreach (var fl in file.flagsSyntaxes)
        {
            var children = fl.Entries.Select(e => MakeSymbol(
                e.Name.Identifier, SymbolKind.EnumMember, ToRange(e), e.Comments)).ToList();
            symbols.Add(MakeSymbol(fl.Name.Identifier, SymbolKind.Enum, ToRange(fl), fl.Comments, children));
        }

        foreach (var un in file.unionSyntaxes)
        {
            var children = un.cases.Select(c => MakeSymbol(
                c.caseName.Name.Identifier, SymbolKind.EnumMember, ToRange(c), c.Comments)).ToList();
            symbols.Add(MakeSymbol(un.unionName.Identifier, SymbolKind.Class, ToRange(un), un.Comments, children));
        }

        foreach (var td in file.typedefSyntaxes)
            symbols.Add(MakeSymbol(td.TypeName.Name.Identifier, SymbolKind.TypeParameter, ToRange(td), td.Comments));

        foreach (var attr in file.attributeDefSyntaxes)
            symbols.Add(MakeSymbol(attr.Name.Identifier, SymbolKind.Property, ToRange(attr), attr.Comments));

        return Task.FromResult(new SymbolInformationOrDocumentSymbolContainer(symbols));
    }

    /// <summary>
    /// <c>DocumentSymbol.Detail</c> is plain text, so the doc comment is flattened to a
    /// single line. A symbol without a doc keeps <c>Detail</c> unset, exactly as before.
    /// </summary>
    private static DocumentSymbol MakeSymbol(
        string name, SymbolKind kind, Range range, string? doc = null,
        List<DocumentSymbol>? children = null)
    {
        return new DocumentSymbol
        {
            Name = name,
            Kind = kind,
            Detail = IonDocMarkdown.ToSingleLine(doc, 80),
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
