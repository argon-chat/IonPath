namespace ion.compiler.Lsp;

using ion.runtime;
using ion.syntax;
using Pidgin;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

/// <summary>
/// Shared utilities for LSP handlers: word extraction, symbol lookup, position conversion.
/// </summary>
public static class IonLspHelpers
{
    /// <summary>
    /// Every non-generic builtin type name, in the order <c>IonModule.GetStdModule</c> declares
    /// them.
    /// </summary>
    /// <remarks>
    /// One list, shared by completion and by semantic-token classification. There used to be two
    /// hand-maintained copies and they had already drifted apart from the std module and from each
    /// other; the retired <c>vector</c> feature's <c>vec2f…vec4h</c> lived in both for a while
    /// after the types themselves were deleted. Anything absent here is coloured as a
    /// user-defined type and offered by no completion, which is precisely how <c>decimal</c>
    /// behaved before it was added.
    /// </remarks>
    public static readonly string[] BuiltinTypeNames =
    [
        "i1", "i2", "i4", "i8", "i16",
        "u1", "u2", "u4", "u8", "u16",
        "f2", "f4", "f8",
        "bool", "void", "string", "bytes", "guid",
        "decimal", "bigint",
        "datetime", "dateonly", "timeonly", "duration", "uri"
    ];

    /// <summary>
    /// The five builtin generics, with their declared parameter names and a one-line summary.
    /// </summary>
    /// <remarks>
    /// The parameter names are the ones <c>IonModule.GetStdModule</c> declares, so the snippet
    /// placeholders read the same way ION0060's "write <c>Map&lt;K, V&gt;</c>" remedy does.
    /// </remarks>
    public static readonly (string Name, string[] Parameters, string Detail)[] BuiltinGenerics =
    [
        ("Maybe", ["T"], "Builtin generic — optional value. Suffix form: T?"),
        ("Array", ["T"], "Builtin generic — variable-length sequence. Suffix form: T[]"),
        ("Partial", ["T"], "Builtin generic — sparse patch over a msg. Suffix form: T~"),
        ("Map", ["K", "V"], "Builtin generic — keyed collection. No suffix form; write it out"),
        ("Set", ["T"], "Builtin generic — distinct collection. No suffix form; write it out")
    ];

