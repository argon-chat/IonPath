namespace ion.compiler.CodeGen;

using System.Text;

/// <summary>
/// A single documented parameter (method argument, record positional field, ...).
/// </summary>
/// <param name="Name">Identifier exactly as it appears in the generated signature.</param>
/// <param name="Doc">Raw documentation text, or <c>null</c> when undocumented.</param>
public readonly record struct DocParam(string Name, string? Doc);

/// <summary>
/// Shared normalization and escaping for the documentation comments emitted by every Ion
/// code generator (C#, TypeScript, Rust).
/// <para>
/// Every formatter here obeys the same contract: a <c>null</c>, empty or whitespace-only
/// document produces the empty string — never a blank line, never an empty comment marker.
/// A non-empty document produces a block in which <em>every</em> line carries the requested
/// indent and which is terminated by a newline, so call sites can simply
/// <c>sb.Append(...)</c> the result without branching.
/// </para>
/// </summary>
public static class DocCommentFormatter
{
    private static readonly string[] LineSeparators = ["\r\n", "\n", "\r"];

    // ═══════════════════════════════════════════════════════════════════
    // NORMALIZATION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Splits raw doc text into display lines: handles CRLF/LF/CR, strips trailing
    /// whitespace, drops leading and trailing blank lines (interior blank lines are
    /// preserved as paragraph breaks) and removes the common leading indentation.
    /// Returns <c>null</c> when nothing would be emitted.
    /// </summary>
    public static IReadOnlyList<string>? Normalize(string? doc)
    {
        if (string.IsNullOrWhiteSpace(doc))
            return null;

        var raw = doc.Split(LineSeparators, StringSplitOptions.None);
        var lines = new List<string>(raw.Length);
        foreach (var line in raw)
            lines.Add(line.TrimEnd());

        var start = 0;
        var end = lines.Count - 1;
        while (start <= end && lines[start].Length == 0) start++;
        while (end >= start && lines[end].Length == 0) end--;
        if (start > end)
            return null;

        var slice = lines.GetRange(start, end - start + 1);
        Dedent(slice);
        return slice;
    }

    /// <summary>
    /// True when <paramref name="doc"/> would produce no output at all.
    /// </summary>
    public static bool IsEmpty(string? doc) => Normalize(doc) is null;

    private static void Dedent(List<string> lines)
    {
        var common = int.MaxValue;

        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;

            var n = 0;
            while (n < line.Length && (line[n] == ' ' || line[n] == '\t'))
                n++;

            if (n < common)
                common = n;
            if (common == 0)
                return;
        }

