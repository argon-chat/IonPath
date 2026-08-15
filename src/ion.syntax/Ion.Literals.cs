namespace ion.syntax;

using System.Globalization;
using System.Numerics;
using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

/// <summary>
/// The shared literal grammar.
/// <code>
/// literal   := string | number | enumRef | bool | null | array
/// number    := '-'? ( hex | binary | decimal )
/// hex       := '0' [xX] hexDigit ( hexDigit | '_' )*
/// binary    := '0' [bB] binDigit ( binDigit | '_' )*
/// decimal   := digits fraction? exponent?          // float iff fraction or exponent present
/// fraction  := '.' digits                          // only when '.' is followed by a digit
/// exponent  := [eE] [+-]? digits
/// digits    := digit ( digit | '_' )*              // must not end in '_'
/// string    := '"' ( escape | ~["\\\r\n] )* '"'
/// escape    := '\' ( '"' | '\' | 'n' | 'r' | 't' | '0' | 'u' hexDigit{4} )
/// enumRef   := identifier '.' identifier
/// bool      := ( "true" | "false" ) !identifierChar
/// null      := "null" !identifierChar
/// array     := '[' ( literal ( ',' literal )* )? ']'
/// </code>
/// <para>
/// This is a standalone component: <see cref="Literal"/> is the whole entry point and depends on
/// nothing but the trivia layer. It is consumed today by the attribute use site
/// (<c>Ion.Attributes.cs</c>) and is the intended grammar for roadmap 1.3 (default values and
/// constants) and for the <c>flags</c>/<c>enum</c> member values that
/// <c>Ion.Flags.cs</c> currently hands to the compiler as a raw <see cref="IonExpression"/> span.
/// </para>
/// <para>
/// Deliberately <b>not</b> accepted: a bare identifier (a constant reference — no syntax node for
/// it yet), a leading-dot float (<c>.5</c>), a trailing-dot float (<c>1.</c>), a trailing comma in
/// an array, and <c>- 5</c> with a space after the sign. Each is a clean parse failure.
/// </para>
/// </summary>
public partial class IonParser
{
    /// <summary>
    /// How deeply array literals may nest. The literal parser is built as a finite chain of this
    /// many levels rather than as a self-referential parser, so a pathological input such as
    /// 100 000 consecutive <c>[</c> fails with an ordinary parse error instead of overflowing the
    /// stack (which is not a catchable exception on .NET and would take the process down).
    /// </summary>
    public const int MaxLiteralNestingDepth = 32;

    #region digits

    private static Parser<char, char> DecDigit => Token(c => c is >= '0' and <= '9').Labelled("digit");

