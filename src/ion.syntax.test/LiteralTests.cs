namespace ion.syntax.test;

using System.Diagnostics;
using System.Numerics;
using Pidgin;

/// <summary>
/// Coverage for the shared literal grammar (<c>src/ion.syntax/Ion.Literals.cs</c>).
/// <code>
/// literal   := string | number | enumRef | bool | null | array
/// number    := '-'? ( '0'[xX] hex+ | '0'[bB] bin+ | digits ( '.' digits )? ( [eE] [+-]? digits )? )
/// string    := '"' ( escape | ~["\\\r\n] )* '"'
/// escape    := '\' ( '"' | '\' | 'n' | 'r' | 't' | '0' | 'u' hex{4} )
/// enumRef   := identifier '.' identifier
/// array     := '[' ( literal ( ',' literal )* )? ']'
/// </code>
/// The grammar is deliberately value-only. A bare identifier, <c>.5</c>, <c>1.</c>, a trailing
/// comma and <c>- 5</c> are all rejected; each has a pinning test below.
/// </summary>
public class LiteralTests
{
    private static readonly Parser<char, IonLiteralSyntax> Whole =
        IonParser.Literal.Before(Parser<char>.End);

    private static IonLiteralSyntax Accept(string text)
    {
        var result = Whole.Parse(text);
        Assert.That(result.Success, Is.True, () => $"expected `{text}` to parse, got: {result.Error}");
        return result.Value;
    }

    private static T Accept<T>(string text) where T : IonLiteralSyntax
    {
        var literal = Accept(text);
        Assert.That(literal, Is.InstanceOf<T>(), () => $"`{text}` produced {literal.GetType().Name}");
        return (T)literal;
    }

    private static void Reject(string text)
    {
        var result = Whole.Parse(text);
        Assert.That(result.Success, Is.False,
            () => $"expected `{text}` to be rejected, but it produced {Describe(result.Value)}");
    }

    private static string Describe(IonLiteralSyntax literal) => literal switch
    {
        IonIntegerLiteralSyntax i => $"integer {i.Value} (raw {i.Raw})",
        IonFloatLiteralSyntax f => $"float {f.Value} (raw {f.Raw})",
        IonStringLiteralSyntax s => $"string \"{s.Value}\"",
        IonBoolLiteralSyntax b => $"bool {b.Value}",
        IonNullLiteralSyntax => "null",
        IonEnumRefLiteralSyntax e => $"enum ref {e.TypeName}.{e.Member}",
        IonArrayLiteralSyntax a => $"array of {a.Items.Count}",
        _ => literal.ToString()
    };

    #region integers

    [TestCase("0", "0")]
    [TestCase("-0", "0")]
    [TestCase("1", "1")]
    [TestCase("42", "42")]
    [TestCase("-42", "-42")]
    [TestCase("08", "8", TestName = "Integer_LeadingZeroIsNotOctal")]
    // i8 range
    [TestCase("9223372036854775807", "9223372036854775807", TestName = "Integer_I8Max")]
    [TestCase("-9223372036854775808", "-9223372036854775808", TestName = "Integer_I8Min")]
    // u8 range
    [TestCase("18446744073709551615", "18446744073709551615", TestName = "Integer_U8Max")]
    // BigInteger means "out of range for every Ion type" is still lossless here; the semantic
    // layer range-checks against the declared parameter type, the grammar never truncates.
    [TestCase("340282366920938463463374607431768211456", "340282366920938463463374607431768211456",
        TestName = "Integer_BeyondU16")]
    // separators
    [TestCase("1_000", "1000")]
    [TestCase("1_000_000", "1000000")]
    [TestCase("1__0", "10", TestName = "Integer_RepeatedSeparatorAccepted")]
    // hex
    [TestCase("0xFF", "255")]
    [TestCase("0XFF", "255")]
    [TestCase("0xff", "255")]
    [TestCase("0xFFFF_FFFF", "4294967295")]
    [TestCase("0xFFFFFFFFFFFFFFFF", "18446744073709551615", TestName = "Integer_HexU8Max")]
    [TestCase("-0x10", "-16")]
    [TestCase("0x0", "0")]
    // binary
    [TestCase("0b1010_1010", "170")]
    [TestCase("0B11", "3")]
    [TestCase("0b0", "0")]
    [TestCase("-0b1", "-1")]
    public void Integer_Value(string source, string expected)
    {
        var literal = Accept<IonIntegerLiteralSyntax>(source);

        Assert.That(literal.Value, Is.EqualTo(BigInteger.Parse(expected)));
    }

