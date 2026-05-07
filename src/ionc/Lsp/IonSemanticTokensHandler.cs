namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Collections.Immutable;
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
    ];

    private static readonly string[] TokenModifiers =
    [
        "declaration",    // 0
        "definition",     // 1
        "deprecated",     // 2
        "readonly",       // 3
    ];

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

        if (file is null)
            return Task.FromResult<SemanticTokens?>(null);

        var builder = new SemanticTokensBuilder();

        // Directives
        foreach (var use in file.useSyntaxes)
            EmitToken(builder, use, 15 /* macro */, use.Path.Length + 6); // #use "..."

        foreach (var feat in file.featureSyntaxes)
            EmitToken(builder, feat, 15 /* macro */, feat.featureName.Length + 12);

        // Attribute definitions
        foreach (var attr in file.attributeDefSyntaxes)
        {
            EmitToken(builder, attr.Name, 13 /* decorator */, attr.Name.Identifier.Length);
            foreach (var arg in attr.Args)
            {
                EmitToken(builder, arg.argName, 4 /* variable */, arg.argName.Identifier.Length);
                EmitTypeRef(builder, arg.type);
            }
        }

        // Messages
        foreach (var msg in file.messageSyntaxes)
        {
            EmitToken(builder, msg.Name, 0 /* type */, msg.Name.Identifier.Length, 0b11 /* declaration|definition */);
            foreach (var field in msg.Fields)
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

        // Typedefs
        foreach (var td in file.typedefSyntaxes)
        {
            EmitTypeRef(builder, td.TypeName);
            if (td.BaseType is not null)
                EmitTypeRef(builder, td.BaseType);
        }

        // Attribute usages on definitions
        foreach (var def in file.Definitions)
        {
            foreach (var attr in def.Attributes)
                EmitToken(builder, attr.Name, 13 /* decorator */, attr.Name.Identifier.Length);
        }

        return Task.FromResult<SemanticTokens?>(new SemanticTokens
        {
            Data = builder.Build()
        });
    }

    // Builtin type names for coloring
    private static readonly HashSet<string> BuiltinTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "i1", "i2", "i4", "i8", "i16",
        "u1", "u2", "u4", "u8", "u16",
        "f2", "f4", "f8",
        "bool", "void", "string", "bytes", "guid",
        "datetime", "dateonly", "timeonly", "duration", "uri", "bigint",
        "Maybe", "Array", "Partial",
        "vec2f", "vec3f", "vec4f", "vec2d", "vec3d", "vec4d", "vec2h", "vec3h", "vec4h"
    };

    private static void EmitTypeRef(SemanticTokensBuilder builder, IonUnderlyingTypeSyntax type)
    {
        var tokenType = BuiltinTypeNames.Contains(type.Name.Identifier)
            ? 9  // struct (builtin)
            : 0; // type (user-defined)
        EmitToken(builder, type.Name, tokenType, type.Name.Identifier.Length);

        foreach (var gen in type.generics)
            EmitToken(builder, gen.Name, 12 /* typeParameter */, gen.Name.Identifier.Length);
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
