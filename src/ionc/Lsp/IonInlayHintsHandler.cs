namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ion.runtime;
using ion.syntax;

public class IonInlayHintsHandler(IonWorkspace workspace) : InlayHintsHandlerBase
{
    /// <summary>
    /// Builtins with one fixed wire width. Anything absent — including everything variable-length
    /// — yields a "variable" hint.
    /// </summary>
    /// <remarks>
    /// <c>datetime</c> used to be listed at 64 bits. It is now CBOR tag 0 over RFC 3339 text, so
    /// it has no fixed width in the sense this table means, and quoting one made every message
    /// carrying a timestamp report a total size 8 bytes smaller than it can possibly encode.
    /// <c>decimal</c> is tag 4 over a variable-length mantissa and is deliberately absent for the
    /// same reason.
    /// </remarks>
    private static readonly Dictionary<string, int> BuiltinBits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["i1"] = 8, ["i2"] = 16, ["i4"] = 32, ["i8"] = 64, ["i16"] = 128,
        ["u1"] = 8, ["u2"] = 16, ["u4"] = 32, ["u8"] = 64, ["u16"] = 128,
        ["f2"] = 16, ["f4"] = 32, ["f8"] = 64,
        ["bool"] = 8, ["void"] = 0, ["guid"] = 128,
        ["dateonly"] = 32, ["timeonly"] = 64, ["duration"] = 64,
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

        var hoisted = IonLspHelpers.HoistedTypeNames(file);

        // Field type sizes in messages
        foreach (var msg in file.messageSyntaxes)
        {
            // A hoisted inline type is not a declaration anyone wrote; its "opening line" is the
            // line of the field it was written on, so a size hint for it would land as a second,
            // contradictory annotation on a line that already has the field's own.
            var synthesized = IonLspHelpers.IsHoistedInlineType(msg);

            foreach (var field in msg.Fields)
            {
                var line = Math.Max(0, field.StartPosition.Line - 1);
                var bits = GetFieldBits(field.Type, allDefs);

                if (bits is not null && bits > 0)
                {
                    var label = bits >= 8 ? $"{bits / 8} bytes" : $"{bits} bits";
                    hints.Add(MakeEolHint(lines, line, label));
                }
                else if (bits is null)
                {
                    hints.Add(MakeEolHint(lines, line, "variable"));
                }

                AddHoistedNameHint(hints, field, hoisted);
            }

            if (synthesized)
                continue;

            // Message total size on the opening line
            var totalBits = ComputeMessageBits(msg, allDefs);
            if (totalBits is not null && totalBits > 0)
            {
                var line = Math.Max(0, msg.StartPosition.Line - 1);
                hints.Add(MakeEolHint(lines, line, $"{totalBits / 8} bytes ({totalBits} bits)"));
            }
        }

        // Mixin fields get the same size annotation. A mixin has no total of its own — it is not a
        // message and never encodes alone — so there is no declaration-line hint.
        foreach (var mixin in file.mixinSyntaxes)
            foreach (var field in mixin.Fields)
            {
                var line = Math.Max(0, field.StartPosition.Line - 1);
                var bits = GetFieldBits(field.Type, allDefs);

                hints.Add(MakeEolHint(lines, line,
                    bits is > 0 ? bits >= 8 ? $"{bits / 8} bytes" : $"{bits} bits" : "variable"));

                AddHoistedNameHint(hints, field, hoisted);
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

        AddAttributeArgumentHints(file, uri, hints);

        // An inline type shares its line with the field it was written on —
        // `shipping: msg { address: string; };` is one line holding two fields — so both the
        // outer field and the inner one place an end-of-line size hint at the same column, and
        // the reader sees `// variable  // variable`. Identical hints at one position are always
        // redundant, whatever produced them.
        var deduped = hints
            .GroupBy(h => (h.Position.Line, h.Position.Character, h.Label.String))
            .Select(g => g.First())
            .ToList();

        return Task.FromResult<InlayHintContainer?>(new InlayHintContainer(deduped));
    }

