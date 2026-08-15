namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Collections.Immutable;
using ion.runtime;
using ion.syntax;

public class IonSemanticTokensHandler(IonWorkspace workspace) : SemanticTokensFullHandlerBase
{
    // Token types must match the legend order
    private static readonly string[] TokenTypes =
    [
        "type",           // 0 - user-defined type names
        "keyword",        // 1 - msg, service, enum, etc.
        "function",       // 2 - method names
        "property",       // 3 - field names
        "variable",       // 4 - arguments
        "enumMember",     // 5 - enum/flags members
        "comment",        // 6
        "string",         // 7
        "number",         // 8
        "struct",         // 9 - builtin types
        "interface",      // 10 - service names
        "enum",           // 11 - enum type names
        "typeParameter",  // 12 - generic type params
        "decorator",      // 13 - attributes
        "namespace",      // 14 - #use paths
        "macro",          // 15 - #feature, #use directives
        "parameter",      // 16 - attribute parameters and named attribute arguments
    ];

    // Named so the attribute-argument emitters below read as classification rather than as
    // arithmetic. Appended, never renumbered: the legend index is the wire format, and a client
    // that cached the old legend would recolour the whole file if these moved.
    private const int TokenTypeType = 0;
    private const int TokenTypeKeyword = 1;
    private const int TokenTypeEnumMember = 5;
    private const int TokenTypeString = 7;
    private const int TokenTypeNumber = 8;
    private const int TokenTypeInterface = 10;
    private const int TokenTypeDecorator = 13;
    private const int TokenTypeParameter = 16;

    private static readonly string[] TokenModifiers =
    [
        "declaration",    // 0
        "definition",     // 1
        "deprecated",     // 2
        "readonly",       // 3
        "documentation",  // 4 - `///`, `//!` and `/** */` comments
    ];

    private const int TokenTypeComment = 6;
    private const int ModifierDocumentation = 1 << 4;

