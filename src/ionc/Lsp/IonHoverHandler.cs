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

        // Date/Time
        ["datetime"] = new("datetime", "Date and time (UTC)",      "DateTime",  64, null, null),
        ["dateonly"] = new("dateonly", "Date without time",         "DateOnly",  32, null, null),
        ["timeonly"] = new("timeonly", "Time without date",         "TimeOnly",  64, null, null),
        ["duration"] = new("duration", "Time span / duration",     "TimeSpan",  64, null, null),

        // Generic wrappers
        ["Maybe"]   = new("Maybe<T>",  "Optional value wrapper (nullable)", "T?", null, null, null),
        ["Array"]   = new("Array<T>",  "Variable-length collection", "List<T>", null, null, null),
        ["Partial"] = new("Partial<T>", "Partial object (only set fields are serialized)", "Partial<T>", null, null, null),

        // Vectors
        ["vec2f"] = new("vec2f", "2D vector (single-precision)", "Vector2", 64, null, null),
        ["vec3f"] = new("vec3f", "3D vector (single-precision)", "Vector3", 96, null, null),
        ["vec4f"] = new("vec4f", "4D vector (single-precision)", "Vector4", 128, null, null),
        ["vec2d"] = new("vec2d", "2D vector (double-precision)", "(double, double)", 128, null, null),
        ["vec3d"] = new("vec3d", "3D vector (double-precision)", "(double, double, double)", 192, null, null),
        ["vec4d"] = new("vec4d", "4D vector (double-precision)", "(double, double, double, double)", 256, null, null),
        ["vec2h"] = new("vec2h", "2D vector (half-precision)", "(Half, Half)", 32, null, null),
        ["vec3h"] = new("vec3h", "3D vector (half-precision)", "(Half, Half, Half)", 48, null, null),
        ["vec4h"] = new("vec4h", "4D vector (half-precision)", "(Half, Half, Half, Half)", 64, null, null),
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

        return string.Join("\n\n", lines);
    }

    private static string? GetKeywordHover(string word) => word switch
    {
        "msg" => "```ion\nmsg TypeName {\n    field: type;\n}\n```\nDefines a **message** type — a structured data contract with named fields.",
        "service" => "```ion\nservice ServiceName(base_args) {\n    MethodName(args): ReturnType;\n}\n```\nDefines a **service** — an RPC contract with methods.",
        "enum" => "```ion\nenum EnumName : base_type {\n    Value1,\n    Value2 = 10\n}\n```\nDefines an **enumeration** with named constants.",
        "flags" => "```ion\nflags FlagsName : base_type {\n    Flag1 = 1,\n    Flag2 = 2\n}\n```\nDefines a **flags** type — a bitfield enumeration.",
        "union" => "```ion\nunion UnionName {\n    case CaseA(field: type),\n    case CaseB(field: type)\n}\n```\nDefines a **discriminated union** — a type that can be one of several cases.",
        "typedef" => "```ion\ntypedef NewName = ExistingType;\n```\nDefines a **type alias** for an existing type.",
        "attribute" => "```ion\nattribute AttrName(arg: type);\n```\nDefines a custom **attribute** that can be applied to types, fields, and methods.",
        "stream" => "Modifier: marks a method parameter or return as a **streaming** channel.",
        "unary" => "Modifier: marks a method as **unary** (single request, single response).",
        "internal" => "Modifier: marks a method as **internal** (not exposed to external clients).",
        "#use" => "```ion\n#use \"path/to/module\"\n```\nImports definitions from another `.ion` file.",
        "#feature" => "```ion\n#feature \"feature_name\"\n```\nEnables a compiler feature (e.g., `std`, `vector`, `orleans`).",
        _ => null
    };

    private string? FindSymbolHover(string word, CompilationContext ctx, string currentUri)
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

                return FormatTypeHover(def, module, allDefs);
            }

            foreach (var svc in module.Services)
            {
                if (svc.name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return FormatServiceHover(svc, module);

                // Check methods
                foreach (var method in svc.methods)
                {
                    if (method.name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                        return FormatMethodHover(method, svc);
                }
            }
        }

        return null;
    }

    private static string FormatTypeHover(IonType type, IonModule module, IReadOnlyList<IonType> allDefs)
    {
        var kind = type switch
        {
            IonEnum e => "enum",
            IonFlags f => "flags",
            IonUnion u => "union",
            _ when type.isTypedef => "typedef",
            _ => "msg"
        };

        var lines = new List<string>();

        // Type signature
        switch (type)
        {
            case IonEnum e:
                lines.Add($"```ion\nenum {type.name.Identifier} : {e.baseType.name.Identifier}\n```");
                if (e.members.Count > 0)
                {
                    var members = string.Join(", ", e.members.Select(m =>
                        string.IsNullOrEmpty(m.constantValue) ? m.name.Identifier : $"{m.name.Identifier} = {m.constantValue}"));
                    lines.Add($"Members: `{members}`");
                }
                break;

            case IonFlags f:
                lines.Add($"```ion\nflags {type.name.Identifier} : {f.baseType.name.Identifier}\n```");
                if (f.members.Count > 0)
                {
                    var members = string.Join(", ", f.members.Select(m =>
                        string.IsNullOrEmpty(m.constantValue) ? m.name.Identifier : $"{m.name.Identifier} = {m.constantValue}"));
                    lines.Add($"Flags: `{members}`");
                }
                break;

            case IonUnion u:
                lines.Add($"```ion\nunion {type.name.Identifier}\n```");
                if (u.types.Count > 0)
                {
                    var cases = string.Join(", ", u.types.Select(t => t.name.Identifier));
                    lines.Add($"Cases: `{cases}`");
                }
                if (u.sharedFields.Count > 0)
                {
                    var shared = string.Join(", ", u.sharedFields.Select(f => $"{f.name.Identifier}: {f.type.name.Identifier}"));
                    lines.Add($"Shared fields: `{shared}`");
                }
                break;

            default:
                if (type.isTypedef)
                {
                    lines.Add($"```ion\ntypedef {type.name.Identifier}\n```");
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
                        lines.Add(sizeInfo);
                }
                break;
        }

        if (!module.Path.StartsWith("ion://"))
            lines.Add($"*Defined in `{module.Name}`*");

        return string.Join("\n\n", lines);
    }

    private static string FormatServiceHover(IonService svc, IonModule module)
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
            $"```ion\nservice {svc.name.Identifier} {{{body}}}\n```",
            $"**{svc.methods.Count}** method(s)"
        };

        if (!module.Path.StartsWith("ion://"))
            lines.Add($"*Defined in `{module.Name}`*");

        return string.Join("\n\n", lines);
    }

    private static string FormatMethodHover(IonMethod method, IonService svc)
    {
        var args = string.Join(", ", method.arguments.Select(a =>
            $"{a.name.Identifier}: {FormatTypeName(a.type)}"));
        var ret = method.returnType.IsVoid ? "" : $": {FormatTypeName(method.returnType)}";
        var mods = method.modifiers.Count > 0
            ? string.Join(" ", method.modifiers.Select(x => x.ToString().ToLowerInvariant())) + " "
            : "";

        return $"```ion\n{mods}{method.name.Identifier}({args}){ret}\n```\n\n*Method of service `{svc.name.Identifier}`*";
    }

    private static string FormatTypeName(IonType type)
    {
        if (type is IonGenericType gt && gt.TypeArguments.Count > 0)
        {
            if (type.IsMaybe)
                return $"{FormatTypeName(gt.TypeArguments[0])}?";
            if (type.IsArray)
                return $"{FormatTypeName(gt.TypeArguments[0])}[]";
            if (type.IsPartial)
                return $"~{FormatTypeName(gt.TypeArguments[0])}";
            var args = string.Join(", ", gt.TypeArguments.Select(FormatTypeName));
            return $"{type.name.Identifier}<{args}>";
        }
        return type.name.Identifier;
    }

    private string? FindSyntaxHover(string word, string uri)
    {
        foreach (var file in workspace.ParsedFiles)
        {
            foreach (var msg in file.messageSyntaxes)
            {
                if (msg.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                {
                    var fields = msg.Fields.Select(f => $"    {f.Name.Identifier}: {f.Type.Name.Identifier};");
                    var body = msg.Fields.Count > 0
                        ? "\n" + string.Join("\n", fields) + "\n"
                        : "";
                    return $"```ion\nmsg {msg.Name.Identifier} {{{body}}}\n```";
                }
            }

            foreach (var svc in file.serviceSyntaxes)
            {
                if (svc.serviceName.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                {
                    var methods = svc.Methods.Select(m =>
                    {
                        var args = string.Join(", ", m.arguments.Select(a => $"{a.argName.Identifier}: {a.type.Name.Identifier}"));
                        var ret = m.returnType is not null ? $": {m.returnType.Name.Identifier}" : "";
                        return $"    {m.methodName.Identifier}({args}){ret};";
                    });
                    var body = svc.Methods.Count > 0
                        ? "\n" + string.Join("\n", methods) + "\n"
                        : "";
                    return $"```ion\nservice {svc.serviceName.Identifier} {{{body}}}\n```";
                }
            }

            foreach (var en in file.enumSyntaxes)
                if (en.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return $"```ion\nenum {en.Name.Identifier} : {en.Type.Name.Identifier}\n```";

            foreach (var fl in file.flagsSyntaxes)
                if (fl.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return $"```ion\nflags {fl.Name.Identifier} : {fl.Type.Name.Identifier}\n```";

            foreach (var td in file.typedefSyntaxes)
                if (td.TypeName.Name.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                {
                    var baseType = td.BaseType is not null ? $" = {td.BaseType.Name.Identifier}" : "";
                    return $"```ion\ntypedef {td.TypeName.Name.Identifier}{baseType}\n```";
                }

            foreach (var un in file.unionSyntaxes)
                if (un.unionName.Identifier.Equals(word, StringComparison.OrdinalIgnoreCase))
                    return $"```ion\nunion {un.unionName.Identifier}\n```";
        }

        return null;
    }

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
        if (type.IsMaybe || type.IsArray || type.IsPartial)
            return null;

        if (type.HasBitsAttribute)
            return type.Bits;

        var name = type.name.Identifier;

        switch (name)
        {
            case "bool": return 8;
            case "guid": return 128;
            case "datetime": return 64;
            case "dateonly": return 32;
            case "timeonly": return 64;
            case "duration": return 64;
            case "void": return 0;
            case "string" or "bytes" or "bigint" or "uri":
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

    private record BuiltinTypeInfo(
        string Name,
        string Description,
        string? CSharpType,
        int? Bits,
        string? Min,
        string? Max);
}
