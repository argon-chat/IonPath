namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

/// <summary>
/// Comment / trivia layer of the Ion grammar.
/// <code>
/// // text        ordinary line comment      pure trivia
/// /// text       doc comment                attaches to the next declaration / member
/// //! text       module doc comment         collected into IonFileSyntax.ModuleDoc
/// /* ... */      ordinary block comment     pure trivia, non nesting
/// /** ... */     doc block comment          attaches to the next declaration / member
/// </code>
/// <c>/**/</c> is an ordinary (empty) block comment, <c>////</c> is an ordinary line comment.
/// </summary>
public partial class IonParser
{
    internal enum IonTriviaKind
    {
        Whitespace,
        LineComment,
        DocComment,
        ModuleDocComment,
        BlockComment,
        DocBlockComment
    }

    internal readonly record struct IonTrivia(IonTriviaKind Kind, string Text)
    {
        /// <summary>Trivia that attaches to the following declaration/member.</summary>
        public bool IsDoc => Kind is IonTriviaKind.DocComment or IonTriviaKind.DocBlockComment;

        /// <summary>File level <c>//!</c> documentation.</summary>
        public bool IsModuleDoc => Kind is IonTriviaKind.ModuleDocComment;
    }

    #region raw pieces

    /// <summary>Everything up to (but excluding) the next line break. Never crosses a newline.</summary>
    private static Parser<char, string> RestOfLine =>
        AnyCharExcept('\r', '\n').ManyString();

    /// <summary>
    /// Body of a block comment, starting after the opening <c>/*</c>.
    /// Non nesting: terminated by the first <c>*/</c>. An unterminated block comment
    /// consumes the rest of the input instead of failing/hanging.
    /// </summary>
    private static Parser<char, string> BlockCommentBody =>
        Any.Until(Try(String("*/")).ThenReturn(Unit.Value).Or(End))
            .Select(cs => new string(cs.ToArray()));

    /// <summary>Line comment tail, starting after the leading <c>//</c>.</summary>
    private static Parser<char, IonTrivia> LineCommentTail =>
        OneOf(
            // "//!" -> module doc
            Char('!')
                .Then(RestOfLine)
                .Select(t => new IonTrivia(IonTriviaKind.ModuleDocComment, NormalizeDocLine(t))),
            // exactly one extra slash -> "///" doc comment; zero or 2+ -> ordinary comment
            Map(
                (slashes, text) => slashes.Length == 1
                    ? new IonTrivia(IonTriviaKind.DocComment, NormalizeDocLine(text))
                    : new IonTrivia(IonTriviaKind.LineComment, text),
                Char('/').ManyString(),
                RestOfLine));

    /// <summary>Block comment tail, starting after the leading <c>/*</c>.</summary>
    private static Parser<char, IonTrivia> BlockCommentTail =>
        OneOf(
            // "/**" followed by something other than '/' -> doc block ("/**/" stays an ordinary comment)
            Try(Char('*').Then(Lookahead(AnyCharExcept('/'))))
                .Then(BlockCommentBody)
                .Select(t => new IonTrivia(IonTriviaKind.DocBlockComment, NormalizeDocBlock(t))),
            BlockCommentBody.Select(t => new IonTrivia(IonTriviaKind.BlockComment, t)));

    /// <summary>
    /// Any comment. Atomic: consumes nothing when the input does not start a comment,
    /// so it is safe inside <c>Many</c>/<c>OneOf</c>.
    /// </summary>
    private static Parser<char, IonTrivia> CommentTrivia =>
        Try(Char('/').Then(OneOf(
                Char('/').Then(LineCommentTail),
                Char('*').Then(BlockCommentTail))))
            .Labelled("comment");

    /// <summary>
    /// Whitespace, plus U+FEFF. <c>char.IsWhiteSpace('﻿')</c> is <see langword="false"/> on .NET,
    /// so a UTF-8 BOM would otherwise turn the whole file into an <see cref="InvalidIonBlock"/>.
    /// Treating it as trivia (rather than stripping it at the entry point) keeps every
    /// <see cref="SourcePos"/> aligned with what the editor sees.
    /// </summary>
    private static Parser<char, IonTrivia> WhitespaceTrivia =>
        OneOf(Whitespace, Char('﻿')).AtLeastOnce()
            .ThenReturn(new IonTrivia(IonTriviaKind.Whitespace, string.Empty));

