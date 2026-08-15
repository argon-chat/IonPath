namespace ion.syntax.test;

using Pidgin;

/// <summary>
/// Coverage for the fixed-size array modifier.
/// <code>
/// array := '[' size? ']'
/// size  := '-'? digit+
/// </code>
/// <para>
/// <c>T[]</c> is unchanged; <c>T[N]</c> is the same modifier carrying a count. Both normalize to the
/// <c>"[]"</c> modifier token, so everything the modifier machinery already does — repeat detection,
/// canonical ordering — keeps working without knowing sizes exist.
/// </para>
/// </summary>
public class FixedSizeArrayTests
{
    private static IonUnderlyingTypeSyntax FieldType(string written)
    {
        var result = IonParser.Message.Parse($"msg M {{ a: {written}; }}");
        Assert.That(result.Success, Is.True, () => $"parse of '{written}' failed: {result.Error}");
        return ((IonMessageSyntax)result.Value).Fields.Single().Type;
    }

    private static void AssertRejected(string written)
    {
        var result = IonParser.Message.Parse($"msg M {{ a: {written}; }}");
        Assert.That(result.Success, Is.False, () => $"'{written}' was expected to fail but parsed");
    }

    #region the shape

    [TestCase("f4[16]", 16)]
    [TestCase("Vector[4]", 4)]
    [TestCase("f4[1]", 1)]
    [TestCase("f4[2147483647]", int.MaxValue)]
    [TestCase("f4[007]", 7)]
    public void SizedArray_CarriesItsSize(string written, int expected)
    {
        var type = FieldType(written);

        Assert.Multiple(() =>
        {
            Assert.That(type.IsArray, Is.True, "IsArray is unchanged in meaning");
            Assert.That(type.ArraySize, Is.EqualTo(expected));
            Assert.That(type.ModifierTokens, Is.EqualTo(new[] { "[]" }));
        });
    }

    [Test]
    public void UnsizedArray_IsUnchanged()
    {
        var type = FieldType("T[]");

        Assert.Multiple(() =>
        {
            Assert.That(type.IsArray, Is.True);
            Assert.That(type.ArraySize, Is.Null, "no size written");
            Assert.That(type.ModifierTokens, Is.EqualTo(new[] { "[]" }));
        });
    }

    #endregion

    #region stacking

    [Test]
    public void SizedArray_ThenOptional()
    {
        var type = FieldType("f4[16]?");

        Assert.Multiple(() =>
        {
            Assert.That(type.IsArray, Is.True);
            Assert.That(type.IsOptional, Is.True);
            Assert.That(type.ArraySize, Is.EqualTo(16));
            Assert.That(type.ModifierTokens, Is.EqualTo(new[] { "[]", "?" }),
                "normalized, so the canonical-order check ranks it like a plain '[]'");
        });
    }

    [Test]
    public void Partial_ThenSizedArray()
    {
        var type = FieldType("Data~[8]");

        Assert.Multiple(() =>
        {
            Assert.That(type.IsPartial, Is.True);
            Assert.That(type.IsArray, Is.True);
            Assert.That(type.ArraySize, Is.EqualTo(8));
            Assert.That(type.ModifierTokens, Is.EqualTo(new[] { "~", "[]" }),
                "'~' then '[]' is canonical order — a raw '[8]' token would rank -1 and fake an ION0010");
        });
    }

    [Test]
    public void EveryModifier_Stacked()
    {
        var type = FieldType("Data~[8]?");

        Assert.That(type.ModifierTokens, Is.EqualTo(new[] { "~", "[]", "?" }));
        Assert.That(type.ArraySize, Is.EqualTo(8));
    }

