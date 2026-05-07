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

            // Type references in message fields
            foreach (var msg in file.messageSyntaxes)
            {
                foreach (var field in msg.Fields)
                {
                    CollectTypeRefs(field.Type, symbolName, uri, results);
                }
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

    private static void CollectTypeRefs(
        IonUnderlyingTypeSyntax type, string symbolName,
        string uri, List<(string, IonSyntaxBase)> results)
    {
        if (type.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
            results.Add((uri, type.Name));

        foreach (var generic in type.generics)
        {
            if (generic.Name.Identifier.Equals(symbolName, StringComparison.OrdinalIgnoreCase))
                results.Add((uri, generic.Name));
        }
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

        // Builtin types
        foreach (var bt in new[]
        {
            "i1", "i2", "i4", "i8", "i16",
            "u1", "u2", "u4", "u8", "u16",
            "f2", "f4", "f8",
            "bool", "void", "string", "bytes", "guid",
            "datetime", "dateonly", "timeonly", "duration", "uri", "bigint"
        })
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

        // User-defined types
        foreach (var file in workspace.ParsedFiles)
        {
            foreach (var msg in file.messageSyntaxes)
                if (seen.Add(msg.Name.Identifier))
                    items.Add(new CompletionItem
                    {
                        Label = msg.Name.Identifier,
                        Kind = CompletionItemKind.Struct,
                        Detail = $"msg (in {file.Name})",
                        SortText = $"2_{msg.Name.Identifier}"
                    });

            foreach (var svc in file.serviceSyntaxes)
                if (seen.Add(svc.serviceName.Identifier))
                    items.Add(new CompletionItem
                    {
                        Label = svc.serviceName.Identifier,
                        Kind = CompletionItemKind.Interface,
                        Detail = $"service (in {file.Name})",
                        SortText = $"2_{svc.serviceName.Identifier}"
                    });

            foreach (var en in file.enumSyntaxes)
                if (seen.Add(en.Name.Identifier))
                    items.Add(new CompletionItem
                    {
                        Label = en.Name.Identifier,
                        Kind = CompletionItemKind.Enum,
                        Detail = $"enum (in {file.Name})",
                        SortText = $"2_{en.Name.Identifier}"
                    });

            foreach (var fl in file.flagsSyntaxes)
                if (seen.Add(fl.Name.Identifier))
                    items.Add(new CompletionItem
                    {
                        Label = fl.Name.Identifier,
                        Kind = CompletionItemKind.Enum,
                        Detail = $"flags (in {file.Name})",
                        SortText = $"2_{fl.Name.Identifier}"
                    });

            foreach (var un in file.unionSyntaxes)
                if (seen.Add(un.unionName.Identifier))
                    items.Add(new CompletionItem
                    {
                        Label = un.unionName.Identifier,
                        Kind = CompletionItemKind.Class,
                        Detail = $"union (in {file.Name})",
                        SortText = $"2_{un.unionName.Identifier}"
                    });

            foreach (var td in file.typedefSyntaxes)
                if (seen.Add(td.TypeName.Name.Identifier))
                    items.Add(new CompletionItem
                    {
                        Label = td.TypeName.Name.Identifier,
                        Kind = CompletionItemKind.TypeParameter,
                        Detail = $"typedef (in {file.Name})",
                        SortText = $"2_{td.TypeName.Name.Identifier}"
                    });
        }

        return items;
    }
}
