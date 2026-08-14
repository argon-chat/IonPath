namespace ion.compiler.Lsp;

/// <summary>
/// Comment forms recognized by the Ion grammar.
/// </summary>
public enum IonCommentKind
{
    /// <summary><c>// ...</c></summary>
    Line,

    /// <summary><c>/// ...</c> — documentation attached to the following declaration.</summary>
    DocLine,

    /// <summary><c>//! ...</c> — module level documentation.</summary>
    ModuleDoc,

    /// <summary><c>/* ... */</c></summary>
    Block,

    /// <summary><c>/** ... */</c> — documentation attached to the following declaration.</summary>
    DocBlock,
}

/// <summary>
/// A single comment occurrence. Positions are 0-based; <see cref="EndChar"/> is exclusive.
/// </summary>
public readonly record struct IonCommentSpan(
    int StartLine,
    int StartChar,
    int EndLine,
    int EndChar,
    IonCommentKind Kind)
{
    public bool IsDoc => Kind is IonCommentKind.DocLine or IonCommentKind.ModuleDoc or IonCommentKind.DocBlock;
    public bool IsBlock => Kind is IonCommentKind.Block or IonCommentKind.DocBlock;
    public bool IsMultiLine => EndLine > StartLine;
}

/// <summary>
/// Lexical classification of a single character.
/// </summary>
public enum IonCharClass : byte
{
    Code = 0,
    Comment = 1,
    String = 2,
}

/// <summary>
/// Result of a lexical pre-pass over a document: per-character classification plus the
/// list of comment spans. Handlers use this instead of ad-hoc <c>Contains("//")</c> /
/// <c>Contains("/*")</c> text scanning, which false-positives inside string literals.
/// </summary>
public sealed class IonScannedDocument
{
    /// <summary>Raw lines, split on '\n'. A trailing '\r' (CRLF files) is kept.</summary>
    public required string[] Lines { get; init; }

    /// <summary>Per line, per character classification. Same length as the raw line.</summary>
    public required IonCharClass[][] Mask { get; init; }

    /// <summary>Every comment found in the document, in source order.</summary>
    public required IReadOnlyList<IonCommentSpan> Comments { get; init; }

    /// <summary>True when the line begins already inside an unterminated block comment.</summary>
    public required bool[] OpensInsideBlockComment { get; init; }

    public IonCharClass ClassAt(int line, int character)
    {
        if (line < 0 || line >= Mask.Length) return IonCharClass.Code;
        var row = Mask[line];
        if (character < 0 || character >= row.Length) return IonCharClass.Code;
        return row[character];
    }

    /// <summary>True when the given position sits inside a comment or a string literal.</summary>
    public bool IsCommentOrString(int line, int character)
        => ClassAt(line, character) != IonCharClass.Code;

    /// <summary>Length of the line with any trailing '\r' removed.</summary>
    public int VisualLength(int line)
    {
        if (line < 0 || line >= Lines.Length) return 0;
        var len = Lines[line].Length;
        while (len > 0 && Lines[line][len - 1] == '\r') len--;
        return len;
    }

    /// <summary>Index of the first non-whitespace character classified as code, or -1.</summary>
    public int FirstCodeChar(int line) => FindCodeChar(line, forward: true);

    /// <summary>Index of the last non-whitespace character classified as code, or -1.</summary>
    public int LastCodeChar(int line) => FindCodeChar(line, forward: false);

    private int FindCodeChar(int line, bool forward)
    {
        if (line < 0 || line >= Lines.Length) return -1;
        var text = Lines[line];
        var row = Mask[line];
        if (forward)
        {
            for (var i = 0; i < text.Length; i++)
                if (row[i] == IonCharClass.Code && !char.IsWhiteSpace(text[i]))
                    return i;
        }
        else
        {
            for (var i = text.Length - 1; i >= 0; i--)
                if (row[i] == IonCharClass.Code && !char.IsWhiteSpace(text[i]))
                    return i;
        }
        return -1;
    }