    /// <summary>
    /// The requirement that drove normalization: a repeated array suffix has to look repeated to
    /// <c>TypeModifierValidationStage</c>, which groups the tokens by ordinal equality. Raw
    /// <c>"[16]"</c> / <c>"[8]"</c> tokens would be two distinct modifiers and ION0019 would go
    /// missing.
    /// </summary>
    [TestCase("f4[16][8]")]
    [TestCase("f4[][8]")]
    [TestCase("f4[16][]")]
    [TestCase("f4[][]")]
    public void RepeatedArraySuffix_LooksRepeated(string written)
    {
        Assert.That(FieldType(written).ModifierTokens, Is.EqualTo(new[] { "[]", "[]" }));
    }

    [Test]
    public void RepeatedArraySuffix_KeepsTheFirstSizeWritten()
    {
        Assert.That(FieldType("f4[16][8]").ArraySize, Is.EqualTo(16));
        Assert.That(FieldType("f4[][8]").ArraySize, Is.EqualTo(8),
            "the first suffix that carried a size at all");
    }

    #endregion

    #region out-of-range sizes are carried, not rejected

    /// <summary>
    /// Consistent with how a repeated modifier is handled: failing in the grammar would abort the
    /// enclosing declaration and lose error recovery for every field after it, so the value reaches
    /// the compiler and gets a diagnostic in context.
    /// </summary>
    [TestCase("f4[0]", 0)]
    [TestCase("f4[-1]", -1)]
    [TestCase("f4[-0]", 0)]
    [TestCase("f4[-2147483648]", int.MinValue)]
    public void ZeroAndNegativeSizes_ParseAndAreCarried(string written, int expected)
    {
        var type = FieldType(written);

        Assert.Multiple(() =>
        {
            Assert.That(type.IsArray, Is.True);
            Assert.That(type.ArraySize, Is.EqualTo(expected));
        });
    }

    [Test]
    public void ZeroSize_DoesNotAbortTheRestOfTheMessage()
    {
        var result = IonParser.Message.Parse("msg M { a: f4[0]; b: i4; c: string; }");

        Assert.That(result.Success, Is.True, () => $"{result.Error}");
        Assert.That(((IonMessageSyntax)result.Value).Fields, Has.Count.EqualTo(3));
    }