    /// <summary>Whether a written name is a builtin type, generic or otherwise.</summary>
    /// <remarks>
    /// Case-insensitive, matching how the semantic-token classifier has always asked. Builtin
    /// <em>resolution</em> is case-sensitive (<c>msg U4</c> shadows nothing — see ION0031), so
    /// this is deliberately the looser test: mis-colouring <c>Datetime</c> as a builtin is a
    /// cosmetic wrong answer, whereas leaving it plain would hide a real typo.
    /// </remarks>
    public static bool IsBuiltinTypeName(string name)
        => BuiltinTypeNames.Contains(name, StringComparer.OrdinalIgnoreCase)
           || BuiltinGenerics.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Extract the word under the cursor at a given 0-based line/col.
    /// </summary>
    public static string GetWordAtPosition(string content, int line, int col)
    {
        var lines = content.Split('\n');
        if (line < 0 || line >= lines.Length)
            return "";

        var lineText = lines[line].TrimEnd('\r');
        if (col < 0 || col >= lineText.Length)
            return "";

        if (!IsWordChar(lineText[col]))
            return "";

        var start = col;
        var end = col;

        while (start > 0 && IsWordChar(lineText[start - 1]))
            start--;
        while (end < lineText.Length - 1 && IsWordChar(lineText[end + 1]))
            end++;

        return lineText[start..(end + 1)];
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// True when the 0-based position sits inside a comment or a string literal.
    /// Handlers that resolve the symbol under the cursor from raw text must bail out on
    /// those positions, otherwise a word that merely *appears* in prose gets treated as a
    /// real reference (and, for rename, rewrites every genuine declaration of that name).
    /// </summary>
    public static bool IsInCommentOrString(string content, int line, int character)
        => IonCommentScanner.Scan(content).IsCommentOrString(line, character);

    /// <summary>
    /// True when an identifier token covers the given 0-based position.
    /// <see cref="IonIdentifier"/> carries an exact start/end from the parser.
    /// </summary>
    public static bool Covers(IonIdentifier id, int line, int character)
    {
        var start = id.StartPosition;
        if (start.Line <= 0) return false;
        if (line != start.Line - 1) return false;

        var startCol = start.Col - 1;
        var endCol = id.EndPosition is { } ep && ep.Line == start.Line
            ? ep.Col - 1
            : startCol + id.Identifier.Length;

        return character >= startCol && character < endCol;
    }

    /// <summary>
    /// A written type reference rendered back into source spelling, nesting and all —
    /// <c>Map&lt;string, Array&lt;User&gt;&gt;</c>, <c>f4[16]</c>, <c>Data~[]?</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Recurses through <see cref="IonTypeParameterSyntax.Type"/>, never through
    /// <c>Name</c>.</strong> An argument's <c>Name</c> is only its <em>head</em>: it is
    /// <c>Array</c> for <c>Array&lt;User&gt;</c> and <c>Maybe</c> for <c>User?</c>, so every
    /// handler that read it rendered <c>Map&lt;string, Array&gt;</c> and
    /// <c>Map&lt;string, Maybe&gt;</c>. Three copies of this function existed — in hover, in the
    /// code lens and in <see cref="IonAttributeLsp"/> — and all three had the bug; they are one
    /// function now so a fix cannot land in two of the three again.
    /// </para>
    /// <para>
    /// Modifiers are re-emitted in the canonical order <c>T~[]?</c> rather than in written order,
    /// so the result is always something the parser accepts back without ION0010. A fixed size is
    /// rendered on the array suffix, <c>f4[16]</c>, because that is where the author wrote it —
    /// the <c>Array&lt;f4, 16&gt;</c> spelling belongs to <c>ion.lock.json</c> and nowhere else.
    /// </para>
    /// <para>
    /// An inline anonymous body renders as <c>msg { … }</c>. In a compiled workspace it has
    /// already been hoisted and rewritten to its derived name, so this arm is only reached while
    /// the file has never compiled — but it must not print the unlexable <c>$inline</c>
    /// placeholder at the user.
    /// </para>
    /// </remarks>
    public static string FormatTypeSyntax(IonUnderlyingTypeSyntax type)
    {
        var name = type.IsInline
            ? "msg { … }"
            : type.Name.Identifier;

        if (type.generics.Count > 0)
            name += "<" + string.Join(", ", type.generics.Select(FormatArgument)) + ">";

        if (type.IsPartial) name += "~";
        if (type.IsArray) name += type.ArraySize is { } size ? $"[{size}]" : "[]";
        if (type.IsOptional) name += "?";

        return name;
    }

    /// <summary>
    /// One generic argument. Falls back to the head name only for a node somebody synthesized by
    /// hand — the parser always fills <see cref="IonTypeParameterSyntax.Type"/> in.
    /// </summary>
    private static string FormatArgument(IonTypeParameterSyntax argument)
        => argument.Type is { } written ? FormatTypeSyntax(written) : argument.Name.Identifier;

    /// <summary>
    /// Whether this <c>msg</c> is one <c>InlineTypeHoistingStage</c> synthesized from an inline
    /// anonymous body, rather than one the author wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The language server reads the very same syntax tree the pipeline just mutated —
    /// <c>IonWorkspace.CompileProject</c> hands <c>project.ParsedFiles</c> the list it passed
    /// through <c>CompilationPipeline.Execute</c> — so by the time any handler runs there are no
    /// inline bodies left, and the hoisted messages are sitting in
    /// <see cref="IonFileSyntax.messageSyntaxes"/> looking exactly like declarations.
    /// </para>
    /// <para>
    /// They are told apart by their span. A written <c>msg Foo { … }</c> starts at the <c>msg</c>
    /// keyword while its name token starts at <c>Foo</c>, at least four columns later. A hoisted
    /// one is built with <c>StartPosition = body.StartPosition</c> and its synthesized name
    /// identifier carries <em>the same</em> span — the whole <c>msg { … }</c> the author wrote.
    /// Equal spans therefore mean synthesized, and there is no way to write a message that fakes
    /// it.
    /// </para>
    /// <para>
    /// That shared span is also why order matters when resolving what the cursor is on: a hoisted
    /// name token covers every character of the body, including the field names inside it.
    /// </para>
    /// </remarks>
    public static bool IsHoistedInlineType(IonMessageSyntax msg)
        => msg.Name.StartPosition == msg.StartPosition
           && msg.EndPosition is { } end
           && msg.Name.EndPosition == end;

    /// <summary>
    /// Whether an identifier's recorded span is not the span of its own text — i.e. the node was
    /// synthesized by the compiler rather than lexed from source.
    /// </summary>
    /// <remarks>
    /// <see cref="IonParser.Identifier"/> captures <c>CurrentPos</c> immediately before and after
    /// the identifier characters, so a lexed name always spans exactly its own length on one line.
    /// The name <c>InlineTypeHoistingStage</c> puts on a rewritten field type carries the span of
    /// the whole <c>msg { … }</c> body instead, which is longer and usually multi-line. Anything
    /// that turns a name into a screen range — a semantic token, a rename edit, a highlight — has
    /// to skip those, or it paints or rewrites source the identifier does not occupy.
    /// </remarks>
    public static bool IsSynthesizedSpan(IonIdentifier id)
        => id.EndPosition is { } end
           && (end.Line != id.StartPosition.Line
               || end.Col - id.StartPosition.Col != id.Identifier.Length);

    /// <summary>The names of every hoisted inline type in a file.</summary>
    public static HashSet<string> HoistedTypeNames(IonFileSyntax file)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var msg in file.messageSyntaxes)
            if (IsHoistedInlineType(msg))
                names.Add(msg.Name.Identifier);