    /// <summary>
    /// Shows the name an inline anonymous type was hoisted to, right where it was written:
    /// <c>shipping: msg</c> <i>OrderShipping</i> <c>{ address: string; };</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the hint with the strongest case in the language. The derived name
    /// <c>{Owner}{PascalCasedFieldName}</c> is a real, load-bearing identifier — it is what goes
    /// into <c>ion.lock.json</c>, what three generators emit a declaration for, and what every
    /// diagnostic about the type calls it — and there is no token anywhere in the source that
    /// spells it. Without the hint the only way to learn it is to compile and read the output.
    /// </para>
    /// <para>
    /// Placed at the type position rather than at the end of the line so it reads as part of the
    /// type, and so it does not compete with the size hint that already sits at end-of-line.
    /// </para>
    /// </remarks>
    private static void AddHoistedNameHint(
        List<InlayHint> hints, IonFieldSyntax field, HashSet<string> hoisted)
    {
        var name = field.Type.Name.Identifier;

        if (!hoisted.Contains(name))
            return;

        // The rewritten reference kept the inline body's span, so this is the `msg` keyword the
        // author wrote. Anchor just past it.
        var start = field.Type.Name.StartPosition;

        if (start.Line <= 0 || start.Col <= 0)
            return;

        hints.Add(new InlayHint
        {
            Position = new Position(start.Line - 1, start.Col - 1 + "msg".Length),
            Label = new StringOrInlayHintLabelParts($" {name}"),
            Kind = InlayHintKind.Type,
            Tooltip = new StringOrMarkupContent(
                $"Inline anonymous type, hoisted to `{name}` — `{{Owner}}{{PascalCasedFieldName}}`. "
                + "This is the name recorded in `ion.lock.json` and emitted by every generator. "
                + "A collision with an explicit declaration is ION0067."),
            PaddingLeft = true
        });
    }

    /// <summary>
    /// Names the positional arguments of every attribute use: <c>@Cache(</c><i>duration:</i>
    /// <c>300, </c><i>key:</i><c> "user")</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the position in the language with the weakest local context. A method argument is
    /// read next to a signature that is usually in the same file and often on screen; an attribute
    /// use is a bare literal list, and the declaration that gives those literals meaning is
    /// typically in another file or is a builtin with no source at all.
    /// </para>
    /// <para>
    /// Only positional arguments are annotated — a written <c>key: "user"</c> already says it — and
    /// only up to the declared parameter count, so a surplus argument (ION0032) is left unlabelled
    /// rather than being given the name of a parameter it does not bind to.
    /// </para>
    /// </remarks>
    private void AddAttributeArgumentHints(IonFileSyntax file, string uri, List<InlayHint> hints)
    {
        // Resolved once per request, not once per use: this handler runs on every viewport change,
        // and IonAttributeLsp.Find rebuilds the whole visible-attribute list on each call.
        var declarations = IonAttributeLsp.Declarations(workspace, uri)
            .ToDictionary(d => d.Name, StringComparer.Ordinal);

        foreach (var site in IonAttributeLsp.Sites(file))
        {
            var use = site.Attribute;

            if (use.Args.Count == 0)
                continue;

            if (!declarations.TryGetValue(use.Name.Identifier, out var declaration)
                || declaration.Parameters.Count == 0)
                continue;

            var slot = 0;

            foreach (var argument in use.Args)
            {
                if (argument.Name is not null)
                    continue;

                if (slot >= declaration.Parameters.Count)
                    break;

                var parameter = declaration.Parameters[slot++];
                var start = argument.Value.StartPosition;

                if (start.Line <= 0 || start.Col <= 0)
                    continue;

                hints.Add(new InlayHint
                {
                    Position = IonLspHelpers.ToLspPosition(start),
                    Label = new StringOrInlayHintLabelParts($"{parameter.Name}:"),
                    Kind = InlayHintKind.Parameter,
                    Tooltip = new StringOrMarkupContent(
                        $"`{parameter.Name}: {parameter.Type}` of `@{declaration.Name}`"),
                    PaddingRight = true
                });
            }
        }
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

        // A written modifier suffix has to be tested before the name is even looked at. `f4[16]`
        // and `i4[]` have `Name == "f4"` / `"i4"`, so a name-only test reported the *element*
        // width — every array field claimed to be 4 bytes — and the enclosing message's total was
        // wrong by however many elements the arrays held. `T?` and `T~` are the same hazard:
        // WrapModifiers turns them into a Maybe/Partial that the name does not mention.
        if (type.IsArray || type.IsOptional || type.IsPartial || type.IsInline)
            return null;

        // null => the hint reads "variable". Correct for `Partial<T>`: it encodes as a CBOR map
        // carrying only the fields the sender touched, so its width is unrelated to `sizeof(T)`.
        if (name is "Maybe" or "Array" or "Partial" or "Map" or "Set"
            or "string" or "bytes" or "bigint" or "uri" or "datetime" or "decimal")
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