    /// <summary>
    /// The one shape that does fail in the grammar: <c>int?</c> has no room for it, and no semantic
    /// rule could make a size that large legal, so there is no diagnostic quality to preserve.
    /// </summary>
    [TestCase("f4[2147483648]", TestName = "OverlargeSize_JustPastIntMax")]
    [TestCase("f4[99999999999999999999]", TestName = "OverlargeSize_HugeLiteral")]
    [TestCase("f4[-2147483649]", TestName = "OverlargeSize_JustPastIntMin")]
    public void SizeThatDoesNotFitInAnInt_IsAParseError(string written)
    {
        var result = IonParser.Message.Parse($"msg M {{ a: {written}; }}");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ToString(), Does.Contain("32 bit integer"));
    }

    [Test]
    public void ASizeOfAMillionDigits_FailsFast()
    {
        var input = $"msg M {{ a: f4[{new string('9', 1_000_000)}]; }}";

        Assert.That(ParseBudget.Within(() => IonParser.Message.Parse(input).Success), Is.False,
            "the digit run is length-checked before any numeric conversion");
    }

    /// <summary>
    /// A rejected type kills its declaration, but file level recovery still resynchronises on the
    /// next one — a bad size cannot swallow the rest of the file.
    /// </summary>
    [Test]
    public void AnUnparseableSize_DoesNotSwallowTheRestOfTheFile()
    {
        var file = IonParser.Parse("test", """
                                           msg Broken { a: f4[99999999999999999999]; }

                                           msg Fine { b: i4; }
                                           """);

        Assert.Multiple(() =>
        {
            Assert.That(file.messageSyntaxes.Select(m => m.Name.Identifier), Does.Contain("Fine"));
            Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Not.Empty);
        });
    }

    #endregion

    #region malformed

    [TestCase("f4[x]", TestName = "Size_NonNumeric")]
    [TestCase("f4[1.5]", TestName = "Size_Float")]
    [TestCase("f4[0x10]", TestName = "Size_Hex")]
    [TestCase("f4[1_000]", TestName = "Size_DigitSeparator")]
    [TestCase("f4[16", TestName = "Size_Unterminated")]
    [TestCase("f4[1,2]", TestName = "Size_TwoDimensions")]
    [TestCase("f4[- 1]", TestName = "Size_SpaceAfterSign")]
    [TestCase("f4[+1]", TestName = "Size_ExplicitPlus")]
    public void MalformedSize_IsAParseError(string written) => AssertRejected(written);

    [Test]
    public void MalformedSize_DoesNotSwallowTheRestOfTheFile()
    {
        var file = IonParser.Parse("test", """
                                           msg Broken { a: f4[x]; }

                                           msg Fine { b: i4; }
                                           """);

        Assert.That(file.messageSyntaxes.Select(m => m.Name.Identifier), Does.Contain("Fine"));
    }

    #endregion

    #region trivia

    [Test]
    public void Comments_AroundTheSize()
    {
        var type = FieldType("f4 /* a */ [ /* b */ 16 /* c */ ] /* d */ ?");

        Assert.Multiple(() =>
        {
            Assert.That(type.ArraySize, Is.EqualTo(16));
            Assert.That(type.IsOptional, Is.True);
        });
    }

    [Test]
    public void NewlinesInsideTheSuffix()
    {
        var type = FieldType("""
                             f4[
                                 // sixteen components
                                 16
                             ]
                             """);

        Assert.That(type.ArraySize, Is.EqualTo(16));
    }

    [Test]
    public void EmptySuffix_WithTriviaInside()
    {
        var type = FieldType("f4[ /* nothing */ ]");

        Assert.Multiple(() =>
        {
            Assert.That(type.IsArray, Is.True);
            Assert.That(type.ArraySize, Is.Null);
        });
    }

    #endregion

    #region positions

    /// <summary>
    /// A diagnostic about the size has to point at the size, not at the type that carries it.
    /// </summary>
    [Test]
    public void SizeSpan_PointsAtTheSuffix()
    {
        var type = FieldType("f4[16]");

        // `msg M { a: f4[16]; }` — 'f' of f4 is col 12, so '[' is col 14 and ']' ends at col 18.
        Assert.Multiple(() =>
        {
            Assert.That(type.StartPosition.Col, Is.EqualTo(12), "the type starts at 'f4'");
            Assert.That(type.ArraySizeStart!.Value.Col, Is.EqualTo(14), "the span starts at '['");
            Assert.That(type.ArraySizeEnd!.Value.Col, Is.EqualTo(18), "and ends just past ']'");
        });
    }

    [Test]
    public void NoSizeWritten_MeansNoSizeSpan()
    {
        var type = FieldType("f4[]");

        Assert.That(type.ArraySizeStart, Is.Null);
        Assert.That(type.ArraySizeEnd, Is.Null);
    }

    #endregion

    #region every type position accepts a size

    [TestCase("msg M { a: f4[16]; }", TestName = "SizedArray_OnAMessageField")]
    [TestCase("service S(a: f4[16]) { m(): i4; }", TestName = "SizedArray_OnAServiceArgument")]
    [TestCase("service S() { m(a: f4[16]): i4; }", TestName = "SizedArray_OnAMethodArgument")]
    [TestCase("service S() { m(): f4[16]; }", TestName = "SizedArray_OnAMethodReturn")]
    [TestCase("union U { Ok(a: f4[16]) }", TestName = "SizedArray_OnAUnionCaseField")]
    [TestCase("mixin X { a: f4[16]; }", TestName = "SizedArray_OnAMixinField")]
    [TestCase("typedef Buf = f4[16];", TestName = "SizedArray_OnATypedefBase")]
    public void SizedArray_InEveryTypePosition(string source)
    {
        var result = IonParser.IonFile.Parse(source);

        Assert.That(result.Success, Is.True, () => $"{result.Error}");
    }

    #endregion
}