    #endregion

    #region trivia runs

    /// <summary>
    /// Every kind of trivia, including doc comments. Used at declaration/member start
    /// (where the doc is captured) and to flush dangling comments before a closing token.
    /// </summary>
    private static Parser<char, IEnumerable<IonTrivia>> TriviaRun =>
        OneOf(WhitespaceTrivia, CommentTrivia).Many();

    /// <summary>
    /// Inter token trivia inside a declaration: whitespace, <c>//</c>, <c>//!</c> and <c>/* */</c>.
    /// Deliberately stops in front of <c>///</c> and <c>/** */</c> so that a doc comment is never
    /// swallowed as trailing trivia of the previous construct.
    /// This is the replacement for the bare <c>SkipWhitespaces</c> that used to be everywhere.
    /// </summary>
    private static Parser<char, Unit> SkipTrivia =>
        OneOf(WhitespaceTrivia, Try(CommentTrivia.Assert(t => !t.IsDoc))).SkipMany();

    /// <summary>
    /// Top level variant of <see cref="SkipTrivia"/>: additionally stops in front of a
    /// module doc (<c>//!</c>) so it can be collected into the file syntax.
    /// </summary>
    private static Parser<char, Unit> SkipTopLevelTrivia =>
        OneOf(WhitespaceTrivia, Try(CommentTrivia.Assert(t => !t.IsDoc && !t.IsModuleDoc))).SkipMany();

    /// <summary>
    /// Skips absolutely all trivia, including dangling doc comments.
    /// Used immediately before <c>}</c> / <c>)</c> and at end of file.
    /// </summary>
    private static Parser<char, Unit> SkipTriviaAll =>
        OneOf(WhitespaceTrivia, CommentTrivia).SkipMany();

    /// <summary>A single <c>//!</c> line plus following non-doc trivia.</summary>
    private static Parser<char, string> ModuleDocLine =>
        Try(CommentTrivia.Assert(t => t.IsModuleDoc))
            .Select(t => t.Text)
            .Before(SkipTopLevelTrivia);

    #endregion

    #region leading section (doc comments + attributes, interleaved)

    /// <summary>Doc comment and attributes preceding a declaration or member.</summary>
    internal readonly record struct IonLeading(string? Doc, List<IonAttributeSyntax> Attributes)
    {
        public static readonly IonLeading Empty = new(null, []);
    }

    /// <summary>
    /// The set of characters that may begin a declaration or a member:
    /// <c>@</c> (attribute), <c>#</c> (directive), or the first character of an identifier/keyword.
    /// Explicitly excludes <c>}</c>, <c>)</c>, <c>,</c> and end of input.
    /// </summary>
    private static Parser<char, char> MemberStartChar =>
        OneOf(Char('@'), Char('#'), Char('_'), Letter);

    /// <summary>
    /// Leading doc comments and attributes in any order (<c>@a /// doc msg X</c> and
    /// <c>/// doc @a msg X</c> are both accepted; all doc lines are merged).
    /// <para>
    /// The leading trivia is only committed when something that can actually start a
    /// declaration/member follows. That is what keeps a dangling comment in front of a
    /// <c>}</c>/<c>)</c> or at end of file from killing the enclosing <c>Many</c>/<c>Separated</c>.
    /// </para>
    /// </summary>
    private static Parser<char, IonLeading> LeadingSection =>
        Map(
            BuildLeading,
            Try(TriviaRun.Before(Lookahead(MemberStartChar))),
            Map((attr, trivia) => (Attr: attr, Trivia: trivia), Attribute, TriviaRun).Many());

    private static IonLeading BuildLeading(
        IEnumerable<IonTrivia> lead,
        IEnumerable<(IonAttributeSyntax Attr, IEnumerable<IonTrivia> Trivia)> tail)
    {
        var docLines = new List<string>();
        var attributes = new List<IonAttributeSyntax>();

        CollectDocLines(lead, docLines);

        foreach (var (attr, trivia) in tail)
        {
            attributes.Add(attr);
            CollectDocLines(trivia, docLines);
        }

        return new IonLeading(NormalizeDocLines(docLines), attributes);
    }

    /// <summary>Applies a hoisted <see cref="IonLeading"/> to a declaration parsed without one.</summary>
    private static Parser<char, T> WithLeading<T>(Parser<char, T> core) where T : IonSyntaxMember =>
        Map(
            (lead, value) => value.WithComments(lead.Doc).WithAttributes(lead.Attributes),
            LeadingSection,
            core);