        return names;
    }

    /// <summary>
    /// Convert a 1-based Pidgin SourcePos to a 0-based LSP Position.
    /// </summary>
    public static Position ToLspPosition(SourcePos pos)
        => new(Math.Max(0, pos.Line - 1), Math.Max(0, pos.Col - 1));

    /// <summary>
    /// Convert an IonSyntaxBase to an LSP Range.
    /// </summary>
    public static Range ToLspRange(IonSyntaxBase node)
    {
        var start = ToLspPosition(node.StartPosition);
        var end = node.EndPosition is { } ep
            ? ToLspPosition(ep)
            : start;
        return new Range(start, end);
    }

    /// <summary>
    /// Find all locations where a symbol name is defined across parsed files.
    /// Returns (file URI, syntax node) pairs.
    /// </summary>
    public static List<(string fileUri, IonSyntaxBase node)> FindDefinitions(
        string symbolName, IonWorkspace workspace)
    {
        var results = new List<(string, IonSyntaxBase)>();

        foreach (var file in workspace.ParsedFiles)
        {
            var uri = workspace.GetFileUri(file);

            foreach (var msg in file.messageSyntaxes)
                if (msg.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                    results.Add((uri, msg.Name));

            foreach (var svc in file.serviceSyntaxes)
            {
                if (svc.serviceName.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                    results.Add((uri, svc.serviceName));

                foreach (var m in svc.Methods)
                    if (m.methodName.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                        results.Add((uri, m.methodName));
            }

            foreach (var en in file.enumSyntaxes)
            {
                if (en.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                    results.Add((uri, en.Name));
                foreach (var entry in en.Entries)
                    if (entry.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                        results.Add((uri, entry.Name));
            }

            foreach (var fl in file.flagsSyntaxes)
            {
                if (fl.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                    results.Add((uri, fl.Name));
                foreach (var entry in fl.Entries)
                    if (entry.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                        results.Add((uri, entry.Name));
            }

            foreach (var un in file.unionSyntaxes)
                if (un.unionName.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                    results.Add((uri, un.unionName));

            foreach (var td in file.typedefSyntaxes)
                if (td.TypeName.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                    results.Add((uri, td.TypeName.Name));

            foreach (var attr in file.attributeDefSyntaxes)
                if (attr.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                    results.Add((uri, attr.Name));

            // Mixins share the declaration namespace with everything above (a `mixin Audited`
            // beside a `msg Audited` is ION0002), so go-to-definition has to reach them or a
            // `with Audited` is a dead link.
            foreach (var mixin in file.mixinSyntaxes)
                if (mixin.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                    results.Add((uri, mixin.Name));
        }

        return results;
    }

    /// <summary>
    /// Find all references (usages) of a symbol name across parsed files.
    /// Includes type references in fields, method args, return types, use statements, etc.
    /// </summary>
    public static List<(string fileUri, IonSyntaxBase node)> FindReferences(
        string symbolName, IonWorkspace workspace, bool includeDefinition)
    {
        var results = new List<(string, IonSyntaxBase)>();

        if (includeDefinition)
            results.AddRange(FindDefinitions(symbolName, workspace));

        foreach (var file in workspace.ParsedFiles)
        {
            var uri = workspace.GetFileUri(file);

            // Type references in message fields, plus the `with` clause. A clause entry is the
            // only kind of reference a mixin can have, so without this a mixin always reads as
            // "0 references" and renaming one silently rewrites the declaration alone, leaving
            // every includer pointing at a name that no longer exists.
            foreach (var msg in file.messageSyntaxes)
            {
                CollectMixinRefs(msg.Mixins, symbolName, uri, results);

                foreach (var field in msg.Fields)
                {
                    CollectTypeRefs(field.Type, symbolName, uri, results);
                }
            }

            // Mixin bodies are field lists like any other, and a mixin may itself compose others.
            foreach (var mixin in file.mixinSyntaxes)
            {
                CollectMixinRefs(mixin.Mixins, symbolName, uri, results);

                foreach (var field in mixin.Fields)
                    CollectTypeRefs(field.Type, symbolName, uri, results);
            }

            // Type references in services
            foreach (var svc in file.serviceSyntaxes)
            {
                // Base arguments
                foreach (var arg in svc.BaseArguments)
                    CollectTypeRefs(arg.type, symbolName, uri, results);

                foreach (var method in svc.Methods)
                {
                    foreach (var arg in method.arguments)
                        CollectTypeRefs(arg.type, symbolName, uri, results);

                    if (method.returnType is not null)
                        CollectTypeRefs(method.returnType, symbolName, uri, results);
                }
            }

            // Typedef base types
            foreach (var td in file.typedefSyntaxes)
            {
                if (td.BaseType is not null)
                    CollectTypeRefs(td.BaseType, symbolName, uri, results);
            }

            // Enum/flags base types
            foreach (var en in file.enumSyntaxes)
                CollectTypeRefs(en.Type, symbolName, uri, results);
            foreach (var fl in file.flagsSyntaxes)
                CollectTypeRefs(fl.Type, symbolName, uri, results);

            // Union cases
            foreach (var un in file.unionSyntaxes)
            {
                foreach (var arg in un.baseFields)
                    CollectTypeRefs(arg.type, symbolName, uri, results);
                foreach (var c in un.cases)
                {
                    CollectTypeRefs(c.caseName, symbolName, uri, results);
                    foreach (var arg in c.arguments)
                        CollectTypeRefs(arg.type, symbolName, uri, results);
                }
            }

            // Attribute usages
            foreach (var def in file.Definitions)
            {
                foreach (var attr in def.Attributes)
                {
                    if (attr.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                        results.Add((uri, attr.Name));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Every occurrence of <paramref name="symbolName"/> inside one written type reference,
    /// <strong>at any nesting depth</strong>.
    /// </summary>
    /// <remarks>
    /// This used to look at each argument's head <c>Name</c> and stop. That found the
    /// <c>Array</c> of <c>Map&lt;string, Array&lt;User&gt;&gt;</c> and never the <c>User</c>, so
    /// find-references under-reported and — much worse — rename rewrote every reference it could
    /// see and left the nested ones behind, producing a schema that no longer compiled. Recursing
    /// through <see cref="IonTypeParameterSyntax.Type"/> reaches the whole tree.
    /// </remarks>
    private static void CollectTypeRefs(
        IonUnderlyingTypeSyntax type, string symbolName,
        string uri, List<(string, IonSyntaxBase)> results)
    {
        // An inline body's `Name` is the unlexable `$inline` placeholder before hoisting and the
        // synthesized derived name after it; neither is something the author can rename.
        if (!type.IsInline && type.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
            results.Add((uri, type.Name));

        foreach (var generic in type.generics)
        {
            if (generic.Type is { } written)
                CollectTypeRefs(written, symbolName, uri, results);
            else if (generic.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                results.Add((uri, generic.Name));
        }

        // The fields of an inline body are ordinary type sites. Only reachable before hoisting;
        // afterwards the body has become a top level msg and is walked as one.
        foreach (var field in type.InlineBody?.Fields ?? [])
            CollectTypeRefs(field.Type, symbolName, uri, results);
    }

    /// <summary>Occurrences of a name in a <c>with</c> clause.</summary>
    private static void CollectMixinRefs(
        List<IonIdentifier>? clause, string symbolName,
        string uri, List<(string, IonSyntaxBase)> results)
    {
        foreach (var written in clause ?? [])
            if (written.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                results.Add((uri, written));
    }

    /// <summary>
    /// Collect all symbol names known in the workspace for completion.
    /// </summary>
    public static List<CompletionItem> GetCompletionItems(IonWorkspace workspace)
    {
        var items = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Keywords
        foreach (var kw in new[]
        {
            ("msg", CompletionItemKind.Keyword, "Define a message type"),
            ("mixin", CompletionItemKind.Keyword, "Define a field-set template (not a type)"),
            ("with", CompletionItemKind.Keyword, "Include mixins into a msg or mixin"),
            ("service", CompletionItemKind.Keyword, "Define a service contract"),
            ("enum", CompletionItemKind.Keyword, "Define an enumeration"),
            ("flags", CompletionItemKind.Keyword, "Define a bitfield flags type"),
            ("union", CompletionItemKind.Keyword, "Define a discriminated union"),
            ("typedef", CompletionItemKind.Keyword, "Define a type alias"),
            ("attribute", CompletionItemKind.Keyword, "Define a custom attribute"),
            ("stream", CompletionItemKind.Keyword, "Stream modifier"),
            ("unary", CompletionItemKind.Keyword, "Unary modifier"),
            ("internal", CompletionItemKind.Keyword, "Internal modifier"),
        })
        {
            items.Add(new CompletionItem
            {
                Label = kw.Item1,
                Kind = kw.Item2,
                Detail = kw.Item3,
                SortText = $"0_{kw.Item1}" // keywords first
            });
        }

        // Builtin types. Kept in step with `IonModule.GetStdModule` — the list is written out
        // rather than read from the module because completion has to work before the project has
        // ever compiled, which is exactly when there is no resolved module to read.
        foreach (var bt in BuiltinTypeNames)
        {
            if (seen.Add(bt))
            {
                items.Add(new CompletionItem
                {
                    Label = bt,
                    Kind = CompletionItemKind.TypeParameter,
                    Detail = "Builtin type",
                    SortText = $"1_{bt}"
                });
            }
        }

        // The builtin generics. Offered with a snippet body because none of them is legal bare:
        // writing `Map` alone is ION0060, and `Maybe`/`Array`/`Partial` have suffix spellings
        // (`T?`, `T[]`, `T~`) that most authors want instead, while `Map` and `Set` have none and
        // must be written out.
        foreach (var (name, parameters, detail) in BuiltinGenerics)
        {
            if (!seen.Add(name))
                continue;

            var slots = string.Join(", ", parameters.Select((p, i) => $"${{{i + 1}:{p}}}"));

            items.Add(new CompletionItem
            {
                Label = name,
                Kind = CompletionItemKind.TypeParameter,
                InsertText = $"{name}<{slots}>",
                InsertTextFormat = InsertTextFormat.Snippet,
                Detail = detail,
                SortText = $"1_{name}"
            });
        }

        // User-defined types. Members live in their own namespace so that a field named
        // like a type does not get swallowed by the type-name dedupe set.
        var seenMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in workspace.ParsedFiles)
        {
            foreach (var msg in file.messageSyntaxes)
            {
                if (seen.Add(msg.Name.Identifier))
                    items.Add(WithDoc(new CompletionItem
                    {
                        Label = msg.Name.Identifier,
                        Kind = CompletionItemKind.Struct,
                        Detail = $"msg (in {file.Name})",
                        SortText = $"2_{msg.Name.Identifier}"
                    }, msg.Comments));

                foreach (var field in msg.Fields)
                    if (seenMembers.Add($"field:{field.Name.Identifier}"))
                        items.Add(WithDoc(new CompletionItem
                        {
                            Label = field.Name.Identifier,
                            Kind = CompletionItemKind.Field,
                            // FormatTypeSyntax, not `Type.Name.Identifier`: the head name alone
                            // renders `Map<string, User>` as `Map` and `f4[16]` as `f4`.
                            Detail = $"field of {msg.Name.Identifier}: {FormatTypeSyntax(field.Type)}",
                            SortText = $"3_{field.Name.Identifier}"
                        }, field.Comments));
            }

            // Mixins are offered as ordinary completions even though they are not types: a
            // `with` clause is a completion position too, and the dedicated clause list in
            // IonCompletionHandler only fires once `with ` is already typed.
            foreach (var mixin in file.mixinSyntaxes)
            {
                if (!seen.Add(mixin.Name.Identifier))
                    continue;

                var count = mixin.Fields.Count;

                items.Add(WithDoc(new CompletionItem
                {
                    Label = mixin.Name.Identifier,
                    Kind = CompletionItemKind.Interface,
                    Detail = $"mixin, {count} field{(count == 1 ? "" : "s")} (in {file.Name})",
                    SortText = $"2_{mixin.Name.Identifier}"
                }, mixin.Comments));
            }

            foreach (var svc in file.serviceSyntaxes)
            {
                if (seen.Add(svc.serviceName.Identifier))
                    items.Add(WithDoc(new CompletionItem
                    {
                        Label = svc.serviceName.Identifier,
                        Kind = CompletionItemKind.Interface,
                        Detail = $"service (in {file.Name})",
                        SortText = $"2_{svc.serviceName.Identifier}"
                    }, svc.Comments));

                foreach (var method in svc.Methods)
                    if (seenMembers.Add($"method:{method.methodName.Identifier}"))
                        items.Add(WithDoc(new CompletionItem
                        {
                            Label = method.methodName.Identifier,
                            Kind = CompletionItemKind.Method,
                            Detail = $"method of {svc.serviceName.Identifier}",
                            SortText = $"3_{method.methodName.Identifier}"
                        }, method.Comments));
            }

            foreach (var en in file.enumSyntaxes)
            {
                if (seen.Add(en.Name.Identifier))
                    items.Add(WithDoc(new CompletionItem
                    {
                        Label = en.Name.Identifier,
                        Kind = CompletionItemKind.Enum,
                        Detail = $"enum (in {file.Name})",
                        SortText = $"2_{en.Name.Identifier}"
                    }, en.Comments));

                foreach (var entry in en.Entries)
                    if (seenMembers.Add($"member:{en.Name.Identifier}.{entry.Name.Identifier}"))
                        items.Add(WithDoc(new CompletionItem
                        {
                            Label = entry.Name.Identifier,
                            Kind = CompletionItemKind.EnumMember,
                            Detail = $"{en.Name.Identifier} member",
                            SortText = $"3_{entry.Name.Identifier}"
                        }, entry.Comments));
            }

            foreach (var fl in file.flagsSyntaxes)
            {
                if (seen.Add(fl.Name.Identifier))
                    items.Add(WithDoc(new CompletionItem
                    {
                        Label = fl.Name.Identifier,
                        Kind = CompletionItemKind.Enum,
                        Detail = $"flags (in {file.Name})",
                        SortText = $"2_{fl.Name.Identifier}"
                    }, fl.Comments));

                foreach (var entry in fl.Entries)
                    if (seenMembers.Add($"member:{fl.Name.Identifier}.{entry.Name.Identifier}"))
                        items.Add(WithDoc(new CompletionItem
                        {
                            Label = entry.Name.Identifier,
                            Kind = CompletionItemKind.EnumMember,
                            Detail = $"{fl.Name.Identifier} flag",
                            SortText = $"3_{entry.Name.Identifier}"
                        }, entry.Comments));
            }

            foreach (var un in file.unionSyntaxes)
            {
                if (seen.Add(un.unionName.Identifier))
                    items.Add(WithDoc(new CompletionItem
                    {
                        Label = un.unionName.Identifier,
                        Kind = CompletionItemKind.Class,
                        Detail = $"union (in {file.Name})",
                        SortText = $"2_{un.unionName.Identifier}"
                    }, un.Comments));

                foreach (var c in un.cases)
                    if (seenMembers.Add($"case:{un.unionName.Identifier}.{c.caseName.Name.Identifier}"))
                        items.Add(WithDoc(new CompletionItem
                        {
                            Label = c.caseName.Name.Identifier,
                            Kind = CompletionItemKind.EnumMember,
                            Detail = $"case of union {un.unionName.Identifier}",
                            SortText = $"3_{c.caseName.Name.Identifier}"
                        }, c.Comments));
            }

            foreach (var td in file.typedefSyntaxes)
                if (seen.Add(td.TypeName.Name.Identifier))
                    items.Add(WithDoc(new CompletionItem
                    {
                        Label = td.TypeName.Name.Identifier,
                        Kind = CompletionItemKind.TypeParameter,
                        Detail = $"typedef (in {file.Name})",
                        SortText = $"2_{td.TypeName.Name.Identifier}"
                    }, td.Comments));

            foreach (var attr in file.attributeDefSyntaxes)
                if (seenMembers.Add($"attr:{attr.Name.Identifier}"))
                    items.Add(WithDoc(new CompletionItem
                    {
                        Label = $"@{attr.Name.Identifier}",
                        FilterText = attr.Name.Identifier,
                        InsertText = attr.Name.Identifier,
                        Kind = CompletionItemKind.Property,
                        Detail = $"attribute (in {file.Name})",
                        SortText = $"3_{attr.Name.Identifier}"
                    }, attr.Comments));
        }

        return items;
    }

    /// <summary>
    /// Attaches the symbol's doc comment as markdown documentation.
    /// A null / empty doc leaves the item exactly as it was.
    /// </summary>
    public static CompletionItem WithDoc(CompletionItem item, string? doc)
    {
        var markup = IonDocMarkdown.ToMarkupContent(doc);
        return markup is null ? item : item with { Documentation = markup };
    }
}