    private static Parser<char, char> HexDigit =>
        Token(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')
            .Labelled("hexadecimal digit");

    private static Parser<char, char> BinDigit => Token(c => c is '0' or '1').Labelled("binary digit");

    /// <summary>
    /// A run of digits with optional <c>_</c> separators. Must start and end with a digit, so
    /// <c>_1</c> is not a number at all and <c>1_</c> is a parse error rather than the value 1
    /// followed by mystery input.
    /// </summary>
    private static Parser<char, string> DigitRun(Parser<char, char> digit) =>
        Map((first, rest) => first + rest, digit, OneOf(digit, Char('_')).ManyString())
            .Assert(s => s[^1] != '_', "a '_' digit separator must be followed by a digit");

    #endregion

    #region numbers

    /// <summary>The raw token text of a number, plus whether it is a float.</summary>
    private static Parser<char, (string Raw, bool IsFloat)> NumberToken =>
        Map(
            (sign, body) => (sign.HasValue ? "-" + body.Raw : body.Raw, body.IsFloat),
            Char('-').Optional(),
            OneOf(HexBody, BinaryBody, DecimalBody));

    private static Parser<char, (string Raw, bool IsFloat)> HexBody =>
        Map(
            (x, digits) => ("0" + x + digits, false),
            Try(Char('0').Then(OneOf(Char('x'), Char('X')))),
            DigitRun(HexDigit));

    private static Parser<char, (string Raw, bool IsFloat)> BinaryBody =>
        Map(
            (b, digits) => ("0" + b + digits, false),
            Try(Char('0').Then(OneOf(Char('b'), Char('B')))),
            DigitRun(BinDigit));

    /// <summary>
    /// <c>digits [ '.' digits ] [ exponent ]</c>. The fraction is guarded by a <c>Try</c> on
    /// <c>'.' digit</c>: <c>Status.Active</c> never reaches here (it does not start with a digit),
    /// but <c>1.</c> and <c>1.2.3</c> do, and the guard makes both stop cleanly at the stray dot
    /// instead of producing a half-parsed float.
    /// </summary>
    private static Parser<char, (string Raw, bool IsFloat)> DecimalBody =>
        Map(
            (intPart, frac, exp) => (
                intPart + frac.GetValueOrDefault(string.Empty) + exp.GetValueOrDefault(string.Empty),
                frac.HasValue || exp.HasValue),
            DigitRun(DecDigit),
            Try(Char('.').Then(DigitRun(DecDigit)).Select(d => "." + d)).Optional(),
            Try(ExponentPart).Optional());

    private static Parser<char, string> ExponentPart =>
        Map(
            (e, sign, digits) => e + (sign.HasValue ? sign.Value.ToString() : string.Empty) + digits,
            OneOf(Char('e'), Char('E')),
            OneOf(Char('+'), Char('-')).Optional(),
            DigitRun(DecDigit));

    private static Parser<char, IonLiteralSyntax> LiteralNumber =>
        Map(
            (pos, token, end) => MakeNumber(token.Raw, token.IsFloat).WithPos(pos, end),
            CurrentPos,
            NumberToken,
            CurrentPos);

    private static IonLiteralSyntax MakeNumber(string raw, bool isFloat) =>
        isFloat
            ? new IonFloatLiteralSyntax(
                double.Parse(raw.Replace("_", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture),
                raw)
            : new IonIntegerLiteralSyntax(ParseIntegerText(raw), raw);

    /// <summary>
    /// Converts an already validated integer token to its exact value. Never throws: the grammar
    /// guarantees the shape, and <see cref="BigInteger"/> has no range to overflow.
    /// </summary>
    private static BigInteger ParseIntegerText(string raw)
    {
        var negative = raw.StartsWith('-');
        var body = negative ? raw[1..] : raw;

        BigInteger value;
        if (body.Length > 2 && body[0] == '0' && body[1] is 'x' or 'X')
            value = ParseRadix(body.AsSpan(2), 16);
        else if (body.Length > 2 && body[0] == '0' && body[1] is 'b' or 'B')
            value = ParseRadix(body.AsSpan(2), 2);
        else
            value = BigInteger.Parse(body.Replace("_", string.Empty), NumberStyles.None, CultureInfo.InvariantCulture);

        return negative ? -value : value;
    }

    private static BigInteger ParseRadix(ReadOnlySpan<char> digits, int radix)
    {
        var value = BigInteger.Zero;
        foreach (var c in digits)
        {
            if (c == '_')
                continue;
            value = value * radix + DigitValue(c);
        }

        return value;
    }

    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => c - 'A' + 10
    };

    #endregion

    #region strings

    /// <summary>
    /// Exactly four hex digits after <c>\u</c>. An astral-plane character is written as two
    /// escapes — a surrogate pair — and lands in the decoded value as its two chars. The parser
    /// deliberately does not validate pairing, which is what makes that round-trip work.
    /// </summary>
    private static Parser<char, string> UnicodeEscape =>
        HexDigit.Repeat(4)
            .Select(cs => ((char)Convert.ToInt32(new string(cs.ToArray()), 16)).ToString());

    /// <summary>
    /// The tail of an escape, after the backslash. An unrecognised escape fails here — and the
    /// backslash has already been consumed, so the failure is committed and surfaces as
    /// "unrecognised escape sequence" rather than silently ending the string.
    /// </summary>
    private static Parser<char, string> EscapeTail =>
        OneOf(
                Char('"').ThenReturn("\""),
                Char('\\').ThenReturn("\\"),
                Char('n').ThenReturn("\n"),
                Char('r').ThenReturn("\r"),
                Char('t').ThenReturn("\t"),
                Char('0').ThenReturn("\0"),
                Char('u').Then(UnicodeEscape))
            .Labelled("escape sequence (one of \\\" \\\\ \\n \\r \\t \\0 \\uXXXX)");

    /// <summary>
    /// One string element. A raw line break is excluded on purpose: without that, an unterminated
    /// string swallows the rest of the file and the error is reported at EOF instead of at the
    /// opening quote's line.
    /// </summary>
    private static Parser<char, string> StringElement =>
        OneOf(
            Char('\\').Then(EscapeTail),
            AnyCharExcept('"', '\\', '\r', '\n').Select(char.ToString));

    private static Parser<char, IonLiteralSyntax> LiteralString =>
        Map(
            IonLiteralSyntax (pos, parts, end) =>
                new IonStringLiteralSyntax(string.Concat(parts)).WithPos(pos, end),
            CurrentPos,
            Char('"')
                .Then(StringElement.Many())
                .Before(Char('"').Labelled("closing '\"' of a string literal")),
            CurrentPos);

    #endregion

    #region keywords, enum references

    /// <summary>
    /// A bare word terminated by a word boundary, so <c>trueish</c> is an identifier and not
    /// <c>true</c> followed by <c>ish</c>. Atomic: a rejected match consumes nothing.
    /// </summary>
    private static Parser<char, Unit> LiteralWord(string text) =>
        Try(String(text).Then(Not(Lookahead(IdentifierChar))));

    private static Parser<char, IonLiteralSyntax> LiteralBool =>
        Map(
            IonLiteralSyntax (pos, value, end) => new IonBoolLiteralSyntax(value).WithPos(pos, end),
            CurrentPos,
            OneOf(LiteralWord("true").ThenReturn(true), LiteralWord("false").ThenReturn(false)),
            CurrentPos);

    private static Parser<char, IonLiteralSyntax> LiteralNull =>
        Map(
            IonLiteralSyntax (pos, _, end) => new IonNullLiteralSyntax().WithPos(pos, end),
            CurrentPos,
            LiteralWord("null"),
            CurrentPos);

    /// <summary>
    /// <c>Status.Active</c>. Atomic, because a lone <c>true</c>/<c>false</c>/<c>null</c> also
    /// starts with an identifier and has to fall through to the keyword alternatives.
    /// </summary>
    private static Parser<char, IonLiteralSyntax> LiteralEnumRef =>
        Try(Map(
            IonLiteralSyntax (pos, type, _, member, end) =>
                new IonEnumRefLiteralSyntax(type, member).WithPos(pos, end),
            CurrentPos,
            Identifier,
            Char('.').Before(SkipTrivia),
            Identifier,
            CurrentPos));

    #endregion

    #region composition

    /// <summary>
    /// The interior of a bracketed list uses <see cref="SkipTriviaAll"/> rather than
    /// <see cref="SkipTrivia"/>: a doc comment cannot attach to anything between <c>[</c> and
    /// <c>]</c>, so swallowing one there is right, whereas <see cref="Literal"/> itself must keep
    /// stopping in front of a doc comment (a future <c>x: i4 = 5;</c> default must not eat the doc
    /// of the field that follows it).
    /// </summary>
    private static Parser<char, IonLiteralSyntax> LiteralArray(Parser<char, IonLiteralSyntax> item) =>
        Map(
            IonLiteralSyntax (pos, items, end) => new IonArrayLiteralSyntax(items.ToList()).WithPos(pos, end),
            CurrentPos,
            Char('[')
                .Before(SkipTriviaAll)
                .Then(item.Before(SkipTriviaAll).Separated(Char(',').Before(SkipTriviaAll)))
                .Before(SkipTriviaAll.Then(Char(']').Labelled("closing ']' of an array literal"))),
            CurrentPos);

    /// <summary>
    /// The bottom of the nesting chain: consumes the <c>[</c> so the error lands on the offending
    /// bracket rather than being reported as "no literal here".
    /// </summary>
    private static Parser<char, IonLiteralSyntax> LiteralArrayTooDeep =>
        Char('[').Then(Fail<IonLiteralSyntax>(
            $"array literal nested more than {MaxLiteralNestingDepth} levels deep"));

    private static Parser<char, IonLiteralSyntax> BuildLiteral(int depth) =>
        OneOf(
                LiteralString,
                LiteralNumber,
                LiteralEnumRef,
                LiteralBool,
                LiteralNull,
                depth <= 0 ? LiteralArrayTooDeep : LiteralArray(BuildLiteral(depth - 1)))
            .Before(SkipTrivia);

    /// <summary>
    /// Built once. Nothing else in this type is touched during its initialisation (every part it
    /// references is a property), so partial-class static initialisation order cannot bite.
    /// </summary>
    private static readonly Parser<char, IonLiteralSyntax> LiteralParser = BuildLiteral(MaxLiteralNestingDepth);

    /// <summary>
    /// A literal value. Consumes trailing non-doc trivia, like every other token level parser here.
    /// </summary>
    public static Parser<char, IonLiteralSyntax> Literal => LiteralParser;

    #endregion
}
