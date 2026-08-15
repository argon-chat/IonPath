namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ion.runtime;
using ion.syntax;
using Pidgin;

public class IonHoverHandler(IonWorkspace workspace) : HoverHandlerBase
{
    private static readonly Dictionary<string, BuiltinTypeInfo> BuiltinTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Signed integers
        ["i1"]  = new("i1",  "Signed 8-bit integer",   "sbyte",    8,    "-128", "127"),
        ["i2"]  = new("i2",  "Signed 16-bit integer",  "short",    16,   "-32,768", "32,767"),
        ["i4"]  = new("i4",  "Signed 32-bit integer",  "int",      32,   "-2,147,483,648", "2,147,483,647"),
        ["i8"]  = new("i8",  "Signed 64-bit integer",  "long",     64,   "-9.2×10¹⁸", "9.2×10¹⁸"),
        ["i16"] = new("i16", "Signed 128-bit integer", "Int128",   128,  "-1.7×10³⁸", "1.7×10³⁸"),

        // Unsigned integers
        ["u1"]  = new("u1",  "Unsigned 8-bit integer",   "byte",   8,    "0", "255"),
        ["u2"]  = new("u2",  "Unsigned 16-bit integer",  "ushort", 16,   "0", "65,535"),
        ["u4"]  = new("u4",  "Unsigned 32-bit integer",  "uint",   32,   "0", "4,294,967,295"),
        ["u8"]  = new("u8",  "Unsigned 64-bit integer",  "ulong",  64,   "0", "1.8×10¹⁹"),
        ["u16"] = new("u16", "Unsigned 128-bit integer", "UInt128", 128, "0", "3.4×10³⁸"),

        // Floating point
        ["f2"]  = new("f2",  "Half-precision float (IEEE 754)", "Half",   16,  "±6.55×10⁴",  "~3 digits"),
        ["f4"]  = new("f4",  "Single-precision float (IEEE 754)", "float",  32,  "±3.4×10³⁸",  "~7 digits"),
        ["f8"]  = new("f8",  "Double-precision float (IEEE 754)", "double", 64,  "±1.8×10³⁰⁸", "~15 digits"),

        // Special scalars
        ["bool"]     = new("bool",     "Boolean value",            "bool",     8,   "false", "true"),
        ["void"]     = new("void",     "No value / unit type",     "void",     0,   null, null),
        ["bigint"]   = new("bigint",   "Arbitrary-precision integer", "BigInteger", null, "unlimited", "unlimited"),

        // String-like
        ["string"]   = new("string",   "UTF-8 encoded text",       "string",   null, null, null),
        ["bytes"]    = new("bytes",    "Raw byte sequence",        "byte[]",   null, null, null),
        ["guid"]     = new("guid",     "128-bit unique identifier (RFC 4122)", "Guid", 128, null, null),
        ["uri"]      = new("uri",      "Uniform Resource Identifier", "Uri",   null, null, null),

        // Exact base-10 decimal. Deliberately not `f8`: it is the one numeric type whose purpose
        // is that it does not lose precision, and no bit width is quoted because tag 4 carries a
        // variable-length mantissa.
        ["decimal"]  = new("decimal",  "Exact base-10 decimal", "System.Decimal", null, "±7.9×10²⁸", "28–29 significant digits",
            "CBOR **tag 4** wrapping `[exponent, mantissa]`. The mantissa is normalised on write, so `1.50` and "
            + "`1.5` are the same bytes — as are `-0.0m` and `0m`, the one deliberate place where two source "
            + "values become one encoding. TypeScript `IonDecimal`, Rust `ion_rustcore::IonDecimal`."),

        // Date/Time
        // `datetime` is text on the wire, not a 64-bit number — the old "64 bits / DateTime" row
        // was wrong in both columns after the wire format was corrected.
        ["datetime"] = new("datetime", "Instant with an explicit UTC offset", "System.DateTimeOffset", null, null, null,
            "CBOR **tag 0** wrapping RFC 3339 text, always with an explicit numeric offset and always with exactly "
            + "seven fractional digits: `2024-03-01T12:34:56.7891234+05:30` — 33 characters, 36 wire bytes. "
            + "Readers accept 0–9 fractional digits and `Z`, truncating rather than rounding.\n\n"
            + "**Wire-breaking change.** C# maps to `System.DateTimeOffset` (was `DateTime`, which discarded the "
            + "offset on read) and TypeScript to `IonDateTime` (was `Date`, millisecond resolution). Rust wrote a "
            + "bare `[ticks, offset]` array and could not exchange a `datetime` with C# at all."),
        ["dateonly"] = new("dateonly", "Date without time",         "System.DateOnly",  32, null, null),
        ["timeonly"] = new("timeonly", "Time without date",         "System.TimeOnly",  64, null, null),
        ["duration"] = new("duration", "Time span / duration",     "System.TimeSpan",  64, null, null),

        // Generic wrappers
        ["Maybe"]   = new("Maybe<T>",  "Optional value wrapper (nullable)", "T?", null, null, null),
        ["Array"]   = new("Array<T>",  "Variable-length collection", "List<T>", null, null, null),
        ["Partial"] = new("Partial<T>", "Sparse patch over a `msg` — written with the suffix `T~`. Encoded as a CBOR map keyed by field name: an absent key means *untouched*, a `null` value means *cleared*.", "IonPartial<T>", null, null, null),

