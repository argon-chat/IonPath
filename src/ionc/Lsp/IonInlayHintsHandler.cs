namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ion.runtime;
using ion.syntax;

public class IonInlayHintsHandler(IonWorkspace workspace) : InlayHintsHandlerBase
{
    private static readonly Dictionary<string, int> BuiltinBits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["i1"] = 8, ["i2"] = 16, ["i4"] = 32, ["i8"] = 64, ["i16"] = 128,
        ["u1"] = 8, ["u2"] = 16, ["u4"] = 32, ["u8"] = 64, ["u16"] = 128,
        ["f2"] = 16, ["f4"] = 32, ["f8"] = 64,
        ["bool"] = 8, ["void"] = 0, ["guid"] = 128,
        ["datetime"] = 64, ["dateonly"] = 32, ["timeonly"] = 64, ["duration"] = 64,
        ["vec2f"] = 64, ["vec3f"] = 96, ["vec4f"] = 128,
        ["vec2d"] = 128, ["vec3d"] = 192, ["vec4d"] = 256,
        ["vec2h"] = 32, ["vec3h"] = 48, ["vec4h"] = 64,
    };

    protected override InlayHintRegistrationOptions CreateRegistrationOptions(
        InlayHintClientCapabilities capability, ClientCapabilities clientCapabilities)
    {
        return new InlayHintRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion")
        };
    }

    public override Task<InlayHintContainer?> Handle(InlayHintParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var file = workspace.ParsedFiles
            .FirstOrDefault(f => workspace.GetFileUri(f).Equals(uri, StringComparison.OrdinalIgnoreCase));

        if (file is null)
            return Task.FromResult<InlayHintContainer?>(null);

        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);
        if (content is null)
            return Task.FromResult<InlayHintContainer?>(null);

        var lines = IonCommentScanner.Scan(content);
        var hints = new List<InlayHint>();
        var allDefs = GetAllDefs();

        // Field type sizes in messages
        foreach (var msg in file.messageSyntaxes)
        {
            foreach (var field in msg.Fields)
            {
                var bits = GetFieldBits(field.Type, allDefs);
                if (bits is not null && bits > 0)
                {
                    var label = bits >= 8 ? $"{bits / 8} bytes" : $"{bits} bits";
                    var line = Math.Max(0, field.StartPosition.Line - 1);
                    hints.Add(MakeEolHint(lines, line, label));
                }
                else if (bits is null)
                {
                    var line = Math.Max(0, field.StartPosition.Line - 1);
                    hints.Add(MakeEolHint(lines, line, "variable"));
                }
            }

            // Message total size on the opening line
            var totalBits = ComputeMessageBits(msg, allDefs);
            if (totalBits is not null && totalBits > 0)
            {
                var line = Math.Max(0, msg.StartPosition.Line - 1);
                hints.Add(MakeEolHint(lines, line, $"{totalBits / 8} bytes ({totalBits} bits)"));
            }
        }

        // Enum/flags member count
        foreach (var en in file.enumSyntaxes)
        {
            var line = Math.Max(0, en.StartPosition.Line - 1);
            hints.Add(MakeEolHint(lines, line, $"{en.Entries.Count} members"));
        }

        foreach (var fl in file.flagsSyntaxes)
        {
            var line = Math.Max(0, fl.StartPosition.Line - 1);
            hints.Add(MakeEolHint(lines, line, $"{fl.Entries.Count} flags"));
        }

        // Service method count
        foreach (var svc in file.serviceSyntaxes)
        {
            var line = Math.Max(0, svc.StartPosition.Line - 1);
            hints.Add(MakeEolHint(lines, line, $"{svc.Methods.Count} methods"));
        }

        return Task.FromResult<InlayHintContainer?>(new InlayHintContainer(hints));
    }

    public override Task<InlayHint> Handle(InlayHint request, CancellationToken cancellationToken)
        => Task.FromResult(request);

    /// <summary>
    /// Places the hint immediately after the last code character of the line rather than at
    /// the very end, so a trailing comment stays the right-most thing on the line
    /// (<c>id: guid;  8 bytes  // stable identity</c>, not <c>... // stable identity  8 bytes</c>).
    /// </summary>
    private static InlayHint MakeEolHint(IonScannedDocument lines, int line, string label)
    {
        var lastCode = lines.LastCodeChar(line);
        var col = lastCode >= 0 ? lastCode + 1 : lines.VisualLength(line);
        return new InlayHint
        {
            Position = new Position(line, col),
            Label = new StringOrInlayHintLabelParts($"  // {label}"),
            Kind = InlayHintKind.Type,
            PaddingLeft = true
        };
    }

    private IReadOnlyList<IonType> GetAllDefs()
    {
        var ctx = workspace.LastContext;
        if (ctx is null) return [];
        return ctx.ProcessedModules
            .Concat(ctx.GlobalModules)
            .SelectMany(m => m.Definitions)
            .ToList();
    }

    private static int? ComputeMessageBits(IonMessageSyntax msg, IReadOnlyList<IonType> allDefs)
    {
        var total = 0;
        foreach (var field in msg.Fields)
        {
            var bits = GetFieldBits(field.Type, allDefs);
            if (bits is null) return null;
            total += bits.Value;
        }
        return total;
    }

    private static int? GetFieldBits(IonUnderlyingTypeSyntax type, IReadOnlyList<IonType> allDefs)
    {
        var name = type.Name.Identifier;

        if (name is "Maybe" or "Array" or "Partial" or "string" or "bytes" or "bigint" or "uri")
            return null;
        if (type.generics.Count > 0) return null;

        if (BuiltinBits.TryGetValue(name, out var bits))
            return bits;

        // Look up from compiled defs
        var resolved = allDefs.FirstOrDefault(d =>
            d.name.Identifier.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (resolved is null) return null;

        return GetResolvedBits(resolved, new HashSet<string>(StringComparer.OrdinalIgnoreCase), allDefs);
    }

    private static int? GetResolvedBits(IonType type, HashSet<string> visited, IReadOnlyList<IonType> allDefs)
    {
        if (!visited.Add(type.name.Identifier)) return null;

        if (type is IonEnum e)
        {
            if (BuiltinBits.TryGetValue(e.baseType.name.Identifier, out var eb))
                return eb;
            return null;
        }
        if (type is IonFlags f)
        {
            if (BuiltinBits.TryGetValue(f.baseType.name.Identifier, out var fb))
                return fb;
            return null;
        }

        if (type.fields.Count == 0) return null;

        var total = 0;
        foreach (var field in type.fields)
        {
            var name = field.type.name.Identifier;
            if (BuiltinBits.TryGetValue(name, out var bits))
            {
                total += bits;
                continue;
            }
            var nested = allDefs.FirstOrDefault(d =>
                d.name.Identifier.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (nested is null) return null;
            var nestedBits = GetResolvedBits(nested, visited, allDefs);
            if (nestedBits is null) return null;
            total += nestedBits.Value;
        }

        visited.Remove(type.name.Identifier);
        return total;
    }
}