    #endregion

    #region doc text normalization

    private static void CollectDocLines(IEnumerable<IonTrivia> trivia, List<string> sink)
    {
        foreach (var t in trivia)
        {
            if (!t.IsDoc)
                continue;
            if (t.Kind == IonTriviaKind.DocComment)
                sink.Add(t.Text);
            else
                sink.AddRange(t.Text.Split('\n'));
        }
    }

    private static string? ExtractModuleDoc(IEnumerable<IonTrivia> trivia)
    {
        List<string>? lines = null;
        foreach (var t in trivia)
        {
            if (!t.IsModuleDoc)
                continue;
            lines ??= [];
            lines.Add(t.Text);
        }

        return NormalizeDocLines(lines);
    }

    /// <summary>Strips at most one leading space and all trailing whitespace of a doc line.</summary>
    private static string NormalizeDocLine(string text)
    {
        if (text.StartsWith(' '))
            text = text[1..];
        return text.TrimEnd();
    }

    /// <summary>
    /// Normalizes the body of a <c>/** ... */</c> block: strips the leading <c>*</c> of every
    /// continuation line plus at most one following space, trims trailing whitespace and
    /// drops leading/trailing blank lines. Interior blank lines are paragraph breaks and kept.
    /// </summary>
    private static string NormalizeDocBlock(string body)
    {
        var rawLines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lines = new List<string>(rawLines.Length);

        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i];
            if (i == 0)
            {
                if (line.StartsWith(' '))
                    line = line[1..];
            }
            else
            {
                line = line.TrimStart(' ', '\t');
                if (line.StartsWith('*'))
                {
                    line = line[1..];
                    if (line.StartsWith(' '))
                        line = line[1..];
                }
            }

            lines.Add(line.TrimEnd());
        }

        return NormalizeDocLines(lines) ?? string.Empty;
    }

    /// <summary>
    /// Joins doc lines with <c>\n</c>, dropping leading and trailing blank lines.
    /// Returns <see langword="null"/> when there is no doc text at all.
    /// </summary>
    private static string? NormalizeDocLines(List<string>? lines)
    {
        if (lines is null || lines.Count == 0)
            return null;

        var start = 0;
        var end = lines.Count - 1;
        while (start <= end && lines[start].Length == 0) start++;
        while (end >= start && lines[end].Length == 0) end--;

        if (start > end)
            return null;

        return string.Join("\n", lines.GetRange(start, end - start + 1));
    }

    /// <summary>
    /// Removes <c>//</c> and <c>/* */</c> comments from a raw text span while respecting
    /// double quoted strings. Used to clean attribute argument spans before splitting on <c>,</c>.
    /// </summary>
    internal static string StripComments(string raw)
    {
        if (raw.IndexOf('/') < 0)
            return raw;

        var sb = new System.Text.StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];

            if (c == '"')
            {
                var start = i++;
                while (i < raw.Length && raw[i] != '"') i++;
                if (i < raw.Length) i++;
                sb.Append(raw, start, i - start);
                continue;
            }

            if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '/')
            {
                while (i < raw.Length && raw[i] != '\n' && raw[i] != '\r') i++;
                continue;
            }

            if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < raw.Length && !(raw[i] == '*' && raw[i + 1] == '/')) i++;
                i = i + 1 < raw.Length ? i + 2 : raw.Length;
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    #endregion

    #region raw (source preserving) scanners, used by error recovery

    /// <summary>A comment, returned as (approximately) its raw source text.</summary>
    private static Parser<char, string> RawComment =>
        OneOf(
            Try(Try(String("//")).Then(RestOfLine).Select(t => "//" + t)),
            Try(Try(String("/*"))
                .Then(Any.ManyThen(Try(String("*/")).Or(End.ThenReturn(string.Empty))))
                .Select(t => "/*" + new string(t.Item1.ToArray()) + t.Item2)));

    /// <summary>A double quoted string literal, returned as its raw source text.</summary>
    private static Parser<char, string> RawStringLiteral =>
        Try(Map(
            (_, body, _) => "\"" + body + "\"",
            Char('"'),
            AnyCharExcept('"').ManyString(),
            Char('"')));

    #endregion
}
