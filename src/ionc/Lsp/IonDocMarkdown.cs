namespace ion.compiler.Lsp;

using System.Text;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

/// <summary>
/// Turns raw Ion doc-comment text (as captured on <c>IonSyntaxMember.Comments</c> /
/// the semantic <c>Doc</c> properties) into markdown that is safe to hand to an LSP client.
/// <para>
/// Every entry point returns <see langword="null"/> for null / whitespace-only input so that
/// callers can keep their pre-existing "no doc" output byte-for-byte identical.
/// </para>
/// </summary>
public static class IonDocMarkdown
{
    private const int SingleLineBudget = 120;

    /// <summary>
    /// Trims trailing whitespace, drops blank leading/trailing lines and dedents the block.
    /// Returns <see langword="null"/> when nothing is left.
    /// <para>
    /// Comment leaders (<c>//</c>, <c>///</c>, <c>//!</c>, the <c>*</c> of a <c>/** */</c>
    /// continuation line) are already removed by the parser, so they are deliberately *not*
    /// stripped again here — doing so would eat legitimate markdown such as a <c>* item</c>
    /// bullet or a leading <c>!</c> image.
    /// </para>
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cleaned = new List<string>(lines.Length);

        foreach (var line in lines)
            cleaned.Add(line.TrimEnd());

        // Trim leading / trailing blank lines.
        var start = 0;
        var end = cleaned.Count - 1;
        while (start <= end && cleaned[start].Length == 0) start++;
        while (end >= start && cleaned[end].Length == 0) end--;
        if (start > end) return null;

        cleaned = cleaned.GetRange(start, end - start + 1);

        // Dedent by the common leading-space prefix so fenced code blocks keep their shape.
        var common = int.MaxValue;
        foreach (var line in cleaned)
        {
            if (line.Length == 0) continue;
            var n = 0;
            while (n < line.Length && line[n] == ' ') n++;
            if (n < common) common = n;
            if (common == 0) break;
        }
        if (common is > 0 and < int.MaxValue)
        {
            for (var i = 0; i < cleaned.Count; i++)
                if (cleaned[i].Length >= common)
                    cleaned[i] = cleaned[i][common..];
        }

        var text = string.Join("\n", cleaned);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Normalizes and neutralizes markdown constructs that would break out of the hover layout
    /// (raw HTML, unbalanced code spans, headings, thematic breaks) while leaving ordinary
    /// emphasis / lists / code fences intact. Line breaks are preserved as markdown hard breaks.
    /// </summary>
    public static string? ToMarkdown(string? raw)
    {
        var text = Normalize(raw);
        if (text is null) return null;

        // An odd number of backticks would swallow everything after it.
        var escapeTicks = text.Count(c => c == '`') % 2 != 0;

        var lines = text.Split('\n');
        var safe = new string[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // No raw HTML.
            line = line.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

            if (escapeTicks)
                line = line.Replace("`", "\\`");

            var trimmed = line.TrimStart();
            var pad = line[..(line.Length - trimmed.Length)];

            // A heading would blow out the hover; a rule collides with our own `---` separator
            // and can retroactively turn the previous line into a setext heading.
            if (trimmed.StartsWith('#') || IsThematicBreak(trimmed))
                line = pad + "\\" + trimmed;

            // A trailing backslash would escape the hard line break we append below.
            if (line.EndsWith('\\'))
                line += "\\";

            safe[i] = line;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < safe.Length; i++)
        {
            sb.Append(safe[i]);
            if (i == safe.Length - 1) continue;
            sb.Append(safe[i].Length > 0 && safe[i + 1].Length > 0 ? "  \n" : "\n");
        }

        return sb.ToString();
    }

    private static bool IsThematicBreak(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        var marker = trimmed[0];
        if (marker is not ('-' or '=' or '_' or '*')) return false;
        var count = 0;
        foreach (var c in trimmed)
        {
            if (c == marker) count++;
            else if (!char.IsWhiteSpace(c)) return false;
        }
        return count >= 3;
    }

    /// <summary>
    /// Flattens a doc comment to a single plain-text line, for protocol fields such as
    /// <c>CompletionItem.Detail</c> and <c>DocumentSymbol.Detail</c> that are not markdown.
    /// </summary>
    public static string? ToSingleLine(string? raw, int maxLength = SingleLineBudget)
    {
        var text = Normalize(raw);
        if (text is null) return null;

        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }

        var flat = sb.ToString();
        if (flat.Length == 0) return null;
        if (flat.Length > maxLength)
            flat = flat[..Math.Max(1, maxLength - 1)].TrimEnd() + "…";
        return flat;
    }

    /// <summary>
    /// Appends the <c>---</c> separator and the rendered doc to a hover section list.
    /// A null / empty doc appends nothing at all.
    /// </summary>
    public static void AppendSection(List<string> sections, string? raw)
    {
        var md = ToMarkdown(raw);
        if (md is null) return;
        sections.Add("---");
        sections.Add(md);
    }

    /// <summary>
    /// Markdown <see cref="MarkupContent"/> for <c>Documentation</c> fields, or null when empty.
    /// </summary>
    public static StringOrMarkupContent? ToMarkupContent(string? raw)
    {
        var md = ToMarkdown(raw);
        return md is null
            ? null
            : new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = md });
    }

    /// <summary>
    /// Markdown <see cref="MarkupContent"/> that prepends the doc (when present) above an
    /// always-present synthesized detail line, separated by <c>---</c>.
    /// </summary>
    public static StringOrMarkupContent WithDoc(string? raw, string detail)
    {
        var md = ToMarkdown(raw);
        var value = md is null ? detail : $"{md}\n\n---\n\n{detail}";
        return new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = value });
    }
}