        // Collections. No suffix spelling exists for either — there is no `T{}` — so they are
        // only ever written out, which is also why hover on the bare name has to be useful.
        ["Map"] = new("Map<K, V>", "Keyed collection", "Dictionary<K, V>", null, null, null,
            "A **definite-length CBOR map** whose keys are sorted in canonical RFC 8949 order — by encoded byte "
            + "*length* first, then lexicographically. Every runtime sorts on write, so the same logical map is "
            + "byte-identical everywhere. Duplicate keys are rejected on read.\n\n"
            + "**Keys are restricted** to the integral scalars, `bool`, `duration`, `string`, `guid` and enums "
            + "(`ION0061`). Floats are excluded: `-0.0` and `0.0` encode differently but compare equal, and `NaN` "
            + "is not equal to itself, so a float-keyed map cannot reproduce its own key set."),
        ["Set"] = new("Set<T>", "Distinct collection", "HashSet<T>", null, null, null,
            "**CBOR tag 258** over a sorted array, in the same canonical order `Map` uses for its keys. Sorted on "
            + "write by every runtime, so the encoding does not depend on insertion order or on the host's hash "
            + "seed."),
    };

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability, ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion")
        };
    }

    public override Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var line = request.Position.Line;     // 0-based
        var col = request.Position.Character; // 0-based

        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);
        if (content is null)
            return Task.FromResult<Hover?>(null);

        var word = IonLspHelpers.GetWordAtPosition(content, line, col);
        if (string.IsNullOrEmpty(word))
            return Task.FromResult<Hover?>(null);

        // 0. Exact declaration under the cursor. This resolves members that the by-name
        //    lookups below cannot disambiguate at all — fields, enum/flags members, method
        //    arguments, union cases, attribute declarations — and carries their doc comment.
        var declHover = FindDeclarationHover(uri, line, col);
        if (declHover is not null)
            return Task.FromResult<Hover?>(Markdown(declHover));

        // 1. Check builtin types
        if (BuiltinTypes.TryGetValue(word, out var builtinInfo))
        {
            return Task.FromResult<Hover?>(new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = FormatBuiltinHover(builtinInfo)
                })
            });
        }

        // 2. Check keywords
        var keywordHover = GetKeywordHover(word);
        if (keywordHover is not null)
        {
            return Task.FromResult<Hover?>(new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = keywordHover
                })
            });
        }

        // 3. Check compiled types/services from workspace
        var ctx = workspace.LastContext;
        if (ctx is not null)
        {
            var hover = FindSymbolHover(word, ctx, uri);
            if (hover is not null)
            {
                return Task.FromResult<Hover?>(new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = hover
                    })
                });
            }
        }

        // 4. Check syntax-level definitions (before compilation)
        var syntaxHover = FindSyntaxHover(word, uri);
        if (syntaxHover is not null)
        {
            return Task.FromResult<Hover?>(new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = syntaxHover
                })
            });
        }

        return Task.FromResult<Hover?>(null);
    }

    private static string FormatBuiltinHover(BuiltinTypeInfo info)
    {
        var lines = new List<string>
        {
            $"```ion\n(builtin) {info.Name}\n```",
            $"**{info.Description}**"
        };

        if (info.CSharpType is not null)
            lines.Add($"C# mapping: `{info.CSharpType}`");

        if (info.Bits is not null)
            lines.Add($"Size: **{info.Bits} bits** ({info.Bits / 8} bytes)");

        if (info.Min is not null && info.Max is not null)
            lines.Add($"Range: `{info.Min}` .. `{info.Max}`");

        if (info.Note is not null)
            lines.Add(info.Note);

        return string.Join("\n\n", lines);
    }

    private static string? GetKeywordHover(string word) => word switch
    {
        "msg" => "```ion\nmsg TypeName {\n    field: type;\n}\n```\nDefines a **message** type — a structured data contract with named fields.\n\n"
                 + "Written in *type position* with a body and no name — `shipping: msg { address: string; };` — it is "
                 + "instead an **inline anonymous type**, which the compiler hoists to `{Owner}{PascalCasedFieldName}`.",
        "mixin" => "```ion\nmixin Audited {\n    createdAt: datetime;\n    createdBy: guid;\n}\n\nmsg Document with Audited {\n    title: string;\n}\n```\n"
                   + "Defines a **field-set template**. Every declaration that names it in a `with` clause gets its "
                   + "fields, complete with their doc comments and attributes.\n\n"
                   + IonMixinLsp.NotATypeNote,
        "with" => "```ion\nmsg Document with Audited, Traced {\n    title: string;\n}\n```\n"
                  + "Includes one or more **mixins**. Available on `msg` and on `mixin`, and on nothing else — a union, "
                  + "service, enum, flags or typedef has no field list to mix into.\n\n"
                  + "**Field order is a hard contract**, because the wire is positional: the mixins in `with` order "
                  + "(each expanded base-first), then the declaration's own fields. A mixin reached by more than one "
                  + "path contributes **once**, at its first position in that walk.",
        "service" => "```ion\nservice ServiceName(base_args) {\n    MethodName(args): ReturnType;\n}\n```\nDefines a **service** — an RPC contract with methods.",
        "enum" => "```ion\nenum EnumName : base_type {\n    Value1,\n    Value2 = 10\n}\n```\nDefines an **enumeration** with named constants.",
        "flags" => "```ion\nflags FlagsName : base_type {\n    Flag1 = 1,\n    Flag2 = 2\n}\n```\nDefines a **flags** type — a bitfield enumeration.",
        // NOTE: union cases have no `case` keyword — a case is just `Name(args)`.
        "union" => "```ion\nunion UnionName {\n    CaseA(field: type),\n    CaseB(field: type)\n}\n```\nDefines a **discriminated union** — a type that can be one of several cases.",
        "typedef" => "```ion\ntypedef NewName = ExistingType;\n```\n"
                     + "Defines a **transparent type alias**. `NewName` *is* `ExistingType` — the alias is "
                     + "erased at compile time, so there is no wrapper, no separate formatter and no wire "
                     + "overhead, and the two names are freely interchangeable.\n\n"
                     + "Because the alias is erased, the schema lock records the **underlying** type at every "
                     + "use site: changing what a typedef points at surfaces as `ION0022` on each field that "
                     + "uses it, not as a change to the typedef itself.",
        "attribute" => "```ion\nattribute @AttrName(arg: type);\n```\nDefines a custom **attribute** that can be applied to types, fields, and methods.",
        "stream" => "Modifier: marks a method parameter or return as a **streaming** channel.",
        "unary" => "Modifier: marks a method as **unary** (single request, single response).",
        "internal" => "Modifier: marks a method as **internal** (not exposed to external clients).",
        "#use" => "```ion\n#use \"path/to/module\"\n```\nImports definitions from another `.ion` file.",
        "#feature" => "```ion\n#feature \"feature_name\"\n```\nEnables a compiler feature (e.g., `std`, `orleans`).",
        _ => null
    };

    // fallbackDoc: doc text from the syntax tree, used when the resolved semantic symbol
    // carries none (e.g. the doc was not propagated through lowering).
    private string? FindSymbolHover(string word, CompilationContext ctx, string currentUri, string? fallbackDoc = null)
    {
        // Search all modules (processed + global)
        var allModules = ctx.ProcessedModules
            .Concat(ctx.GlobalModules)
            .ToList();

        var allDefs = allModules.SelectMany(m => m.Definitions).ToList();

        // Find type
        foreach (var module in allModules)
        {
            foreach (var def in module.Definitions)
            {
                if (!def.name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    continue;

                return FormatTypeHover(def, module, allDefs, def.Doc ?? fallbackDoc);
            }

            foreach (var svc in module.Services)
            {
                if (svc.name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return FormatServiceHover(svc, module, svc.Doc ?? fallbackDoc);

                // Check methods
                foreach (var method in svc.methods)
                {
                    if (method.name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                        return FormatMethodHover(method, svc, method.Doc ?? fallbackDoc);
                }
            }
        }

        return null;
    }

    // Instance rather than static: rendering a typedef may need the parsed syntax held by the
    // workspace to recover the alias target when the IR is not built yet.
    private string FormatTypeHover(
        IonType type, IonModule module, IReadOnlyList<IonType> allDefs, string? doc = null)
    {
        var lines = new List<string>();
        // Everything that goes below the doc section.
        var details = new List<string>();

        // Type signature
        switch (type)
        {
            // A builtin reached through the by-name symbol search rather than through the table
            // above. Every std definition is constructed with `isTypedef: true` and none of them
            // has fields, so the two arms below would render `decimal` as `typedef decimal` with
            // an alias-erasure note, and `Map` as `msg Map {}` — both inventions. The table is
            // complete today; this is what keeps the next builtin from being described as an
            // empty message on the day someone adds one and forgets to update it.
            case { IsBuiltin: true }:
                lines.Add($"```ion\n(builtin) {FormatBuiltinSignature(type)}\n```");
                break;

            case IonEnum e:
                lines.Add($"```ion\nenum {type.name.Identifier} : {e.baseType.name.Identifier}\n```");
                if (e.members.Count > 0)
                {
                    var members = string.Join(", ", e.members.Select(m =>
                        string.IsNullOrEmpty(m.constantValue) ? m.name.Identifier : $"{m.name.Identifier} = {m.constantValue}"));
                    details.Add($"Members: `{members}`");
                }
                break;

            case IonFlags f:
                lines.Add($"```ion\nflags {type.name.Identifier} : {f.baseType.name.Identifier}\n```");
                if (f.members.Count > 0)
                {
                    var members = string.Join(", ", f.members.Select(m =>
                        string.IsNullOrEmpty(m.constantValue) ? m.name.Identifier : $"{m.name.Identifier} = {m.constantValue}"));
                    details.Add($"Flags: `{members}`");
                }
                break;

            case IonUnion u:
                lines.Add($"```ion\nunion {type.name.Identifier}\n```");
                if (u.types.Count > 0)
                {
                    var cases = string.Join(", ", u.types.Select(t => t.name.Identifier));
                    details.Add($"Cases: `{cases}`");
                }
                if (u.sharedFields.Count > 0)
                {
                    var shared = string.Join(", ", u.sharedFields.Select(f => $"{f.name.Identifier}: {f.type.name.Identifier}"));
                    details.Add($"Shared fields: `{shared}`");
                }
                break;

            default:
                if (type.isTypedef)
                {
                    // TransformStage.CompileTypedefs lowers `typedef N = U;` to a single field
                    // named `Value` carrying U. Fall back to the parsed syntax when the IR has
                    // not been built yet (first keystrokes in a fresh file).
                    var underlying = type.fields.Count > 0
                        ? FormatTypeName(type.fields[0].type)
                        : ResolveTypedefUnderlying(type.name.Identifier);
                    lines.Add(underlying is null
                        ? $"```ion\ntypedef {type.name.Identifier}\n```"
                        : $"```ion\ntypedef {type.name.Identifier} = {underlying}\n```");
                    details.Add(TypedefErasureNote);
                }
                else
                {
                    var fieldLines = type.fields.Select(f =>
                        $"    {f.name.Identifier}: {FormatTypeName(f.type)};").ToList();
                    var body = fieldLines.Count > 0
                        ? "\n" + string.Join("\n", fieldLines) + "\n"
                        : "";
                    lines.Add($"```ion\nmsg {type.name.Identifier} {{{body}}}\n```");

                    var sizeInfo = ComputeMessageSize(type, allDefs);
                    if (sizeInfo is not null)
                        details.Add(sizeInfo);
                }
                break;
        }

        IonDocMarkdown.AppendSection(lines, doc);
        lines.AddRange(details);

        if (!module.Path.StartsWith("ion://"))
            lines.Add($"*Defined in `{module.Name}`*");

        return string.Join("\n\n", lines);
    }

    private static string FormatServiceHover(IonService svc, IonModule module, string? doc = null)
    {
        var methodSigs = svc.methods.Select(m =>
        {
            var args = string.Join(", ", m.arguments.Select(a => $"{a.name.Identifier}: {FormatTypeName(a.type)}"));
            var ret = m.returnType.IsVoid ? "" : $": {FormatTypeName(m.returnType)}";
            var mods = m.modifiers.Count > 0
                ? string.Join(" ", m.modifiers.Select(x => x.ToString().ToLowerInvariant())) + " "
                : "";
            return $"    {mods}{m.name.Identifier}({args}){ret};";
        }).ToList();

        var body = methodSigs.Count > 0
            ? "\n" + string.Join("\n", methodSigs) + "\n"
            : "";

        var lines = new List<string>
        {
            $"```ion\nservice {svc.name.Identifier} {{{body}}}\n```"
        };

        IonDocMarkdown.AppendSection(lines, doc);
        lines.Add($"**{svc.methods.Count}** method(s)");

        if (!module.Path.StartsWith("ion://"))
            lines.Add($"*Defined in `{module.Name}`*");

        return string.Join("\n\n", lines);
    }

    private static string FormatMethodHover(IonMethod method, IonService svc, string? doc = null)
    {
        var args = string.Join(", ", method.arguments.Select(a =>
            $"{a.name.Identifier}: {FormatTypeName(a.type)}"));
        var ret = method.returnType.IsVoid ? "" : $": {FormatTypeName(method.returnType)}";
        var mods = method.modifiers.Count > 0
            ? string.Join(" ", method.modifiers.Select(x => x.ToString().ToLowerInvariant())) + " "
            : "";

        var lines = new List<string> { $"```ion\n{mods}{method.name.Identifier}({args}){ret}\n```" };
        IonDocMarkdown.AppendSection(lines, doc);
        lines.Add($"*Method of service `{svc.name.Identifier}`*");

        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// A <em>resolved</em> type in the source spelling the author would write, so a signature
    /// rendered from the IR reads like the file it came from.
    /// </summary>
    /// <remarks>
    /// The lock's own <c>GetCanonicalTypeName</c> is deliberately not reused: it prints
    /// <c>Array&lt;f4, 16&gt;</c> and <c>Maybe&lt;string&gt;</c>, which are correct for
    /// <c>ion.lock.json</c> and wrong for a tooltip, where the reader wants <c>f4[16]</c> and
    /// <c>string?</c>.
    /// </remarks>
    private static string FormatTypeName(IonType type)
    {
        if (type is IonGenericType gt && gt.TypeArguments.Count > 0)
        {
            if (type.IsMaybe)
                return $"{FormatTypeName(gt.TypeArguments[0])}?";
            // The size lives on the wrapper, not on a separate node — see IonGenericType.FixedSize.
            // Dropping it here rendered `f4[16]` as `f4[]`, which is a different wire shape.
            if (type.IsArray)
                return gt.FixedSize is { } size
                    ? $"{FormatTypeName(gt.TypeArguments[0])}[{size}]"
                    : $"{FormatTypeName(gt.TypeArguments[0])}[]";
            // `~` is a *suffix* modifier in the grammar (`Data~`), see IonParser.ModifierOfType.
            if (type.IsPartial)
                return $"{FormatTypeName(gt.TypeArguments[0])}~";
            var args = string.Join(", ", gt.TypeArguments.Select(FormatTypeName));
            return $"{type.name.Identifier}<{args}>";
        }
        return type.name.Identifier;
    }

    /// <summary>A builtin's own spelling — <c>decimal</c>, <c>Map&lt;K, V&gt;</c>.</summary>
    private static string FormatBuiltinSignature(IonType type)
        => type is IonGenericType { TypeParameters.Count: > 0 } generic
            ? $"{type.name.Identifier}<{string.Join(", ", generic.TypeParameters.Select(p => p.Name.Identifier))}>"
            : type.name.Identifier;

    private const string TypedefErasureNote =
        "**Transparent alias** — erased at compile time. Identical to the underlying type on the "
        + "wire; no wrapper and no formatter of its own.";

    /// <summary>
    /// Renders the alias target of a typedef, e.g. <c>u4</c> for <c>typedef UserId = u4;</c>,
    /// from the parsed syntax. Use this only when the compiled IR is unavailable — while the file
    /// has never compiled the workspace still has a parse tree, and hover should still work.
    /// </summary>
    private string? ResolveTypedefUnderlying(string name)
    {
        foreach (var file in workspace.ParsedFiles)
            foreach (var td in file.typedefSyntaxes)
                if (td.BaseType is not null &&
                    td.TypeName.Name.Identifier.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return FormatSyntaxTypeName(td.BaseType);

        return null;
    }

    /// <summary>
    /// Extra hover line shown when a field/parameter is declared with a typedef: the alias is what
    /// the author wrote, so it stays in the signature, and the underlying type is surfaced here.
    /// Nothing is emitted for a non-alias type.
    /// </summary>
    private string? TypedefNote(IonUnderlyingTypeSyntax type)
    {
        var underlying = ResolveTypedefUnderlying(type.Name.Identifier);
        return underlying is null
            ? null
            : $"`{type.Name.Identifier}` is a transparent alias for `{underlying}`.";
    }

    private string? FindSyntaxHover(string word, string uri)
    {
        foreach (var file in workspace.ParsedFiles)
        {
            foreach (var msg in file.messageSyntaxes)
                if (msg.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return Compose(SyntaxMessageSignature(msg), msg.Comments);

            foreach (var svc in file.serviceSyntaxes)
                if (svc.serviceName.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return Compose(SyntaxServiceSignature(svc), svc.Comments);

            foreach (var en in file.enumSyntaxes)
                if (en.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return Compose($"```ion\nenum {en.Name.Identifier} : {FormatSyntaxTypeName(en.Type)}\n```", en.Comments);

            foreach (var fl in file.flagsSyntaxes)
                if (fl.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return Compose($"```ion\nflags {fl.Name.Identifier} : {FormatSyntaxTypeName(fl.Type)}\n```", fl.Comments);

            foreach (var td in file.typedefSyntaxes)
                if (td.TypeName.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return Compose(SyntaxTypedefSignature(td), td.Comments, TypedefErasureNote);

            foreach (var un in file.unionSyntaxes)
                if (un.unionName.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return Compose(SyntaxUnionSignature(un), un.Comments);

            foreach (var attr in file.attributeDefSyntaxes)
                if (attr.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return Compose(SyntaxAttributeSignature(attr), attr.Comments);

            // Last, and reachable mainly from a *type* position: `x: Audited;` is ION0066, and
            // the by-name fallback is what turns the squiggle into an explanation rather than
            // leaving the name with no hover at all.
            foreach (var mixin in file.mixinSyntaxes)
                if (mixin.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return MixinHover(mixin);
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Declaration under the cursor
    // ---------------------------------------------------------------------

    /// <summary>
    /// Resolves the declaration whose identifier token covers the cursor. Unlike the
    /// by-name lookups this is unambiguous, so it can safely surface member level symbols
    /// (fields, enum/flags members, arguments, union cases) together with their doc comment.
    /// </summary>
    private string? FindDeclarationHover(string uri, int line, int col)
    {
        var file = workspace.FindFileByUri(uri);
        if (file is null)
            return null;

        var ctx = workspace.GetContextForFile(uri);

        // --- attribute uses ---------------------------------------------
        // Before the declarations: an attribute's name token and its declaration's name token are
        // the same word, and the use site is the one that can say what the arguments bound to.
        if (IonAttributeLsp.SiteAt(file, line, col) is { } site)
            return AttributeUseHover(site, uri);

        // --- attribute declarations -------------------------------------
        foreach (var attr in file.attributeDefSyntaxes)
        {
            if (IonLspHelpers.Covers(attr.Name, line, col))
                return AttributeDeclarationHover(attr);

            foreach (var arg in attr.Args)
                if (IonLspHelpers.Covers(arg.argName, line, col))
                    return Compose(
                        $"```ion\n(attribute parameter) {arg.argName.Identifier}: {FormatSyntaxTypeName(arg.type)}\n```",
                        arg.Comments,
                        arg.type.IsOptional
                            ? "Optional — declared `" + FormatSyntaxTypeName(arg.type)
                              + "`, so it may be omitted at the use site."
                            : "Required — supply it at every use site.",
                        TypedefNote(arg.type),
                        $"*Parameter of attribute `@{attr.Name.Identifier}`*");

            // The `on` clause. Hovering a target keyword explains the position it names.
            if (attr.Targets is not null)
                foreach (var target in attr.Targets)
                    if (IonLspHelpers.Covers(target, line, col))
                        return TargetKeywordHover(target.Identifier, attr.Name.Identifier);
        }

        // --- field names, ahead of every declaration name ---------------
        // A hoisted inline type's synthesized name token spans the entire `msg { … }` body it was
        // lifted from — see IonLspHelpers.IsHoistedInlineType — so a declaration-name check would
        // answer every position inside the body, including the field names, with the type hover.
        // Field tokens are narrow and can never overlap a written declaration name, so resolving
        // them first is safe for the ordinary case and correct for this one.
        foreach (var msg in file.messageSyntaxes)
            foreach (var field in msg.Fields)
                if (IonLspHelpers.Covers(field.Name, line, col))
                    return FieldHover(field, msg.Name.Identifier,
                        IonLspHelpers.IsHoistedInlineType(msg) ? "inline type" : "message");

        foreach (var mixin in file.mixinSyntaxes)
            foreach (var field in mixin.Fields)
                if (IonLspHelpers.Covers(field.Name, line, col))
                    return FieldHover(field, mixin.Name.Identifier, "mixin");

        // --- mixin declarations and their `with` clauses ----------------
        foreach (var mixin in file.mixinSyntaxes)
        {
            if (IonLspHelpers.Covers(mixin.Name, line, col))
                return MixinHover(mixin);

            if (WithClauseHover(mixin, line, col) is { } clause)
                return clause;
        }

        // --- messages, their `with` clauses, and inline types -----------
        foreach (var msg in file.messageSyntaxes)
        {
            if (IonLspHelpers.Covers(msg.Name, line, col))
                return IonLspHelpers.IsHoistedInlineType(msg)
                    ? InlineTypeHover(msg, file)
                    : WithMixinProvenance(msg,
                        TypeHover(msg.Name.Identifier, msg.Comments, ctx, uri)
                        ?? Compose(SyntaxMessageSignature(msg), msg.Comments));

            if (WithClauseHover(msg, line, col) is { } clause)
                return clause;
        }

        // --- enums / flags and their members ----------------------------
        foreach (var en in file.enumSyntaxes)
        {
            if (IonLspHelpers.Covers(en.Name, line, col))
                return TypeHover(en.Name.Identifier, en.Comments, ctx, uri)
                       ?? Compose($"```ion\nenum {en.Name.Identifier} : {FormatSyntaxTypeName(en.Type)}\n```", en.Comments);

            foreach (var entry in en.Entries)
                if (IonLspHelpers.Covers(entry.Name, line, col))
                    return Compose(
                        $"```ion\n(enum member) {en.Name.Identifier}.{entry.Name.Identifier}{EntryValue(entry)}\n```",
                        entry.Comments,
                        $"*Member of enum `{en.Name.Identifier}`*");
        }

        foreach (var fl in file.flagsSyntaxes)
        {
            if (IonLspHelpers.Covers(fl.Name, line, col))
                return TypeHover(fl.Name.Identifier, fl.Comments, ctx, uri)
                       ?? Compose($"```ion\nflags {fl.Name.Identifier} : {FormatSyntaxTypeName(fl.Type)}\n```", fl.Comments);

            foreach (var entry in fl.Entries)
                if (IonLspHelpers.Covers(entry.Name, line, col))
                    return Compose(
                        $"```ion\n(flags member) {fl.Name.Identifier}.{entry.Name.Identifier}{EntryValue(entry)}\n```",
                        entry.Comments,
                        $"*Member of flags `{fl.Name.Identifier}`*");
        }

        // --- services, methods and arguments ----------------------------
        foreach (var svc in file.serviceSyntaxes)
        {
            if (IonLspHelpers.Covers(svc.serviceName, line, col))
                return ServiceHover(svc.serviceName.Identifier, svc.Comments, ctx, uri)
                       ?? Compose(SyntaxServiceSignature(svc), svc.Comments);

            foreach (var arg in svc.BaseArguments)
                if (IonLspHelpers.Covers(arg.argName, line, col))
                    return Compose(
                        $"```ion\n(service argument) {arg.argName.Identifier}: {FormatSyntaxTypeName(arg.type)}\n```",
                        arg.Comments,
                        TypedefNote(arg.type),
                        $"*Base argument of service `{svc.serviceName.Identifier}`*");

            foreach (var method in svc.Methods)
            {
                if (IonLspHelpers.Covers(method.methodName, line, col))
                    return MethodHover(method.methodName.Identifier, method.Comments, ctx, uri)
                           ?? Compose(
                               $"```ion\n{SyntaxMethodSignature(method)}\n```",
                               method.Comments,
                               $"*Method of service `{svc.serviceName.Identifier}`*");

                foreach (var arg in method.arguments)
                    if (IonLspHelpers.Covers(arg.argName, line, col))
                        return Compose(
                            $"```ion\n(parameter) {ArgModifier(arg)}{arg.argName.Identifier}: {FormatSyntaxTypeName(arg.type)}\n```",
                            arg.Comments,
                            TypedefNote(arg.type),
                            $"*Parameter of `{svc.serviceName.Identifier}.{method.methodName.Identifier}`*");
            }
        }

        // --- unions, cases and their arguments --------------------------
        foreach (var un in file.unionSyntaxes)
        {
            if (IonLspHelpers.Covers(un.unionName, line, col))
                return TypeHover(un.unionName.Identifier, un.Comments, ctx, uri)
                       ?? Compose(SyntaxUnionSignature(un), un.Comments);

            foreach (var arg in un.baseFields)
                if (IonLspHelpers.Covers(arg.argName, line, col))
                    return Compose(
                        $"```ion\n(shared field) {arg.argName.Identifier}: {FormatSyntaxTypeName(arg.type)}\n```",
                        arg.Comments,
                        TypedefNote(arg.type),
                        $"*Shared field of union `{un.unionName.Identifier}`*");

            foreach (var c in un.cases)
            {
                if (IonLspHelpers.Covers(c.caseName.Name, line, col))
                    return Compose(
                        $"```ion\n(union case) {SyntaxUnionCaseSignature(c)}\n```",
                        c.Comments,
                        $"*Case of union `{un.unionName.Identifier}`*");

                foreach (var arg in c.arguments)
                    if (IonLspHelpers.Covers(arg.argName, line, col))
                        return Compose(
                            $"```ion\n(case field) {arg.argName.Identifier}: {FormatSyntaxTypeName(arg.type)}\n```",
                            arg.Comments,
                            TypedefNote(arg.type),
                            $"*Field of union case `{c.caseName.Name.Identifier}`*");
            }
        }

        // --- typedefs ---------------------------------------------------
        foreach (var td in file.typedefSyntaxes)
            if (IonLspHelpers.Covers(td.TypeName.Name, line, col))
                return TypeHover(td.TypeName.Name.Identifier, td.Comments, ctx, uri)
                       ?? Compose(SyntaxTypedefSignature(td), td.Comments, TypedefErasureNote);

        return null;
    }

    // ---------------------------------------------------------------------
    // Mixins and inline types
    // ---------------------------------------------------------------------

    /// <summary>One field of a message, a mixin, or a hoisted inline type.</summary>
    private string FieldHover(IonFieldSyntax field, string owner, string ownerKind)
        => Compose(
            $"```ion\n(field) {field.Name.Identifier}: {FormatSyntaxTypeName(field.Type)}\n```",
            field.Comments,
            TypedefNote(field.Type),
            FixedArrayNote(field.Type),
            $"*Field of {ownerKind} `{owner}`*");

    /// <summary>
    /// Hover over a <c>mixin</c> declaration: what it is, what it contributes in what order, and
    /// who includes it.
    /// </summary>
    /// <remarks>
    /// The <em>expanded</em> field list is the part worth showing. A mixin's body is right there
    /// on screen; what is not on screen is what <c>mixin Traced with Audited</c> actually amounts
    /// to, and — because the wire is positional — the order those fields land in.
    /// </remarks>
    private string MixinHover(IonMixinSyntax mixin)
    {
        var declarations = IonMixinLsp.Declarations(workspace);
        var expanded = IonMixinLsp.Expand(mixin, declarations);

        var lines = new List<string> { $"```ion\n{IonMixinLsp.Signature(mixin)}\n```" };

        IonDocMarkdown.AppendSection(lines, mixin.Comments);

        lines.Add(IonMixinLsp.NotATypeNote);

        if (expanded.Count == 0)
            lines.Add("Contributes **no fields**.");
        else
            lines.Add($"**Contributes {expanded.Count} field(s)**, in this order — this *is* the wire order "
                      + "wherever it is included:\n\n"
                      + string.Join("\n", expanded.Select(FieldLine)));

        var includedBy = IonMixinLsp.IncludedBy(workspace, mixin.Name.Identifier);

        lines.Add(includedBy.Count == 0
            ? "*Included by nothing — see `ION1001`.*"
            : $"*Included by {string.Join(", ", includedBy.Select(i => $"`{i}`"))}*");

        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// Appends "where did these fields come from" to a message hover that has a <c>with</c>
    /// clause.
    /// </summary>
    /// <remarks>
    /// The signature above it — whether rendered from the IR or from the syntax — shows the final
    /// field list but not its provenance, and provenance is the whole question a reader has when
    /// a message declares one field and the tooltip lists five.
    /// </remarks>
    private string WithMixinProvenance(IonMessageSyntax msg, string hover)
    {
        if (msg.Mixins is not { Count: > 0 })
            return hover;

        var declarations = IonMixinLsp.Declarations(workspace);
        var expanded = IonMixinLsp.Expand(msg, declarations);

        return hover + "\n\n"
                     + $"**Expanded field order** (`with {string.Join(", ", msg.Mixins.Select(m => m.Identifier))}`):\n\n"
                     + string.Join("\n", expanded.Select(FieldLine));
    }

    /// <summary>One row of an expanded field list, saying where the field came from.</summary>
    private static string FieldLine(IonMixinField field)
    {
        var rendered = $"- `{field.Field.Name.Identifier}: "
                       + $"{IonLspHelpers.FormatTypeSyntax(field.Field.Type)}`";

        if (field.Origin is null)
            return rendered + " — declared here";

        return field.ListedAs is { } listed && listed != field.Origin
            ? rendered + $" — from `mixin {field.Origin}` (included by `{listed}`)"
            : rendered + $" — from `mixin {field.Origin}`";
    }

    /// <summary>
    /// Hover over one name inside a <c>with</c> clause, or <see langword="null"/> when the cursor
    /// is not on one.
    /// </summary>
    /// <remarks>
    /// The question this answers is not "what is this mixin" — hovering the declaration does that
    /// — but "what does listing it <em>here</em> actually add". Those differ whenever a diamond
    /// is involved: in <c>msg Document with Audited, Traced</c> over <c>mixin Traced with
    /// Audited</c>, the <c>Traced</c> entry contributes only <c>traceId</c>, because <c>Audited</c>
    /// was already spliced by the entry before it. An entry that contributes nothing at all is
    /// worth deleting, and nothing else in the editor can tell you that.
    /// </remarks>
    private string? WithClauseHover(IonSyntaxMember declaration, int line, int col)
    {
        if (IonMixinLsp.ClauseOf(declaration) is not { } clause)
            return null;

        var written = clause.FirstOrDefault(entry => IonLspHelpers.Covers(entry, line, col));

        if (written is null)
            return null;

        var name = written.Identifier;
        var declarations = IonMixinLsp.Declarations(workspace);
        var owner = IonMixinLsp.Describe(declaration);

        if (!declarations.TryGetValue(name, out var mixin))
            return Compose(
                $"```ion\nwith {name}\n```",
                null,
                $"**Not a mixin.** No `mixin {name} {{ … }}` is declared, so this clause entry does not "
                + "resolve — see `ION0063`. A `msg` cannot be included with `with`: a mixin is a field-set "
                + "template with no wire identity of its own.",
                $"*In the `with` clause of {owner}*");

        var contribution = IonMixinLsp.ContributionOf(declaration, name, declarations);

        var lines = new List<string> { $"```ion\n{IonMixinLsp.Signature(mixin)}\n```" };

        IonDocMarkdown.AppendSection(lines, mixin.Comments);

        if (contribution.Count == 0)
        {
            var otherwise = clause
                .Select(e => e.Identifier)
                .FirstOrDefault(other => other != name
                                         && declarations.TryGetValue(other, out var m)
                                         && IonMixinLsp.Expand(m, declarations)
                                             .Any(f => f.Origin == name));

            lines.Add(otherwise is null
                ? $"**Contributes nothing to {owner}.** Every field it declares is already present."
                : $"**Contributes nothing to {owner}** — `{otherwise}` already includes `{name}`, and a "
                  + "mixin is spliced once however many paths reach it. Listing it here is redundant; "
                  + $"`with {otherwise}` alone gives the same fields in the same order.");
        }
        else
        {
            lines.Add($"**Contributes {contribution.Count} field(s) to {owner}:**\n\n"
                      + string.Join("\n", contribution.Select(FieldLine)));
        }

        lines.Add($"*In the `with` clause of {owner}*");

        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// Hover anywhere on an inline anonymous type — <c>shipping: msg { … }</c>.
    /// </summary>
    /// <remarks>
    /// The hoisted name is the entire point. The author never wrote <c>OrderShipping</c>, but it
    /// is the name that goes into <c>ion.lock.json</c>, into three generated languages, and into
    /// every diagnostic about the type — and nothing else in the editor discloses it, because
    /// there is no token in the file that spells it.
    /// </remarks>
    private string InlineTypeHover(IonMessageSyntax hoisted, IonFileSyntax file)
    {
        var name = hoisted.Name.Identifier;
        // SyntaxMessageSignature already returns a fenced block — wrapping it again nests a
        // ```ion fence inside a ```ion fence and the client renders the markers as text.
        var lines = new List<string> { SyntaxMessageSignature(hoisted) };

        IonDocMarkdown.AppendSection(lines, hoisted.Comments);

        lines.Add($"**Inline anonymous type, hoisted to `{name}`.** The name is derived as "
                  + "`{Owner}{PascalCasedFieldName}` and is a real top level declaration from the compiler "
                  + "onwards: it is what appears in `ion.lock.json` and in the generated code. A collision "
                  + "with an explicit declaration is `ION0067` — never a silent rename.");

        if (Owner(file, name) is { } owner)
            lines.Add($"*Written as the type of {owner}*");

        return string.Join("\n\n", lines);
    }

    /// <summary>The field a hoisted inline type was written on, as a phrase.</summary>
    private static string? Owner(IonFileSyntax file, string hoistedName)
    {
        foreach (var msg in file.messageSyntaxes)
            foreach (var field in msg.Fields)
                if (string.Equals(field.Type.Name.Identifier, hoistedName, StringComparison.Ordinal))
                    return $"the field `{field.Name.Identifier}` of `{msg.Name.Identifier}`";

        foreach (var mixin in file.mixinSyntaxes)
            foreach (var field in mixin.Fields)
                if (string.Equals(field.Type.Name.Identifier, hoistedName, StringComparison.Ordinal))
                    return $"the field `{field.Name.Identifier}` of mixin `{mixin.Name.Identifier}`";

        return null;
    }

    /// <summary>
    /// The note on a <c>T[N]</c> field. Nothing is emitted for an unsized array.
    /// </summary>
    /// <remarks>
    /// The lock spelling is the part that earns its place: <c>f4[16]</c> is written as
    /// <c>Array&lt;f4, 16&gt;</c> in <c>ion.lock.json</c>, and because the size is part of the
    /// recorded type, changing it is ION0022 — a breaking change, not a tightening.
    /// </remarks>
    private static string? FixedArrayNote(IonUnderlyingTypeSyntax type)
    {
        if (!type.IsArray || type.ArraySize is not { } size)
            return null;

        if (size < 1)
            return $"**Invalid size.** A fixed-size array must declare at least 1 element — see `ION0062`. "
                   + $"`[{size}]` encodes nothing, so it cannot be told apart from the field being absent.";

        var element = IonLspHelpers.FormatTypeSyntax(type with { ModifierTokens = null, ArraySize = null, IsArray = false });

        return $"**Fixed-size array** — a definite-length CBOR array of exactly **{size}** elements. Any other "
               + $"length is a typed decode error. Recorded in `ion.lock.json` as `Array<{element}, {size}>`, so "
               + "changing the size is `ION0022`.";
    }

    // ---------------------------------------------------------------------
    // Attributes
    // ---------------------------------------------------------------------

    /// <summary>
    /// Hover over an attribute <em>use</em>: the declared signature, where it is allowed, the
    /// declaration's doc, and what each written argument bound to.
    /// </summary>
    /// <remarks>
    /// The argument section is the part that only a use site can give. An attribute's signature is
    /// invisible at the use site — <c>@Cache("user", 30)</c> shows neither parameter name nor type —
    /// so a reader otherwise has to open the declaration and count commas to find out that
    /// <c>30</c> is the duration. Named arguments make that worse, not better, because the written
    /// order no longer matches the declared order.
    /// </remarks>
    private string? AttributeUseHover(IonAttributeSite site, string uri)
    {
        var use = site.Attribute;
        var name = use.Name.Identifier;
        var declaration = IonAttributeLsp.Find(workspace, uri, name);

        if (declaration is null)
            return Compose(
                $"```ion\n@{name}\n```",
                null,
                $"**Not declared.** No `attribute @{name}(…);` is visible from this file "
                + "— see [ION0005]. It may be missing an import or a feature.");

        var lines = new List<string> { $"```ion\n{declaration.Label}\n```" };

        IonDocMarkdown.AppendSection(lines, declaration.Doc);

        lines.Add(declaration.TargetClause is { } clause
            ? $"**Allowed on** — `{clause}`"
            : "**Allowed on** — any declaration (the declaration has no `on` clause).");

        if (!declaration.Allows(site.Target))
            lines.Add($"**Not allowed here.** This is {site.Target.Describe()} — see ION0038.");

        if (RenderArguments(IonAttributeLsp.BindForDisplay(declaration, use)) is { } arguments)
            lines.Add(arguments);

        lines.Add(Origin(declaration));

        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// Each parameter slot with the argument that filled it, so a named or reordered argument list
    /// reads in declaration order rather than in written order.
    /// </summary>
    private static string? RenderArguments(IonAttributeUseBinding binding)
    {
        if (binding.Declaration.Parameters.Count == 0 && binding.Surplus.Count == 0)
            return null;

        var rows = new List<string>();

        for (var i = 0; i < binding.Declaration.Parameters.Count; i++)
        {
            var parameter = binding.Declaration.Parameters[i];

            rows.Add(binding.Values[i] is { } value
                ? $"- `{parameter.Name}: {parameter.Type}` = `{value}`"
                : parameter.IsOptional
                    ? $"- `{parameter.Name}: {parameter.Type}` — *omitted*"
                    : $"- `{parameter.Name}: {parameter.Type}` — **missing** (required)");
        }

        foreach (var extra in binding.Surplus)
            rows.Add($"- `{extra}` — **no matching parameter**");

        return "**Arguments**\n\n" + string.Join("\n", rows);
    }

    /// <summary>Hover over an <c>attribute @x(…) on …;</c> declaration.</summary>
    private string AttributeDeclarationHover(IonAttributeDefSyntax attr)
    {
        var lines = new List<string> { SyntaxAttributeSignature(attr) };

        IonDocMarkdown.AppendSection(lines, attr.Comments);

        lines.Add(attr.Targets is { Count: > 0 }
            ? "**Allowed on** — " + string.Join(", ", attr.Targets.Select(t => $"`{t.Identifier}`"))
            : "**Allowed on** — any declaration. Add an `on` clause to restrict it, e.g. "
              + "`on field, unionCase`.");

        var optional = attr.Args.Count(a => a.type.IsOptional);

        if (attr.Args.Count > 0)
            lines.Add(optional == 0
                ? $"{attr.Args.Count} parameter(s), all required."
                : $"{attr.Args.Count} parameter(s), {optional} optional (`T?`, trailing only).");

        return string.Join("\n\n", lines);
    }

    /// <summary>Hover over one keyword inside an <c>on</c> clause.</summary>
    private static string TargetKeywordHover(string keyword, string attribute)
    {
        if (!IonAttributeTargets.TryParse(keyword, out var target))
            return Compose(
                $"```ion\n{keyword}\n```",
                null,
                $"**Unknown attribute target.** Valid targets are: "
                + string.Join(", ", IonAttributeTargets.Keywords.Select(k => $"`{k}`")) + ". See ION0038.");

        return Compose(
            $"```ion\n{keyword}\n```",
            null,
            $"Attribute target — {target.Describe()}.",
            $"*In the `on` clause of attribute `@{attribute}`*");
    }

    private static string Origin(IonAttributeDeclaration declaration)
        => declaration.IsBuiltin
            ? $"*Builtin attribute from module `{declaration.Origin}`*"
            : $"*Declared in `{declaration.Origin}`*";

    private string? TypeHover(string name, string? syntaxDoc, CompilationContext? ctx, string uri)
        => ctx is null ? null : FindSymbolHover(name, ctx, uri, syntaxDoc);

    private string? ServiceHover(string name, string? syntaxDoc, CompilationContext? ctx, string uri)
        => ctx is null ? null : FindSymbolHover(name, ctx, uri, syntaxDoc);

    private string? MethodHover(string name, string? syntaxDoc, CompilationContext? ctx, string uri)
        => ctx is null ? null : FindSymbolHover(name, ctx, uri, syntaxDoc);

    // ---------------------------------------------------------------------
    // Syntax level signature rendering
    // ---------------------------------------------------------------------

    /// <summary>
    /// Assembles a hover: signature code block, then the doc as markdown below a `---`
    /// separator, then any trailing detail lines. An empty doc emits no separator at all.
    /// </summary>
    private static string Compose(string signature, string? doc, params string?[] extras)
    {
        var sections = new List<string> { signature };
        IonDocMarkdown.AppendSection(sections, doc);
        foreach (var extra in extras)
            if (!string.IsNullOrEmpty(extra))
                sections.Add(extra);
        return string.Join("\n\n", sections);
    }

    private static string EntryValue(IonFlagEntrySyntax entry)
        => entry.ValueExpression.HasValue
            ? $" = {entry.ValueExpression.Value.value.Trim()}"
            : "";

    private static string ArgModifier(IonArgumentSyntax arg)
        => arg.modifiers == IonArgumentModifiers.None
            ? ""
            : arg.modifiers.ToString().ToLowerInvariant() + " ";

    /// <inheritdoc cref="IonLspHelpers.FormatTypeSyntax"/>
    private static string FormatSyntaxTypeName(IonUnderlyingTypeSyntax type)
        => IonLspHelpers.FormatTypeSyntax(type);

    /// <remarks>
    /// The <c>with</c> clause is part of the signature, not a footnote: it is where most of the
    /// message's fields come from, and a signature that omitted it would show a <c>msg</c> with
    /// one field where the wire carries five.
    /// </remarks>
    private static string SyntaxMessageSignature(IonMessageSyntax msg)
    {
        var fields = msg.Fields.Select(f => $"    {f.Name.Identifier}: {FormatSyntaxTypeName(f.Type)};");
        var body = msg.Fields.Count > 0 ? "\n" + string.Join("\n", fields) + "\n" : "";
        var clause = IonMixinLsp.WithClause(msg.Mixins);
        return $"```ion\nmsg {msg.Name.Identifier}{clause} {{{body}}}\n```";
    }

    private static string SyntaxMethodSignature(IonMethodSyntax m)
    {
        var args = string.Join(", ", m.arguments.Select(a =>
            $"{ArgModifier(a)}{a.argName.Identifier}: {FormatSyntaxTypeName(a.type)}"));
        var ret = m.returnType is not null ? $": {FormatSyntaxTypeName(m.returnType)}" : "";
        var mods = m.modifiers.Count > 0
            ? string.Join(" ", m.modifiers.Select(x => x.ToString().ToLowerInvariant())) + " "
            : "";
        return $"{mods}{m.methodName.Identifier}({args}){ret}";
    }

    private static string SyntaxServiceSignature(IonServiceSyntax svc)
    {
        var methods = svc.Methods.Select(m => $"    {SyntaxMethodSignature(m)};");
        var body = svc.Methods.Count > 0 ? "\n" + string.Join("\n", methods) + "\n" : "";
        var baseArgs = svc.BaseArguments.Count > 0
            ? "(" + string.Join(", ", svc.BaseArguments.Select(a =>
                $"{a.argName.Identifier}: {FormatSyntaxTypeName(a.type)}")) + ")"
            : "";
        return $"```ion\nservice {svc.serviceName.Identifier}{baseArgs} {{{body}}}\n```";
    }

    private static string SyntaxUnionCaseSignature(IonUnionTypeCaseSyntax c)
    {
        if (c.IsTypeRef)
            return FormatSyntaxTypeName(c.caseName);
        var args = string.Join(", ", c.arguments.Select(a =>
            $"{a.argName.Identifier}: {FormatSyntaxTypeName(a.type)}"));
        return $"{FormatSyntaxTypeName(c.caseName)}({args})";
    }

    private static string SyntaxUnionSignature(IonUnionSyntax un)
    {
        var cases = un.cases.Select(c => $"    {SyntaxUnionCaseSignature(c)},");
        var body = un.cases.Count > 0 ? "\n" + string.Join("\n", cases) + "\n" : "";
        var baseArgs = un.baseFields.Count > 0
            ? "(" + string.Join(", ", un.baseFields.Select(a =>
                $"{a.argName.Identifier}: {FormatSyntaxTypeName(a.type)}")) + ")"
            : "";
        return $"```ion\nunion {un.unionName.Identifier}{baseArgs} {{{body}}}\n```";
    }

    private static string SyntaxTypedefSignature(IonTypedefSyntax td)
    {
        var baseType = td.BaseType is not null ? $" = {FormatSyntaxTypeName(td.BaseType)}" : "";
        return $"```ion\ntypedef {FormatSyntaxTypeName(td.TypeName)}{baseType}\n```";
    }

    /// <summary>
    /// The declaration exactly as it would be written, <c>on</c> clause included. The clause is part
    /// of the signature, not a footnote: it is what decides whether a given use is ION0038.
    /// </summary>
    private static string SyntaxAttributeSignature(IonAttributeDefSyntax attr)
    {
        var args = string.Join(", ", attr.Args.Select(a =>
            $"{a.argName.Identifier}: {FormatSyntaxTypeName(a.type)}"));

        var on = attr.Targets is { Count: > 0 }
            ? " on " + string.Join(", ", attr.Targets.Select(t => t.Identifier))
            : "";

        return $"```ion\nattribute @{attr.Name.Identifier}({args}){on};\n```";
    }

    private static Hover Markdown(string value) => new()
    {
        Contents = new MarkedStringsOrMarkupContent(new MarkupContent
        {
            Kind = MarkupKind.Markdown,
            Value = value
        })
    };

    private static string? ComputeMessageSize(IonType type, IReadOnlyList<IonType> allDefs)
    {
        if (type.fields.Count == 0)
            return null;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBits = 0;
        var hasVariable = false;
        var fieldSizes = new List<string>();

        foreach (var field in type.fields)
        {
            var bits = GetTypeBits(field.type, visited, allDefs);
            if (bits is not null)
            {
                totalBits += bits.Value;
                fieldSizes.Add($"`{field.name.Identifier}`: {bits.Value / 8} B");
            }
            else
            {
                hasVariable = true;
                fieldSizes.Add($"`{field.name.Identifier}`: variable");
            }
        }

        var parts = new List<string>();

        if (totalBits > 0 || !hasVariable)
        {
            var totalBytes = totalBits / 8;
            var prefix = hasVariable ? "Fixed part" : "Size";
            parts.Add($"**{prefix}: {totalBytes} bytes** ({totalBits} bits)");
        }

        if (hasVariable)
            parts.Add("\\+ variable-length fields");

        if (fieldSizes.Count > 1)
            parts.Add(string.Join(" ∙ ", fieldSizes));

        return string.Join("\n\n", parts);
    }

    private static int? GetTypeBits(IonType type, HashSet<string> visited, IReadOnlyList<IonType> allDefs)
    {
        // Variable-width wrappers. A `Partial<T>` in particular is a CBOR map holding only the
        // keys the sender touched, so its encoded size is unrelated to `sizeof(T)`; `Map` and
        // `Set` hold an unbounded number of entries.
        if (type.IsMaybe || type.IsArray || type.IsPartial || type.IsMap || type.IsSet)
            return null;

        if (type.HasBitsAttribute)
            return type.Bits;

        var name = type.name.Identifier;

        switch (name)
        {
            case "bool": return 8;
            case "guid": return 128;
            case "dateonly": return 32;
            case "timeonly": return 64;
            case "duration": return 64;
            case "void": return 0;

            // `datetime` was 64 here when it encoded as a number. It is now tag 0 + RFC 3339 text
            // — 36 bytes today, but text whose width is a property of the encoding rather than of
            // the type, so it belongs with the variable ones rather than with a new constant.
            // `decimal` is tag 4 over a variable-length mantissa and never had a fixed width.
            case "string" or "bytes" or "bigint" or "uri" or "datetime" or "decimal":
                return null;
        }

        // Resolve actual type definition from all known definitions
        var resolved = type.fields.Count > 0
            ? type
            : allDefs.FirstOrDefault(d => d.name.Identifier.Equals(name, StringComparison.OrdinalIgnoreCase));

        // Nested message / struct — recursively sum fields
        if (resolved is not null && resolved.fields.Count > 0 && visited.Add(name))
        {
            var total = 0;
            foreach (var field in resolved.fields)
            {
                var fieldBits = GetTypeBits(field.type, visited, allDefs);
                if (fieldBits is null)
                {
                    visited.Remove(name);
                    return null;
                }
                total += fieldBits.Value;
            }
            visited.Remove(name);
            return total;
        }

        // Enum/flags — use base type size
        if (resolved is IonEnum e)
            return GetTypeBits(e.baseType, visited, allDefs);
        if (resolved is IonFlags f)
            return GetTypeBits(f.baseType, visited, allDefs);

        if (type.IsScalar && type.HasBitsAttribute)
            return type.Bits;

        return null;
    }

    /// <param name="Bits">
    /// Fixed wire width, or <see langword="null"/> when the encoding is variable-length. Only the
    /// types that really are one fixed-width CBOR head carry a number: <c>datetime</c> lost its
    /// <c>64</c> when it became RFC 3339 text, and <c>decimal</c> never had one.
    /// </param>
    /// <param name="Note">
    /// Extra prose below the table lines — the wire rule, and anything about the type that a
    /// reader cannot infer from its name. Rendered as markdown.
    /// </param>
    private record BuiltinTypeInfo(
        string Name,
        string Description,
        string? CSharpType,
        int? Bits,
        string? Min,
        string? Max,
        string? Note = null);
}