    public static SemanticTokensLegend Legend => new()
    {
        TokenTypes = new Container<SemanticTokenType>(TokenTypes.Select(t => new SemanticTokenType(t))),
        TokenModifiers = new Container<SemanticTokenModifier>(TokenModifiers.Select(m => new SemanticTokenModifier(m)))
    };

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
        SemanticTokensCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SemanticTokensRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion"),
            Legend = Legend,
            Full = true
        };
    }

    public override Task<SemanticTokens?> Handle(SemanticTokensParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var file = workspace.ParsedFiles
            .FirstOrDefault(f => workspace.GetFileUri(f).Equals(uri, StringComparison.OrdinalIgnoreCase));

        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);

        if (file is null && content is null)
            return Task.FromResult<SemanticTokens?>(null);

        var builder = new SemanticTokensBuilder();

        // Comments. Scanned from the raw text rather than the syntax tree so that they are
        // still highlighted while the file does not parse, and so that a `//` or `/*` that
        // occurs inside a string literal is never mistaken for a comment.
        if (content is not null)
            EmitComments(builder, content);

        if (file is null)
            return Task.FromResult<SemanticTokens?>(new SemanticTokens { Data = builder.Build() });

        // Workspace-wide, not file-local: a `with` clause may name a mixin declared in any file of
        // the project, exactly as a field may reference a type from any file.
        var declaredMixins = IonMixinLsp.Declarations(workspace).Keys.ToHashSet(StringComparer.Ordinal);

        // Directives
        foreach (var use in file.useSyntaxes)
            EmitToken(builder, use, 15 /* macro */, use.Path.Length + 6); // #use "..."

        foreach (var feat in file.featureSyntaxes)
            EmitToken(builder, feat, 15 /* macro */, feat.featureName.Length + 12);

        // Attribute definitions
        foreach (var attr in file.attributeDefSyntaxes)
        {
            EmitToken(builder, attr.Name, TokenTypeDecorator, attr.Name.Identifier.Length);

            foreach (var arg in attr.Args)
            {
                EmitToken(builder, arg.argName, TokenTypeParameter, arg.argName.Identifier.Length);
                EmitTypeRef(builder, arg.type);
            }

            // `on field, unionCase` — a closed keyword vocabulary, so it colours as keywords.
            // An unknown word is left uncoloured rather than dressed up as a valid target: the
            // absence of highlighting is the first hint that ION0038 is about to fire.
            foreach (var target in attr.Targets ?? [])
                if (IonAttributeTargets.TryParse(target.Identifier, out _))
                    EmitToken(builder, target, TokenTypeKeyword, target.Identifier.Length);
        }

        // Messages. A hoisted inline type is skipped as a *declaration* — there is no name token
        // in the file to colour, only the `msg { … }` its synthesized name borrowed the span of —
        // but its fields are real source and are classified like any others.
        foreach (var msg in file.messageSyntaxes)
        {
            if (!IonLspHelpers.IsHoistedInlineType(msg))
            {
                EmitToken(builder, msg.Name, TokenTypeType, msg.Name.Identifier.Length, 0b11 /* declaration|definition */);
                    EmitWithClause(builder, msg.Mixins, declaredMixins);
            }

            foreach (var field in msg.Fields)
            {
                EmitToken(builder, field.Name, 3 /* property */, field.Name.Identifier.Length);
                EmitTypeRef(builder, field.Type);
            }
        }

        // Mixins. Previously invisible to the tokenizer altogether: a whole declaration form went
        // out uncoloured, name, `with` clause and fields alike.
        foreach (var mixin in file.mixinSyntaxes)
        {
            // `interface`, not `type`: a mixin is not a type, and colouring it as one is the
            // misconception ION0066 exists to correct. It shares the token type with `service`,
            // the other declaration that is a contract rather than a value.
            EmitToken(builder, mixin.Name, TokenTypeInterface, mixin.Name.Identifier.Length, 0b11);
            EmitWithClause(builder, mixin.Mixins, declaredMixins);

            foreach (var field in mixin.Fields)
            {
                EmitToken(builder, field.Name, 3 /* property */, field.Name.Identifier.Length);
                EmitTypeRef(builder, field.Type);
            }
        }

        // Enums
        foreach (var en in file.enumSyntaxes)
        {
            EmitToken(builder, en.Name, 11 /* enum */, en.Name.Identifier.Length, 0b11);
            EmitTypeRef(builder, en.Type);
            foreach (var entry in en.Entries)
                EmitToken(builder, entry.Name, 5 /* enumMember */, entry.Name.Identifier.Length);
        }

        // Flags
        foreach (var fl in file.flagsSyntaxes)
        {
            EmitToken(builder, fl.Name, 11 /* enum */, fl.Name.Identifier.Length, 0b11);
            EmitTypeRef(builder, fl.Type);
            foreach (var entry in fl.Entries)
                EmitToken(builder, entry.Name, 5 /* enumMember */, entry.Name.Identifier.Length);
        }

        // Services
        foreach (var svc in file.serviceSyntaxes)
        {
            EmitToken(builder, svc.serviceName, 10 /* interface */, svc.serviceName.Identifier.Length, 0b11);

            foreach (var arg in svc.BaseArguments)
            {
                EmitToken(builder, arg.argName, 4 /* variable */, arg.argName.Identifier.Length);
                EmitTypeRef(builder, arg.type);
            }

            foreach (var method in svc.Methods)
            {
                EmitToken(builder, method.methodName, 2 /* function */, method.methodName.Identifier.Length, 0b11);
                foreach (var arg in method.arguments)
                {
                    EmitToken(builder, arg.argName, 4 /* variable */, arg.argName.Identifier.Length);
                    EmitTypeRef(builder, arg.type);
                }
                if (method.returnType is not null)
                    EmitTypeRef(builder, method.returnType);
            }
        }

        // Unions
        foreach (var un in file.unionSyntaxes)
        {
            EmitToken(builder, un.unionName, 0 /* type */, un.unionName.Identifier.Length, 0b11);

            foreach (var arg in un.baseFields)
            {
                EmitToken(builder, arg.argName, 4 /* variable */, arg.argName.Identifier.Length);
                EmitTypeRef(builder, arg.type);
            }

            foreach (var c in un.cases)
            {
                EmitTypeRef(builder, c.caseName);
                foreach (var arg in c.arguments)
                {
                    EmitToken(builder, arg.argName, 4 /* variable */, arg.argName.Identifier.Length);
                    EmitTypeRef(builder, arg.type);
                }
            }
        }

        // Typedefs. The name on the left of `=` is a *declaration*, not a reference — emitting it
        // through EmitTypeRef would colour it like a use site (and mis-classify a typedef whose
        // name happens to collide with a builtin as `struct`).
        foreach (var td in file.typedefSyntaxes)
        {
            EmitToken(builder, td.TypeName.Name, 0 /* type */,
                td.TypeName.Name.Identifier.Length, 0b11 /* declaration|definition */);
            if (td.BaseType is not null)
                EmitTypeRef(builder, td.BaseType);
        }

        // Attribute usages, everywhere one can be written. `file.Definitions` only reaches the
        // top level declarations, so an attribute on a field, an enum member, a method or a union
        // case used to be the one identifier in the file with no token at all — the `@` name went
        // uncoloured and the arguments were a single undifferentiated run of plain text.
        foreach (var site in IonAttributeLsp.Sites(file))
        {
            var attr = site.Attribute;
            EmitToken(builder, attr.Name, TokenTypeDecorator, attr.Name.Identifier.Length);

            foreach (var argument in attr.Args)
            {
                if (argument.Name is { } name)
                    EmitToken(builder, name, TokenTypeParameter, name.Identifier.Length);

                EmitLiteral(builder, argument.Value);
            }
        }

        return Task.FromResult<SemanticTokens?>(new SemanticTokens
        {
            Data = builder.Build()
        });
    }

    /// <summary>
    /// Classifies one attribute argument value. Every literal node carries its own start and end,
    /// so the span is the written text — which matters most for a string, whose decoded
    /// <c>Value</c> is not the same length as its source (escapes, quotes).
    /// </summary>
    private static void EmitLiteral(SemanticTokensBuilder builder, IonLiteralSyntax literal)
    {
        switch (literal)
        {
            case IonIntegerLiteralSyntax or IonFloatLiteralSyntax:
                EmitSpan(builder, literal, TokenTypeNumber);
                break;

            case IonStringLiteralSyntax:
                EmitSpan(builder, literal, TokenTypeString);
                break;

            // `true` / `false` / `null` are keywords in the literal grammar, terminated by a word
            // boundary, so `trueish` is an identifier and is deliberately not coloured here.
            case IonBoolLiteralSyntax or IonNullLiteralSyntax:
                EmitSpan(builder, literal, TokenTypeKeyword);
                break;

            case IonEnumRefLiteralSyntax enumRef:
                EmitToken(builder, enumRef.TypeName, TokenTypeType, enumRef.TypeName.Identifier.Length);
                EmitToken(builder, enumRef.Member, TokenTypeEnumMember, enumRef.Member.Identifier.Length);
                break;

            // The brackets themselves get no token; only the elements are classified, so a nested
            // array colours element by element rather than as one blue run.
            case IonArrayLiteralSyntax array:
                foreach (var item in array.Items)
                    EmitLiteral(builder, item);
                break;
        }
    }

    /// <summary>
    /// Emits a token covering a node's own start/end span. Multi-line nodes are skipped: the LSP
    /// token encoding cannot express one, and the only literal that can span lines is an array,
    /// whose elements are emitted individually anyway.
    /// </summary>
    private static void EmitSpan(SemanticTokensBuilder builder, IonSyntaxBase node, int tokenType)
    {
        var start = node.StartPosition;

        if (start.Line <= 0 || start.Col <= 0 || node.EndPosition is not { } end)
            return;

        if (end.Line != start.Line || end.Col <= start.Col)
            return;

        builder.Push(start.Line - 1, start.Col - 1, end.Col - start.Col, tokenType, 0);
    }

    // The builtin list lives on IonLspHelpers now. Two hand-maintained copies had already drifted
    // apart from the std module and from each other — this one was missing `decimal`, `Map` and
    // `Set`, so all three coloured as user-defined types.

    /// <summary>
    /// Emits one `comment` token per line covered by each comment. Doc comments
    /// (<c>///</c>, <c>//!</c>, <c>/** */</c>) additionally carry the `documentation` modifier,
    /// so a theme can colour API docs differently from ordinary commentary.
    /// </summary>
    private static void EmitComments(SemanticTokensBuilder builder, string content)
    {
        var scanned = IonCommentScanner.Scan(content);

        foreach (var comment in scanned.Comments)
        {
            var modifiers = comment.IsDoc ? ModifierDocumentation : 0;

            for (var line = comment.StartLine; line <= comment.EndLine; line++)
            {
                if (line >= scanned.Lines.Length) break;

                var lineEnd = scanned.VisualLength(line);
                var start = line == comment.StartLine ? comment.StartChar : 0;
                var end = line == comment.EndLine ? Math.Min(comment.EndChar, lineEnd) : lineEnd;

                if (end <= start) continue;

                builder.Push(line, start, end - start, TokenTypeComment, modifiers);
            }
        }
    }

    /// <summary>
    /// The names in a <c>with</c> clause.
    /// </summary>
    /// <remarks>
    /// Coloured as <c>interface</c>, the same as the mixin declarations they point at, so a clause
    /// entry visibly is not a type reference. An entry that names nothing declared is left
    /// uncoloured rather than dressed up as valid — the missing highlight is the first hint that
    /// ION0063 is coming, exactly as it is for an unknown attribute target.
    /// </remarks>
    private static void EmitWithClause(
        SemanticTokensBuilder builder, List<IonIdentifier>? clause, HashSet<string> declared)
    {
        foreach (var entry in clause ?? [])
            if (declared.Contains(entry.Identifier))
                EmitToken(builder, entry, TokenTypeInterface, entry.Identifier.Length);
    }

    /// <summary>
    /// Classifies a written type reference and everything nested inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recursion is what makes a nested argument visible at all. This used to emit one
    /// <c>typeParameter</c> token per argument, positioned at the argument's <em>head</em> name,
    /// so in <c>Map&lt;string, Array&lt;Document&gt;&gt;</c> the <c>Array</c> coloured and the
    /// <c>Document</c> inside it got no token — the one identifier in the line that names a
    /// user-defined type was the one left uncoloured.
    /// </para>
    /// <para>
    /// An argument is now classified by what it <em>is</em> — builtin, or user-defined — rather
    /// than as <c>typeParameter</c>, which is the LSP type for a declaration's own <c>T</c> and
    /// not for an argument at a use site. Ion has no generic declarations, so nothing else can
    /// claim that token type; if 2.3 lands, the parameter list is where it belongs.
    /// </para>
    /// <para>
    /// A hoisted inline type is skipped. Its synthesized name token carries the span of the whole
    /// <c>msg { … }</c> body but the length of the derived identifier, so emitting it painted an
    /// arbitrary 13-character run starting at the <c>msg</c> keyword — <c>'msg { address'</c> —
    /// twice over. The body's own fields are classified when the hoisted message is walked.
    /// </para>
    /// </remarks>
    private static void EmitTypeRef(SemanticTokensBuilder builder, IonUnderlyingTypeSyntax type)
    {
        if (type.IsInline || IonLspHelpers.IsSynthesizedSpan(type.Name))
            return;

        var tokenType = IonLspHelpers.IsBuiltinTypeName(type.Name.Identifier)
            ? 9  // struct (builtin)
            : 0; // type (user-defined)
        EmitToken(builder, type.Name, tokenType, type.Name.Identifier.Length);

        foreach (var gen in type.generics)
        {
            if (gen.Type is { } written)
                EmitTypeRef(builder, written);
            else
                EmitToken(builder, gen.Name, TokenTypeType, gen.Name.Identifier.Length);
        }
    }

    private static void EmitToken(SemanticTokensBuilder builder, IonSyntaxBase node, int tokenType, int length, int modifiers = 0)
    {
        if (node.StartPosition.Line <= 0 || node.StartPosition.Col <= 0)
            return;

        var line = node.StartPosition.Line - 1;
        var col = node.StartPosition.Col - 1;

        builder.Push(line, col, length, tokenType, modifiers);
    }

    /// <summary>
    /// Builds sorted, delta-encoded semantic token data.
    /// </summary>
    private class SemanticTokensBuilder
    {
        private readonly List<(int line, int col, int length, int tokenType, int modifiers)> _tokens = [];

        public void Push(int line, int col, int length, int tokenType, int modifiers)
        {
            _tokens.Add((line, col, length, tokenType, modifiers));
        }

        public ImmutableArray<int> Build()
        {
            // Sort by line then column
            _tokens.Sort((a, b) =>
            {
                var lineCmp = a.line.CompareTo(b.line);
                return lineCmp != 0 ? lineCmp : a.col.CompareTo(b.col);
            });

            var data = new List<int>();
            var prevLine = 0;
            var prevCol = 0;

            foreach (var (line, col, length, tokenType, modifiers) in _tokens)
            {
                var deltaLine = line - prevLine;
                var deltaCol = deltaLine == 0 ? col - prevCol : col;

                data.Add(deltaLine);
                data.Add(deltaCol);
                data.Add(length);
                data.Add(tokenType);
                data.Add(modifiers);

                prevLine = line;
                prevCol = col;
            }

            return [..data];
        }
    }
}