        if (common is 0 or int.MaxValue)
            return;

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length != 0)
                lines[i] = lines[i][common..];
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ESCAPING
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Escapes the three characters that would otherwise break a C# XML doc comment.
    /// </summary>
    public static string XmlEscape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Neutralizes any <c>*/</c> inside doc text so it cannot terminate a JSDoc block early.
    /// </summary>
    public static string JsDocEscape(string text)
        => text.Replace("*/", "*\\/");

    // ═══════════════════════════════════════════════════════════════════
    // C# — XML DOCUMENTATION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a C# XML documentation block:
    /// <c>&lt;summary&gt;</c>, then <c>&lt;param&gt;</c> for every documented parameter,
    /// then <c>&lt;returns&gt;</c> when return documentation is supplied.
    /// </summary>
    public static string CSharpDoc(
        string? doc,
        string indent = "",
        IReadOnlyList<DocParam>? parameters = null,
        string? returns = null)
    {
        var summary = Normalize(doc);
        var documentedParams = CollectDocumented(parameters);
        var returnLines = Normalize(returns);

        if (summary is null && documentedParams is null && returnLines is null)
            return string.Empty;

        var sb = new StringBuilder();

        if (summary is not null)
        {
            sb.AppendLine($"{indent}/// <summary>");
            foreach (var line in summary)
                sb.AppendLine(line.Length == 0 ? $"{indent}///" : $"{indent}/// {XmlEscape(line)}");
            sb.AppendLine($"{indent}/// </summary>");
        }

        if (documentedParams is not null)
        {
            foreach (var (name, lines) in documentedParams)
                AppendXmlTag(sb, indent, $"param name=\"{XmlEscape(name)}\"", "param", lines);
        }

        if (returnLines is not null)
            AppendXmlTag(sb, indent, "returns", "returns", returnLines);

        return sb.ToString();
    }

    private static void AppendXmlTag(
        StringBuilder sb,
        string indent,
        string openTag,
        string closeTag,
        IReadOnlyList<string> lines)
    {
        if (lines.Count == 1)
        {
            sb.AppendLine($"{indent}/// <{openTag}>{XmlEscape(lines[0])}</{closeTag}>");
            return;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var text = XmlEscape(lines[i]);
            if (i == 0)
                sb.AppendLine($"{indent}/// <{openTag}>{text}");
            else if (i == lines.Count - 1)
                sb.AppendLine($"{indent}/// {text}</{closeTag}>");
            else
                sb.AppendLine(text.Length == 0 ? $"{indent}///" : $"{indent}/// {text}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // TYPESCRIPT — JSDOC
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a JSDoc block with an optional <c>@param</c> tag per documented parameter.
    /// </summary>
    /// <param name="doc">Raw documentation text, or <c>null</c> when undocumented.</param>
    /// <param name="indent">Indent carried by every emitted line.</param>
    /// <param name="parameters">Parameters to emit a <c>@param</c> tag for; undocumented ones are skipped.</param>
    /// <param name="returns">Return documentation, emitted as <c>@returns</c>.</param>
    /// <param name="deprecated">
    /// The text of a <c>@deprecated</c> tag, or <see langword="null"/> when the declaration is not
    /// deprecated. The empty string is <em>not</em> the same as <see langword="null"/>: it emits a
    /// bare <c>@deprecated</c>, which is what <c>@deprecated</c> with no arguments means and what
    /// every editor already strikes through. The tag is emitted last, after <c>@param</c> and
    /// <c>@returns</c>, and a block is produced even when there is nothing else to document — so a
    /// deprecated but undocumented declaration still gets exactly one comment, never a second
    /// detached one.
    /// </param>
    public static string JsDoc(
        string? doc,
        string indent = "",
        IReadOnlyList<DocParam>? parameters = null,
        string? returns = null,
        string? deprecated = null)
    {
        var summary = Normalize(doc);
        var documentedParams = CollectDocumented(parameters);
        var returnLines = Normalize(returns);

        if (summary is null && documentedParams is null && returnLines is null && deprecated is null)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"{indent}/**");

        if (summary is not null)
        {
            foreach (var line in summary)
                AppendJsDocLine(sb, indent, line);
        }

        if (documentedParams is not null || returnLines is not null || deprecated is not null)
        {
            if (summary is not null)
                sb.AppendLine($"{indent} *");

            if (documentedParams is not null)
            {
                foreach (var (name, lines) in documentedParams)
                    AppendJsDocTag(sb, indent, $"@param {name}", lines);
            }

            if (returnLines is not null)
                AppendJsDocTag(sb, indent, "@returns", returnLines);

            if (deprecated is not null)
                sb.AppendLine(deprecated.Length == 0
                    ? $"{indent} * @deprecated"
                    : $"{indent} * @deprecated {deprecated}");
        }

        sb.AppendLine($"{indent} */");
        return sb.ToString();
    }

    private static void AppendJsDocLine(StringBuilder sb, string indent, string line)
        => sb.AppendLine(line.Length == 0 ? $"{indent} *" : $"{indent} * {JsDocEscape(line)}");

    private static void AppendJsDocTag(StringBuilder sb, string indent, string tag, IReadOnlyList<string> lines)
    {
        sb.AppendLine($"{indent} * {tag} {JsDocEscape(lines[0])}");
        for (var i = 1; i < lines.Count; i++)
            sb.AppendLine(lines[i].Length == 0 ? $"{indent} *" : $"{indent} *   {JsDocEscape(lines[i])}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // RUST — RUSTDOC
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a rustdoc <c>///</c> block. Rustdoc is markdown, so the text is emitted
    /// verbatim; documented parameters become a standard <c># Arguments</c> section.
    /// </summary>
    public static string RustDoc(
        string? doc,
        string indent = "",
        IReadOnlyList<DocParam>? parameters = null)
    {
        var summary = Normalize(doc);
        var documentedParams = CollectDocumented(parameters);

        if (summary is null && documentedParams is null)
            return string.Empty;

        var sb = new StringBuilder();

        if (summary is not null)
        {
            foreach (var line in summary)
                AppendPrefixed(sb, indent, "///", line);
        }

        if (documentedParams is not null)
        {
            if (summary is not null)
                sb.AppendLine($"{indent}///");

            sb.AppendLine($"{indent}/// # Arguments");
            sb.AppendLine($"{indent}///");

            foreach (var (name, lines) in documentedParams)
            {
                sb.AppendLine($"{indent}/// * `{name}` - {lines[0]}");
                for (var i = 1; i < lines.Count; i++)
                    sb.AppendLine(lines[i].Length == 0 ? $"{indent}///" : $"{indent}///   {lines[i]}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds an inner rustdoc block (<c>//!</c>) for module/crate level documentation.
    /// </summary>
    public static string RustModuleDoc(string? doc, string indent = "")
    {
        var lines = Normalize(doc);
        if (lines is null)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var line in lines)
            AppendPrefixed(sb, indent, "//!", line);
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // PLAIN LINE COMMENTS (file/module banners)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a plain <c>//</c> comment block — used for file level documentation in
    /// languages where no declaration exists to attach a doc comment to.
    /// </summary>
    public static string LineComment(string? doc, string indent = "")
    {
        var lines = Normalize(doc);
        if (lines is null)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var line in lines)
            AppendPrefixed(sb, indent, "//", line);
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // INTERNALS
    // ═══════════════════════════════════════════════════════════════════

    private static void AppendPrefixed(StringBuilder sb, string indent, string marker, string line)
        => sb.AppendLine(line.Length == 0 ? $"{indent}{marker}" : $"{indent}{marker} {line}");

    private static List<(string Name, IReadOnlyList<string> Lines)>? CollectDocumented(
        IReadOnlyList<DocParam>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return null;

        List<(string, IReadOnlyList<string>)>? result = null;

        foreach (var p in parameters)
        {
            var lines = Normalize(p.Doc);
            if (lines is null)
                continue;
            result ??= [];
            result.Add((p.Name, lines));
        }

        return result;
    }
}
