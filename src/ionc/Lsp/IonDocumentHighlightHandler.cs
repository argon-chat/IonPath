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
            // A hoisted inline type's name token spans the whole `msg { … }` body, so highlighting
            // it would light up the entire declaration rather than an identifier.
            if (!IonLspHelpers.IsHoistedInlineType(msg)
                && msg.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                highlights.Add(MakeHighlight(msg.Name, DocumentHighlightKind.Write));

            CollectMixinHighlights(msg.Mixins, word, highlights);

            foreach (var field in msg.Fields)
            {
                if (field.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    highlights.Add(MakeHighlight(field.Name, DocumentHighlightKind.Write));
                CollectTypeHighlights(field.Type, word, highlights);
            }
        }

        foreach (var mixin in file.mixinSyntaxes)
        {
            if (mixin.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                highlights.Add(MakeHighlight(mixin.Name, DocumentHighlightKind.Write));

            CollectMixinHighlights(mixin.Mixins, word, highlights);

            foreach (var field in mixin.Fields)
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

    /// <summary>
    /// Every occurrence of <paramref name="word"/> inside one type reference, at any depth.
    /// </summary>
    /// <remarks>
    /// Recurses through <see cref="IonTypeParameterSyntax.Type"/>. Reading each argument's head
    /// <c>Name</c> instead found the <c>Array</c> of <c>Map&lt;string, Array&lt;User&gt;&gt;</c>
    /// and never the <c>User</c>, so putting the cursor on a nested type highlighted the
    /// declaration but not the use.
    /// </remarks>
    private static void CollectTypeHighlights(
        IonUnderlyingTypeSyntax type, string word, List<DocumentHighlight> highlights)
    {
        // A rewritten inline reference carries the span of the whole body it was hoisted from.
        if (type.IsInline || IonLspHelpers.IsSynthesizedSpan(type.Name))
            return;

        if (type.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
            highlights.Add(MakeHighlight(type.Name, DocumentHighlightKind.Read));

        foreach (var generic in type.generics)
        {
            if (generic.Type is { } written)
                CollectTypeHighlights(written, word, highlights);
            else if (generic.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                highlights.Add(MakeHighlight(generic.Name, DocumentHighlightKind.Read));
        }
    }

    /// <summary>Occurrences of a name in a <c>with</c> clause.</summary>
    private static void CollectMixinHighlights(
        List<IonIdentifier>? clause, string word, List<DocumentHighlight> highlights)
    {
        foreach (var entry in clause ?? [])
            if (entry.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                highlights.Add(MakeHighlight(entry, DocumentHighlightKind.Read));
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