    /// <summary>The author's spelling survives verbatim, so a hover can echo <c>0xFF</c> back.</summary>
    [TestCase("0xFF")]
    [TestCase("0b1010_1010")]
    [TestCase("1_000")]
    [TestCase("-0")]
    [TestCase("-0x10")]
    [TestCase("08")]
    public void Integer_RawIsTheAuthorsSpelling(string source)
        => Assert.That(Accept<IonIntegerLiteralSyntax>(source).Raw, Is.EqualTo(source));

    /// <summary><c>-0</c> is the value zero, but the sign is still visible in <c>Raw</c>.</summary>
    [Test]
    public void Integer_NegativeZero()
    {
        var literal = Accept<IonIntegerLiteralSyntax>("-0");

        Assert.Multiple(() =>
        {
            Assert.That(literal.Value, Is.EqualTo(BigInteger.Zero));
            Assert.That(literal.Raw, Is.EqualTo("-0"));
        });
    }

    [TestCase("1_", TestName = "Integer_TrailingSeparator")]
    [TestCase("_1", TestName = "Integer_LeadingSeparator")]
    [TestCase("0x", TestName = "Integer_HexPrefixWithNoDigits")]
    [TestCase("0b", TestName = "Integer_BinaryPrefixWithNoDigits")]
    [TestCase("0x_1", TestName = "Integer_HexSeparatorFirst")]
    [TestCase("0b2", TestName = "Integer_BinaryDigitOutOfRange")]
    [TestCase("0xG", TestName = "Integer_HexDigitOutOfRange")]
    [TestCase("0xFF_", TestName = "Integer_HexTrailingSeparator")]
    [TestCase("- 5", TestName = "Integer_SpaceAfterSign")]
    [TestCase("+5", TestName = "Integer_LeadingPlusNotAccepted")]
    [TestCase("--5", TestName = "Integer_DoubleSign")]
    public void Integer_Rejected(string source) => Reject(source);

    #endregion

    #region floats

    [TestCase("1.5", 1.5d)]
    [TestCase("-0.25", -0.25d)]
    [TestCase("0.0", 0.0d)]
    [TestCase("1e10", 1e10d)]
    [TestCase("1E10", 1e10d)]
    [TestCase("1e+10", 1e10d)]
    [TestCase("1e-10", 1e-10d)]
    [TestCase("1.5e-3", 0.0015d)]
    [TestCase("-1.5e-3", -0.0015d)]
    [TestCase("1e308", 1e308d)]
    [TestCase("-1e308", -1e308d)]
    [TestCase("1_000.5", 1000.5d, TestName = "Float_SeparatorInIntegerPart")]
    [TestCase("1.000_5", 1.0005d, TestName = "Float_SeparatorInFraction")]
    public void Float_Value(string source, double expected)
    {
        var literal = Accept<IonFloatLiteralSyntax>(source);

        Assert.Multiple(() =>
        {
            Assert.That(literal.Value, Is.EqualTo(expected).Within(1e-15).Percent);
            Assert.That(literal.Raw, Is.EqualTo(source));
        });
    }

    /// <summary>
    /// Overflow is not a parse error: .NET yields infinity and <c>Raw</c> still holds the source
    /// text, which is what a "value out of range for f8" diagnostic needs.
    /// </summary>
    [Test]
    public void Float_OverflowBecomesInfinity()
    {
        var literal = Accept<IonFloatLiteralSyntax>("1e999");

        Assert.Multiple(() =>
        {
            Assert.That(double.IsPositiveInfinity(literal.Value), Is.True);
            Assert.That(literal.Raw, Is.EqualTo("1e999"));
        });
    }

