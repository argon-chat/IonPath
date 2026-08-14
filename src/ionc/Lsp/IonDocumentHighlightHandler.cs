namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol;
using ion.syntax;

public class IonDocumentHighlightHandler(IonWorkspace workspace) : DocumentHighlightHandlerBase
{
    protected override DocumentHighlightRegistrationOptions CreateRegistrationOptions(
        DocumentHighlightCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentHighlightRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion")
        };
    }

    public override Task<DocumentHighlightContainer?> Handle(DocumentHighlightParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);

        if (content is null)
            return Task.FromResult<DocumentHighlightContainer?>(null);

        // Do not light up a symbol just because its name is mentioned in a comment or a
        // string literal — that position is prose, not a reference.
        if (IonLspHelpers.IsInCommentOrString(content, (int)request.Position.Line, (int)request.Position.Character))
            return Task.FromResult<DocumentHighlightContainer?>(null);

        var word = IonLspHelpers.GetWordAtPosition(content, (int)request.Position.Line, (int)request.Position.Character);
        if (string.IsNullOrEmpty(word))
            return Task.FromResult<DocumentHighlightContainer?>(null);

        var highlights = new List<DocumentHighlight>();

        // Find all occurrences of the word in the same file
        var file = workspace.ParsedFiles
            .FirstOrDefault(f => workspace.GetFileUri(f).Equals(uri, StringComparison.OrdinalIgnoreCase));

        if (file is null)
            return Task.FromResult<DocumentHighlightContainer?>(null);

        // Definitions (Write highlights)
        foreach (var msg in file.messageSyntaxes)
        {
            if (msg.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                highlights.Add(MakeHighlight(msg.Name, DocumentHighlightKind.Write));

            foreach (var field in msg.Fields)
            {
                if (field.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    highlights.Add(MakeHighlight(field.Name, DocumentHighlightKind.Write));
                CollectTypeHighlights(field.Type, word, highlights);
            }
        }

        foreach (var svc in file.serviceSyntaxes)
        {
            if (svc.serviceName.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                highlights.Add(MakeHighlight(svc.serviceName, DocumentHighlightKind.Write));

            foreach (var arg in svc.BaseArguments)
            {
                if (arg.argName.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    highlights.Add(MakeHighlight(arg.argName, DocumentHighlightKind.Write));
                CollectTypeHighlights(arg.type, word, highlights);
            }

            foreach (var method in svc.Methods)
            {
                if (method.methodName.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    highlights.Add(MakeHighlight(method.methodName, DocumentHighlightKind.Write));

                foreach (var arg in method.arguments)
                {
                    if (arg.argName.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                        highlights.Add(MakeHighlight(arg.argName, DocumentHighlightKind.Write));
                    CollectTypeHighlights(arg.type, word, highlights);
                }

                if (method.returnType is not null)
                    CollectTypeHighlights(method.returnType, word, highlights);
            }
        }

        foreach (var en in file.enumSyntaxes)
        {
            if (en.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                highlights.Add(MakeHighlight(en.Name, DocumentHighlightKind.Write));
            CollectTypeHighlights(en.Type, word, highlights);
            foreach (var entry in en.Entries)
                if (entry.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    highlights.Add(MakeHighlight(entry.Name, DocumentHighlightKind.Write));
        }

        foreach (var fl in file.flagsSyntaxes)
        {
            if (fl.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                highlights.Add(MakeHighlight(fl.Name, DocumentHighlightKind.Write));
            CollectTypeHighlights(fl.Type, word, highlights);
            foreach (var entry in fl.Entries)
                if (entry.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    highlights.Add(MakeHighlight(entry.Name, DocumentHighlightKind.Write));
        }

        foreach (var un in file.unionSyntaxes)
        {
            if (un.unionName.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                highlights.Add(MakeHighlight(un.unionName, DocumentHighlightKind.Write));

            foreach (var arg in un.baseFields)
            {
                if (arg.argName.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    highlights.Add(MakeHighlight(arg.argName, DocumentHighlightKind.Write));
                CollectTypeHighlights(arg.type, word, highlights);
            }

            foreach (var c in un.cases)
            {
                CollectTypeHighlights(c.caseName, word, highlights);
                foreach (var arg in c.arguments)
                {
                    if (arg.argName.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                        highlights.Add(MakeHighlight(arg.argName, DocumentHighlightKind.Write));
                    CollectTypeHighlights(arg.type, word, highlights);
                }
            }
        }

        foreach (var td in file.typedefSyntaxes)
        {
            if (td.TypeName.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                highlights.Add(MakeHighlight(td.TypeName.Name, DocumentHighlightKind.Write));
            if (td.BaseType is not null)
                CollectTypeHighlights(td.BaseType, word, highlights);
        }

        foreach (var attr in file.attributeDefSyntaxes)
        {
            if (attr.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                highlights.Add(MakeHighlight(attr.Name, DocumentHighlightKind.Write));
        }

        // Attribute usages
        foreach (var def in file.Definitions)
        {
            foreach (var attrUsage in def.Attributes)
            {
                if (attrUsage.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    highlights.Add(MakeHighlight(attrUsage.Name, DocumentHighlightKind.Read));
            }
        }

        if (highlights.Count == 0)
            return Task.FromResult<DocumentHighlightContainer?>(null);

        return Task.FromResult<DocumentHighlightContainer?>(new DocumentHighlightContainer(highlights));
    }

    private static void CollectTypeHighlights(
        IonUnderlyingTypeSyntax type, string word, List<DocumentHighlight> highlights)
    {
        if (type.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
            highlights.Add(MakeHighlight(type.Name, DocumentHighlightKind.Read));

        foreach (var generic in type.generics)
        {
            if (generic.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                highlights.Add(MakeHighlight(generic.Name, DocumentHighlightKind.Read));
        }
    }

    private static DocumentHighlight MakeHighlight(IonSyntaxBase node, DocumentHighlightKind kind)
    {
        return new DocumentHighlight
        {
            Range = IonLspHelpers.ToLspRange(node),
            Kind = kind
        };
    }
}