    /// <summary>True when the line carries no code and no string literal — only whitespace and/or comments.</summary>
    public bool HasNoCode(int line)
    {
        if (line < 0 || line >= Lines.Length) return true;
        var text = Lines[line];
        var row = Mask[line];
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            if (row[i] is IonCharClass.Code or IonCharClass.String) return false;
        }
        return true;
    }

    /// <summary>True when the line contains at least one comment character.</summary>
    public bool HasComment(int line)
    {
        if (line < 0 || line >= Lines.Length) return false;
        return Array.IndexOf(Mask[line], IonCharClass.Comment) >= 0;
    }

    /// <summary>True when the line consists solely of whitespace and comment text.</summary>
    public bool IsCommentOnly(int line) => HasComment(line) && HasNoCode(line);
}

/// <summary>
/// Lexical scanner that classifies every character of an Ion document as code, comment
/// or string literal. This is the single source of truth for "is this <c>//</c> real, or
/// is it inside a string?" across the language server.
/// </summary>
public static class IonCommentScanner
{
    public static IonScannedDocument Scan(string content)
    {
        var lines = content.Split('\n');
        var mask = new IonCharClass[lines.Length][];
        var opens = new bool[lines.Length];
        var comments = new List<IonCommentSpan>();

        var inBlock = false;
        var blockKind = IonCommentKind.Block;
        var blockStartLine = 0;
        var blockStartChar = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var len = line.Length;
            var row = new IonCharClass[len];
            mask[i] = row;
            opens[i] = inBlock;

            var inString = false;
            var j = 0;

            while (j < len)
            {
                var c = line[j];

                if (inBlock)
                {
                    row[j] = IonCharClass.Comment;
                    if (c == '*' && j + 1 < len && line[j + 1] == '/')
                    {
                        row[j + 1] = IonCharClass.Comment;
                        j += 2;
                        comments.Add(new IonCommentSpan(blockStartLine, blockStartChar, i, j, blockKind));
                        inBlock = false;
                        continue;
                    }
                    j++;
                    continue;
                }

                if (inString)
                {
                    row[j] = IonCharClass.String;
                    if (c == '\\' && j + 1 < len)
                    {
                        row[j + 1] = IonCharClass.String;
                        j += 2;
                        continue;
                    }
                    if (c == '"') inString = false;
                    j++;
                    continue;
                }

                if (c == '"')
                {
                    row[j] = IonCharClass.String;
                    inString = true;
                    j++;
                    continue;
                }

                if (c == '/' && j + 1 < len && line[j + 1] == '/')
                {
                    var kind = IonCommentKind.Line;
                    if (j + 2 < len)
                    {
                        if (line[j + 2] == '/' && !(j + 3 < len && line[j + 3] == '/'))
                            kind = IonCommentKind.DocLine;
                        else if (line[j + 2] == '!')
                            kind = IonCommentKind.ModuleDoc;
                    }

                    for (var k = j; k < len; k++)
                        row[k] = IonCharClass.Comment;

                    var end = len;
                    while (end > j && line[end - 1] == '\r') end--;

                    comments.Add(new IonCommentSpan(i, j, i, end, kind));
                    j = len;
                    continue;
                }

                if (c == '/' && j + 1 < len && line[j + 1] == '*')
                {
                    // `/**/` is an empty block comment, not a doc comment.
                    blockKind = j + 2 < len && line[j + 2] == '*' && !(j + 3 < len && line[j + 3] == '/')
                        ? IonCommentKind.DocBlock
                        : IonCommentKind.Block;
                    blockStartLine = i;
                    blockStartChar = j;
                    row[j] = IonCharClass.Comment;
                    row[j + 1] = IonCharClass.Comment;
                    inBlock = true;
                    j += 2;
                    continue;
                }

                row[j] = IonCharClass.Code;
                j++;
            }

            // An unterminated string literal never continues onto the next line.
        }

        if (inBlock && lines.Length > 0)
        {
            var last = lines.Length - 1;
            comments.Add(new IonCommentSpan(blockStartLine, blockStartChar, last, lines[last].Length, blockKind));
        }

        return new IonScannedDocument
        {
            Lines = lines,
            Mask = mask,
            Comments = comments,
            OpensInsideBlockComment = opens
        };
    }
}