    [TestCase(".5", TestName = "Float_LeadingDotRejected")]
    [TestCase("-.5", TestName = "Float_SignedLeadingDotRejected")]
    [TestCase("1.", TestName = "Float_TrailingDotRejected")]
    [TestCase("1.2.3", TestName = "Float_TwoDotsRejected")]
    [TestCase("1e", TestName = "Float_ExponentWithNoDigits")]
    [TestCase("1e+", TestName = "Float_ExponentSignWithNoDigits")]
    [TestCase("1.5e", TestName = "Float_FractionThenEmptyExponent")]
    [TestCase("1..5", TestName = "Float_DoubleDot")]
    public void Float_Rejected(string source) => Reject(source);

    /// <summary>
    /// The pivot of the whole float/enum-ref split: a <c>.</c> only starts a fraction when a digit
    /// follows it, so <c>Status.Active</c> can never be lexed as a number and <c>1.5</c> can never
    /// be lexed as a member reference.
    /// </summary>
    [Test]
    public void Float_AndEnumRef_DoNotCollide()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Accept("1.5"), Is.InstanceOf<IonFloatLiteralSyntax>());
            Assert.That(Accept("Status.Active"), Is.InstanceOf<IonEnumRefLiteralSyntax>());
        });
    }

    #endregion

    #region strings

    [Test]
    public void String_Empty() => Assert.That(Accept<IonStringLiteralSyntax>("\"\"").Value, Is.Empty);

    [Test]
    public void String_Plain() => Assert.That(Accept<IonStringLiteralSyntax>("\"hello\"").Value, Is.EqualTo("hello"));

    /// <summary>Every escape the grammar recognises, in one literal.</summary>
    [Test]
    public void String_AllEscapes()
    {
        const string source = """
                              "a\"b\\c\nd\re\tf\0g"
                              """;

        Assert.That(Accept<IonStringLiteralSyntax>(source).Value,
            Is.EqualTo("a\"b\\c\nd\re\tf\0g"));
    }

    // The first TestCase argument is Ion source text: a real backslash followed by 'u',
    // i.e. exactly what the Ion author typed.
    [TestCase("\"\\u0041\"", "A", TestName = "String_UnicodeEscape_Ascii")]
    [TestCase("\"\\u00e9\"", "\u00e9", TestName = "String_UnicodeEscape_LowercaseHex")]
    [TestCase("\"\\u00E9\"", "\u00e9", TestName = "String_UnicodeEscape_UppercaseHex")]
    [TestCase("\"\\u0000\"", "\0", TestName = "String_UnicodeEscape_Nul")]
    [TestCase("\"a\\u0041b\"", "aAb", TestName = "String_UnicodeEscape_Embedded")]
    public void String_UnicodeEscape(string source, string expected)
        => Assert.That(Accept<IonStringLiteralSyntax>(source).Value, Is.EqualTo(expected));

    /// <summary>
    /// A surrogate pair is two <c>\uXXXX</c> escapes. The parser appends chars and never tries to
    /// validate pairing, which is exactly what makes an astral-plane character round-trip.
    /// </summary>
    [Test]
    public void String_SurrogatePair()
    {
        // Ion source: "\uD83D\uDE00"
        var literal = Accept<IonStringLiteralSyntax>("\"\\uD83D\\uDE00\"");

        Assert.Multiple(() =>
        {
            Assert.That(literal.Value, Is.EqualTo("\uD83D\uDE00"));
            Assert.That(literal.Value, Has.Length.EqualTo(2));
            Assert.That(char.ConvertToUtf32(literal.Value, 0), Is.EqualTo(0x1F600));
        });
    }

    /// <summary>The trivia skipper is never entered from inside a literal.</summary>
    [TestCase(@"""a//b""", "a//b", TestName = "String_LineCommentIsPlainText")]
    [TestCase(@"""a/*b""", "a/*b", TestName = "String_BlockCommentOpenIsPlainText")]
    [TestCase(@"""a*/b""", "a*/b", TestName = "String_BlockCommentCloseIsPlainText")]
    [TestCase(@"""a)b""", "a)b", TestName = "String_CloseParenIsPlainText")]
    [TestCase(@"""a,b""", "a,b", TestName = "String_CommaIsPlainText")]
    [TestCase(@"""a]b""", "a]b", TestName = "String_CloseBracketIsPlainText")]
    public void String_DelimitersInsideAreText(string source, string expected)
        => Assert.That(Accept<IonStringLiteralSyntax>(source).Value, Is.EqualTo(expected));

    [TestCase("\"abc", TestName = "String_Unterminated")]
    [TestCase("\"", TestName = "String_LoneQuote")]
    [TestCase("\"abc\nd\"", TestName = "String_RawNewlineRejected")]
    [TestCase("\"abc\rd\"", TestName = "String_RawCarriageReturnRejected")]
    [TestCase(@"""\q""", TestName = "String_UnknownEscape")]
    [TestCase(@"""\""", TestName = "String_TrailingBackslashEatsTheCloser")]
    [TestCase(@"""\u12""", TestName = "String_ShortUnicodeEscape")]
    [TestCase(@"""\uZZZZ""", TestName = "String_NonHexUnicodeEscape")]
    [TestCase(@"""\x41""", TestName = "String_HexEscapeNotSupported")]
    [TestCase(@"""\'""", TestName = "String_SingleQuoteEscapeNotSupported")]
    public void String_Rejected(string source) => Reject(source);

    /// <summary>
    /// An unterminated string stops at the end of the line rather than swallowing the rest of the
    /// file, so the reported error is near the opening quote.
    /// </summary>
    [Test]
    public void String_Unterminated_DoesNotSwallowFollowingLines()
    {
        var result = IonParser.Literal.Parse("\"abc\nmsg M {}");

        Assert.That(result.Success, Is.False);
    }

    #endregion

    #region booleans and null

    [TestCase("true", true)]
    [TestCase("false", false)]
    public void Bool_Value(string source, bool expected)
        => Assert.That(Accept<IonBoolLiteralSyntax>(source).Value, Is.EqualTo(expected));

    [Test]
    public void Null_Value() => Assert.That(Accept("null"), Is.InstanceOf<IonNullLiteralSyntax>());

    /// <summary>
    /// Word boundary: an identifier that merely starts with a keyword is not that keyword.
    /// Because a bare identifier is not a literal at all, these are outright rejected — which is
    /// the point: <c>trueish</c> must never silently lex as <c>true</c>.
    /// </summary>
    [TestCase("trueish", TestName = "Bool_TrueishIsNotTrue")]
    [TestCase("falsey", TestName = "Bool_FalseyIsNotFalse")]
    [TestCase("nullable", TestName = "Null_NullableIsNotNull")]
    [TestCase("true_", TestName = "Bool_TrailingUnderscore")]
    [TestCase("null1", TestName = "Null_TrailingDigit")]
    [TestCase("True", TestName = "Bool_CaseSensitive")]
    [TestCase("NULL", TestName = "Null_CaseSensitive")]
    public void Keyword_WordBoundary(string source) => Reject(source);

    /// <summary>A bare identifier is not a value. Constant references are roadmap 1.3.</summary>
    [TestCase("x")]
    [TestCase("Status")]
    [TestCase("_private")]
    public void BareIdentifier_IsNotALiteral(string source) => Reject(source);

    #endregion

    #region enum member references

    [Test]
    public void EnumRef_Parts()
    {
        var literal = Accept<IonEnumRefLiteralSyntax>("Status.Active");

        Assert.Multiple(() =>
        {
            Assert.That(literal.TypeName.Identifier, Is.EqualTo("Status"));
            Assert.That(literal.Member.Identifier, Is.EqualTo("Active"));
        });
    }

    [TestCase("Status . Active", TestName = "EnumRef_SpacesAroundDot")]
    [TestCase("Status/* a */./* b */Active", TestName = "EnumRef_CommentsAroundDot")]
    [TestCase("_S._A", TestName = "EnumRef_Underscores")]
    [TestCase("S1.A1", TestName = "EnumRef_Digits")]
    public void EnumRef_TriviaAndShapes(string source)
        => Assert.That(Accept(source), Is.InstanceOf<IonEnumRefLiteralSyntax>());

    /// <summary>
    /// A keyword only wins when it stands alone: <c>true.x</c> is a (nonsensical but
    /// representable) member reference, and resolution is the semantic layer's problem.
    /// </summary>
    [Test]
    public void EnumRef_BeatsKeywordWhenFollowedByDot()
    {
        var literal = Accept<IonEnumRefLiteralSyntax>("true.x");

        Assert.That(literal.TypeName.Identifier, Is.EqualTo("true"));
    }

    [TestCase("Status.", TestName = "EnumRef_MissingMember")]
    [TestCase(".Active", TestName = "EnumRef_MissingType")]
    [TestCase("Status.Active.Extra", TestName = "EnumRef_ThreeParts")]
    [TestCase("Status..Active", TestName = "EnumRef_DoubleDot")]
    public void EnumRef_Rejected(string source) => Reject(source);

    #endregion

    #region arrays

    [Test]
    public void Array_Empty() => Assert.That(Accept<IonArrayLiteralSyntax>("[]").Items, Is.Empty);

    [Test]
    public void Array_Integers()
    {
        var literal = Accept<IonArrayLiteralSyntax>("[1, 2, 3]");

        Assert.That(literal.Items.Cast<IonIntegerLiteralSyntax>().Select(i => (int)i.Value),
            Is.EqualTo(new[] { 1, 2, 3 }));
    }

    /// <summary>
    /// Nested arrays are supported (not diagnosed). Element type agreement is a semantic concern.
    /// </summary>
    [Test]
    public void Array_Nested()
    {
        var literal = Accept<IonArrayLiteralSyntax>("[[1,2],[3]]");

        Assert.Multiple(() =>
        {
            Assert.That(literal.Items, Has.Count.EqualTo(2));
            Assert.That(((IonArrayLiteralSyntax)literal.Items[0]).Items, Has.Count.EqualTo(2));
            Assert.That(((IonArrayLiteralSyntax)literal.Items[1]).Items, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Array_EmptyNested()
    {
        var literal = Accept<IonArrayLiteralSyntax>("[[], []]");

        Assert.That(literal.Items.Cast<IonArrayLiteralSyntax>().Select(a => a.Items.Count),
            Is.EqualTo(new[] { 0, 0 }));
    }

    /// <summary>Mixed element types parse; rejecting them is the semantic layer's call.</summary>
    [Test]
    public void Array_Heterogeneous()
    {
        var literal = Accept<IonArrayLiteralSyntax>("""[1, "a", true, null, Status.Active, 1.5, [2]]""");

        Assert.That(literal.Items.Select(i => i.GetType()), Is.EqualTo(new[]
        {
            typeof(IonIntegerLiteralSyntax),
            typeof(IonStringLiteralSyntax),
            typeof(IonBoolLiteralSyntax),
            typeof(IonNullLiteralSyntax),
            typeof(IonEnumRefLiteralSyntax),
            typeof(IonFloatLiteralSyntax),
            typeof(IonArrayLiteralSyntax)
        }));
    }

    [TestCase("[ 1 , 2 ]", TestName = "Array_Spaces")]
    [TestCase("[\n  1,\n  2\n]", TestName = "Array_Newlines")]
    [TestCase("[/*a*/1/*b*/,/*c*/2/*d*/]", TestName = "Array_BlockComments")]
    [TestCase("[ // leading\n1, 2 // trailing\n]", TestName = "Array_LineComments")]
    [TestCase("[1, 2 /** dangling doc */]", TestName = "Array_DanglingDocBeforeClose")]
    [TestCase("[/** d */ 1, 2]", TestName = "Array_DocAfterOpen")]
    [TestCase("[1 /** d */, 2]", TestName = "Array_DocBeforeComma")]
    [TestCase("[1, /** d */ 2]", TestName = "Array_DocAfterComma")]
    [TestCase("[/// d\n1, 2]", TestName = "Array_LineDocInside")]
    public void Array_TriviaEverywhere(string source)
        => Assert.That(Accept<IonArrayLiteralSyntax>(source).Items, Has.Count.EqualTo(2));

    /// <summary>
    /// Trailing commas are rejected, matching every other comma separated list in Ion (fields,
    /// enum members, declaration argument lists).
    /// </summary>
    [TestCase("[1,]", TestName = "Array_TrailingComma")]
    [TestCase("[,]", TestName = "Array_LoneComma")]
    [TestCase("[,1]", TestName = "Array_LeadingComma")]
    [TestCase("[1 2]", TestName = "Array_MissingComma")]
    [TestCase("[1,,2]", TestName = "Array_DoubleComma")]
    [TestCase("[1", TestName = "Array_Unterminated")]
    [TestCase("[", TestName = "Array_LoneOpenBracket")]
    [TestCase("[[1]", TestName = "Array_UnterminatedNested")]
    [TestCase("]", TestName = "Array_LoneCloseBracket")]
    [TestCase("[1]]", TestName = "Array_ExtraCloseBracket")]
    public void Array_Rejected(string source) => Reject(source);

    #endregion

    #region source positions

    /// <summary>
    /// Every literal carries a position: a diagnostic points at the argument, not at the
    /// construct that contains it.
    /// </summary>
    [Test]
    public void Literal_CarriesSourcePosition()
    {
        var array = Accept<IonArrayLiteralSyntax>("[1,\n 22]");

        Assert.Multiple(() =>
        {
            Assert.That(array.StartPosition.Line, Is.EqualTo(1));
            Assert.That(array.StartPosition.Col, Is.EqualTo(1));
            Assert.That(array.EndPosition!.Value.Line, Is.EqualTo(2));

            var first = array.Items[0];
            Assert.That(first.StartPosition.Line, Is.EqualTo(1));
            Assert.That(first.StartPosition.Col, Is.EqualTo(2));

            var second = array.Items[1];
            Assert.That(second.StartPosition.Line, Is.EqualTo(2), "second element is on line 2");
            Assert.That(second.StartPosition.Col, Is.EqualTo(2));
            Assert.That(second.EndPosition!.Value.Col, Is.EqualTo(4), "end is just past '22'");
        });
    }

    [Test]
    public void EnumRef_PartsCarryTheirOwnPositions()
    {
        var literal = Accept<IonEnumRefLiteralSyntax>("Status.Active");

        Assert.Multiple(() =>
        {
            Assert.That(literal.TypeName.StartPosition.Col, Is.EqualTo(1));
            Assert.That(literal.Member.StartPosition.Col, Is.EqualTo(8));
        });
    }

    #endregion

    #region pathological input

    /// <summary>A big but legal array must parse, and must not take super-linear time.</summary>
    [Test]
    public void Pathological_TenThousandElementArray()
    {
        var source = "[" + string.Join(",", Enumerable.Range(0, 10_000)) + "]";

        var sw = Stopwatch.StartNew();
        var literal = Accept<IonArrayLiteralSyntax>(source);
        sw.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(literal.Items, Has.Count.EqualTo(10_000));
            Assert.That(((IonIntegerLiteralSyntax)literal.Items[9_999]).Value, Is.EqualTo(new BigInteger(9_999)));
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)), "10k element array should not crawl");
        });
    }

    /// <summary>
    /// The nesting limit is a hard grammar bound, not a guideline: the parser is built as a finite
    /// chain of <see cref="IonParser.MaxLiteralNestingDepth"/> levels precisely so that a runaway
    /// <c>[</c> cannot overflow the stack (uncatchable on .NET — it kills the test host).
    /// </summary>
    [Test]
    public void Pathological_MaxNestingIsAccepted()
    {
        const int depth = IonParser.MaxLiteralNestingDepth;
        var source = new string('[', depth) + new string(']', depth);

        var literal = Accept<IonArrayLiteralSyntax>(source);

        var level = 0;
        IonLiteralSyntax current = literal;
        while (current is IonArrayLiteralSyntax { Items.Count: 1 } array)
        {
            level++;
            current = array.Items[0];
        }

        Assert.That(level, Is.EqualTo(depth - 1), "innermost array is empty, so one fewer hop");
    }

    [Test]
    public void Pathological_OneLevelTooDeepIsRejected()
    {
        const int depth = IonParser.MaxLiteralNestingDepth + 1;

        Reject(new string('[', depth) + new string(']', depth));
    }

    [Test]
    public void Pathological_HugeUnterminatedNestingDoesNotHangOrThrow()
    {
        var source = new string('[', 100_000);

        var sw = Stopwatch.StartNew();
        var result = Whole.Parse(source);
        sw.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)));
        });
    }

    [Test]
    public void Pathological_HugeDigitRunDoesNotThrow()
    {
        var literal = Accept<IonIntegerLiteralSyntax>(new string('9', 5_000));

        Assert.That(literal.Value, Is.GreaterThan(BigInteger.Pow(10, 4_999)));
    }

    [Test]
    public void Pathological_EmptyInputIsRejected() => Reject(string.Empty);

    #endregion
}
