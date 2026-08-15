namespace ion.syntax.test;

using ion.runtime;

/// <summary>
/// End-to-end coverage for the fixed-size array suffix <c>T[N]</c> — ION0062, the way the size rides
/// on the <c>Array&lt;T&gt;</c> wrapper as <c>IonGenericType.FixedSize</c>, its place in
/// <c>ion.lock.json</c>, and the fact that it is the one collection wrapper that does <em>not</em>
/// break a type cycle.
/// </summary>
/// <remarks>
/// The interaction with the existing modifier rules is where this feature could most easily have gone
/// wrong, and it is not visible from the parse alone: <c>ModifierTokens</c> normalizes <c>[16]</c> to
/// <c>"[]"</c> precisely so that ION0019 and ION0010 keep working, and both directions of that
/// normalization are pinned below.
/// </remarks>
public class FixedSizeArraySemanticsTests
{
    // ═══════════════════════════════════════════════════════════════════
    // ACCEPTED
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>N &gt;= 1</c>, and the size reaches the IR on the <c>Array</c> wrapper rather than being
    /// dropped somewhere between the parser and the lock.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(16)]
    [TestCase(4096)]
    [TestCase(int.MaxValue)]
    public void PositiveSize_IsAcceptedAndCarried(int size)
    {
        var compiled = LanguageFeature.Compile($"msg M {{ a: f4[{size}]; }}");

        compiled.AssertAccepted();

        var type = compiled.Definition("M").fields[0].type;

        Assert.Multiple(() =>
        {
            Assert.That(type, Is.InstanceOf<IonGenericType>());
            Assert.That(((IonGenericType)type).FixedSize, Is.EqualTo(size));
            Assert.That(((IonGenericType)type).IsFixedSizeArray, Is.True);
            Assert.That(compiled.FieldType("M", "a"), Is.EqualTo($"Array<f4, {size}>"));
        });
    }

    /// <summary>An unsized <c>T[]</c> carries no size, and must stay distinguishable from a sized one.</summary>
    [Test]
    public void UnsizedArray_HasNoSize()
    {
        var compiled = LanguageFeature.Compile("msg M { a: f4[]; b: f4[4]; }");

        compiled.AssertAccepted();

        var unsized = (IonGenericType)compiled.Definition("M").fields[0].type;
        var sized = (IonGenericType)compiled.Definition("M").fields[1].type;

        Assert.Multiple(() =>
        {
            Assert.That(unsized.FixedSize, Is.Null);
            Assert.That(unsized.IsFixedSizeArray, Is.False);
            // Record equality has to see the size, or ION0022 could never fire on it.
            Assert.That(sized, Is.Not.EqualTo(unsized));
            Assert.That(sized, Is.Not.EqualTo(sized with { FixedSize = 8 }));
        });
    }

    /// <summary>
    /// A size is legal over anything an array is legal over, and the wrapper it lands on is the one
    /// <c>WrapModifiers</c> built — Partial stays inside, Maybe stays outside.
    /// </summary>
    [TestCase("f4[16]", "Array<f4, 16>", TestName = "Sized_Builtin")]
    [TestCase("Data[16]", "Array<Data, 16>", TestName = "Sized_Message")]
    [TestCase("Data~[16]", "Array<Partial<Data>, 16>", TestName = "Sized_Partial")]
    [TestCase("Data[16]?", "Maybe<Array<Data, 16>>", TestName = "Sized_Optional")]
    [TestCase("Data~[16]?", "Maybe<Array<Partial<Data>, 16>>", TestName = "Sized_PartialOptional")]
    [TestCase("Set<i4>[3]", "Array<Set<i4>, 3>", TestName = "Sized_Set")]
    [TestCase("Map<string, i4>[3]", "Array<Map<string, i4>, 3>", TestName = "Sized_Map")]
    [TestCase("Maybe<f4[16]>", "Maybe<Array<f4, 16>>", TestName = "Sized_InsideMaybe")]
    [TestCase("Array<f4[16]>", "Array<Array<f4, 16>>", TestName = "Sized_InsideArray")]
    [TestCase("Map<string, f4[16]>", "Map<string, Array<f4, 16>>", TestName = "Sized_AsMapValue")]
    [TestCase("Set<f4[16]>", "Set<Array<f4, 16>>", TestName = "Sized_AsSetElement")]
    public void Size_LandsOnTheArrayWrapper(string written, string canonical)
    {
        var compiled = LanguageFeature.Compile($"msg Data {{ z: i4; }}\nmsg M {{ a: {written}; }}");

        compiled.AssertAccepted();
        Assert.That(compiled.FieldType("M", "a"), Is.EqualTo(canonical));
    }

    // ═══════════════════════════════════════════════════════════════════
    // ION0062 — N < 1
    // ═══════════════════════════════════════════════════════════════════

    [TestCase("f4[0]", 0)]
    [TestCase("f4[-1]", -1)]
    [TestCase("f4[-3]", -3)]
    [TestCase("f4[-2147483648]", int.MinValue)]
    public void NonPositiveSize_IsReported(string written, int size)
    {
        var compiled = LanguageFeature.Compile($"msg M {{ a: {written}; }}");
        var diagnostic = compiled.Only(LanguageFeature.FixedArraySize);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.FixedArraySize }),
                compiled.Describe);
            Assert.That(diagnostic.Severity, Is.EqualTo(IonDiagnosticSeverity.Error));
            // Echoes the type exactly as written, size included...
            Assert.That(diagnostic.Message, Does.StartWith($"Fixed-size array '{written}' declares a size of {size}."));
            // ...says why zero is not merely odd...
            Assert.That(diagnostic.Message, Does.Contain("cannot be told apart from the field not being there"));
            // ...and offers both exits.
            Assert.That(diagnostic.Message, Does.Contain("drop the size for a variable-length array"));
        });
    }

    /// <summary>
    /// A rejected size is not carried into the IR. An out-of-range <c>N</c> must never reach a
    /// generator or the lock, even though the rest of the file still lowers.
    /// </summary>
    [Test]
    public void NonPositiveSize_IsNotCarriedIntoTheIR()
    {
        var compiled = LanguageFeature.Compile("msg M { a: f4[0]; }");

        Assert.Multiple(() =>
        {
            Assert.That(((IonGenericType)compiled.Definition("M").fields[0].type).FixedSize, Is.Null);
            Assert.That(compiled.FieldType("M", "a"), Is.EqualTo("Array<f4>"));
        });
    }

    /// <summary>
    /// ION0062 squiggles the <c>[N]</c> suffix, not the whole type: the element type is fine and
    /// pointing at it would highlight the half that is right.
    /// </summary>
    [Test]
    public void Size_Position_PointsAtTheSuffixOnly()
    {
        //    1234567890123456789
        //    msg M { a: f4[0]; }
        var compiled = LanguageFeature.Compile("msg M { a: f4[0]; }");

        LanguageFeature.AssertSpan(compiled.Only(LanguageFeature.FixedArraySize), 1, 14, 17);
    }

    /// <summary>The sign is inside the span too — it is part of the size that is wrong.</summary>
    [Test]
    public void Size_Position_IncludesANegativeSign()
    {
        //    12345678901234567890
        //    msg M { a: f4[-3]; }
        var compiled = LanguageFeature.Compile("msg M { a: f4[-3]; }");

        LanguageFeature.AssertSpan(compiled.Only(LanguageFeature.FixedArraySize), 1, 14, 18);
    }

    /// <summary>And at depth, on the inner suffix rather than the outer generic.</summary>
    [Test]
    public void Size_Position_PointsAtANestedSuffix()
    {
        //             1         2
        //    12345678901234567890123456
        //    msg M { m: Array<i4[0]>; }
        var compiled = LanguageFeature.Compile("msg M { m: Array<i4[0]>; }");

        LanguageFeature.AssertSpan(compiled.Only(LanguageFeature.FixedArraySize), 1, 20, 23);
    }

    /// <summary>Every bad size in a file, not just the first.</summary>
    [Test]
    public void Size_EveryOffendingSite_IsReported()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg M {
                                                   a: f4[0];
                                                   b: f4[4];
                                                   c: i4[-1];
                                               }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.FixedArraySize).Select(d => d.StartPosition.Line),
            Is.EqualTo(new[] { 2, 4 }), compiled.Describe);
    }

    /// <summary>A size is checked wherever a type can be written, not only on a message field.</summary>
    [TestCase("msg M { a: f4[0]; }", TestName = "Size_Checked_MessageField")]
    [TestCase("mixin X { a: f4[0]; }", TestName = "Size_Checked_MixinField")]
    [TestCase("typedef T = f4[0];", TestName = "Size_Checked_TypedefUnderlying")]
    [TestCase("union U(s: f4[0]) { Ok(a: i4) }", TestName = "Size_Checked_UnionSharedField")]
    [TestCase("union U { Ok(a: f4[0]) }", TestName = "Size_Checked_UnionCaseArgument")]
    [TestCase("service S(ctx: f4[0]) { Go(): i4; }", TestName = "Size_Checked_ServiceBaseArgument")]
    [TestCase("service S() { Go(a: f4[0]): i4; }", TestName = "Size_Checked_MethodArgument")]
    [TestCase("service S() { Go(): f4[0]; }", TestName = "Size_Checked_MethodReturnType")]
    [TestCase("attribute @a(v: f4[0]);", TestName = "Size_Checked_AttributeParameter")]
    public void Size_IsCheckedInEveryWrittenPosition(string source)
    {
        var compiled = LanguageFeature.Compile(source);

        Assert.That(compiled.WithCode(LanguageFeature.FixedArraySize), Has.Count.EqualTo(1),
            compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // MODIFIER NORMALIZATION — the load-bearing '[16]' → '[]'
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>f4[16][8]</c> must be diagnosed exactly like <c>f4[][]</c>. An unnormalized <c>"[16]"</c>
    /// token would make the two suffixes look like different modifiers and the repeat would go
    /// unreported.
    /// </summary>
    [TestCase("f4[16][8]", TestName = "Repeat_BothSized")]
    [TestCase("f4[16][]", TestName = "Repeat_FirstSized")]
    [TestCase("f4[][8]", TestName = "Repeat_SecondSized")]
    [TestCase("f4[][]", TestName = "Repeat_NeitherSized")]
    public void RepeatedArraySuffix_IsStillReported(string written)
    {
        var compiled = LanguageFeature.Compile($"msg M {{ a: {written}; }}");
        var diagnostic = compiled.Only("ION0019");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { "ION0019" }), compiled.Describe);
            Assert.That(diagnostic.Message, Does.Contain("repeats the '[]' modifier"));
            // The echo renders the sizes as '[]' — the documented cost of the normalization, and only
            // ever in input that is already an error.
            Assert.That(diagnostic.Message, Does.Contain("Type 'f4[][]'"));
        });
    }

    /// <summary>
    /// The mirror: <c>Data~[16]</c> is in canonical order and must not draw a spurious ION0010. An
    /// unnormalized token would rank as unknown against the fixed <c>["~", "[]", "?"]</c> table.
    /// </summary>
    [TestCase("Data~[16]", TestName = "Order_PartialThenSized")]
    [TestCase("Data[16]?", TestName = "Order_SizedThenOptional")]
    [TestCase("Data~[16]?", TestName = "Order_AllThree")]
    public void CanonicalOrderWithASize_IsNotOutOfOrder(string written)
        => LanguageFeature.Compile($"msg Data {{ z: i4; }}\nmsg M {{ a: {written}; }}").AssertAccepted();

    /// <summary>And a genuinely misordered size still is out of order.</summary>
    [TestCase("Data?[16]", "Data?[]")]
    [TestCase("Data[16]~", "Data[]~")]
    public void MisorderedSuffixWithASize_IsStillReported(string written, string echoed)
    {
        var compiled = LanguageFeature.Compile($"msg Data {{ z: i4; }}\nmsg M {{ a: {written}; }}");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { "ION0010" }), compiled.Describe);
        Assert.That(compiled.Only("ION0010").Message, Does.Contain($"Type '{echoed}'"));
    }

    /// <summary>
    /// A bad size and a bad spelling are independent mistakes; neither message subsumes the other, so
    /// both are reported on the same run.
    /// </summary>
    [Test]
    public void BadSizeAndBadOrder_AreBothReported()
    {
        var compiled = LanguageFeature.Compile("msg Data { z: i4; }\nmsg M { a: Data?[0]; }");

        Assert.That(compiled.ErrorCodes, Is.EquivalentTo(new[] { "ION0010", LanguageFeature.FixedArraySize }),
            compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // THE LOCK
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The size is part of the wire identity and is rendered as a trailing generic argument, in
    /// nested position too — a suffix form would have to be parsed back out of the middle of a
    /// string.
    /// </summary>
    [Test]
    public void Lock_RendersTheSizeAsATrailingArgument()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg M { a: f4[16]; b: f4[16]?; c: f4[]; d: Set<i4>[3]; }
                                               service Api() { Get(): M; }
                                               """);

        compiled.AssertAccepted();

        var fields = compiled.Lock().Definitions["M"].Fields!;

        Assert.Multiple(() =>
        {
            Assert.That(fields[0].Type, Is.EqualTo("Array<f4, 16>"));
            Assert.That(fields[1].Type, Is.EqualTo("Maybe<Array<f4, 16>>"));
            Assert.That(fields[2].Type, Is.EqualTo("Array<f4>"), "an unsized array keeps its old spelling");
            Assert.That(fields[3].Type, Is.EqualTo("Array<Set<i4>, 3>"));
        });
    }

    /// <summary>
    /// Changing <c>N</c> changes how many elements every existing reader consumes, so it is ION0022 —
    /// the breaking change that would have been invisible while <c>Array&lt;f4&gt;</c> and
    /// <c>Array&lt;f4, 16&gt;</c> printed identically.
    /// </summary>
    [Test]
    public void Lock_ChangingTheSize_IsABreakingChange()
    {
        var before = LanguageFeature.Compile("msg M { a: f4[16]; }\nservice Api() { Get(): M; }");
        before.AssertAccepted();

        var after = LanguageFeature.Compile("msg M { a: f4[8]; }\nservice Api() { Get(): M; }", before.Lock());

        Assert.That(after.WithCode("ION0022"), Has.Count.EqualTo(1), after.Describe);
        Assert.That(after.WithCode("ION0022")[0].Message,
            Does.Contain("changed type from 'Array<f4, 16>' to 'Array<f4, 8>'"));
    }

    /// <summary>Adding or removing the size at all is the same breaking change.</summary>
    [TestCase("f4[16]", "f4[]", "'Array<f4, 16>' to 'Array<f4>'")]
    [TestCase("f4[]", "f4[16]", "'Array<f4>' to 'Array<f4, 16>'")]
    public void Lock_AddingOrRemovingTheSize_IsABreakingChange(string before, string after, string fragment)
    {
        var first = LanguageFeature.Compile($"msg M {{ a: {before}; }}\nservice Api() {{ Get(): M; }}");
        first.AssertAccepted();

        var second = LanguageFeature.Compile($"msg M {{ a: {after}; }}\nservice Api() {{ Get(): M; }}",
            first.Lock());

        Assert.That(second.WithCode("ION0022"), Has.Count.EqualTo(1), second.Describe);
        Assert.That(second.WithCode("ION0022")[0].Message, Does.Contain(fragment));
    }

    /// <summary>An unchanged schema recompiles against its own lock in silence.</summary>
    [Test]
    public void Lock_AnUnchangedFixedArray_IsNotABreakingChange()
    {
        const string source = "msg M { a: f4[16]; b: Set<i4>[3]; }\nservice Api() { Get(): M; }";

        var first = LanguageFeature.Compile(source);
        first.AssertAccepted();

        LanguageFeature.Compile(source, first.Lock()).AssertAccepted();
    }

    // ═══════════════════════════════════════════════════════════════════
    // CYCLES — T[N] is the one owned collection
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>T[N]</c> with <c>N &gt;= 1</c> holds N values of T unconditionally, so a cycle through one
    /// never bottoms out. This is exactly the asymmetry a naive "arrays break cycles" rule gets wrong.
    /// </summary>
    [TestCase("Node[1]", TestName = "Cycle_SelfFixedOne")]
    [TestCase("Node[4]", TestName = "Cycle_SelfFixedFour")]
    public void FixedArray_OfItself_IsACycle(string type)
    {
        var compiled = LanguageFeature.Compile($"msg Node {{ kids: {type}; }}");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.CircularType }), compiled.Describe);
        Assert.That(compiled.Only(LanguageFeature.CircularType).Message, Does.Contain("Node → Node"));
    }

    /// <summary>An unsized array beside it stays clean — it may be empty, so the recursion can stop.</summary>
    [Test]
    public void UnsizedArray_OfItself_IsNotACycle()
        => LanguageFeature.Compile("msg Node { kids: Node[]; }")
            .AssertAccepted();

    [Test]
    public void FixedArray_MutualRecursion_IsACycle()
    {
        var compiled = LanguageFeature.Compile("msg A { b: B[2]; }\nmsg B { a: A[3]; }");

        Assert.That(compiled.WithCode(LanguageFeature.CircularType), Has.Count.EqualTo(1), compiled.Describe);
        Assert.That(compiled.Only(LanguageFeature.CircularType).Message, Does.Contain("A → B → A"));
    }

    /// <summary>
    /// A size of zero is already ION0062, and a second bogus ION0030 stacked on it would send the
    /// author looking for a cycle instead of at the size.
    /// </summary>
    [Test]
    public void FixedArray_WithABadSize_DoesNotAlsoReportACycle()
    {
        var compiled = LanguageFeature.Compile("msg Node { kids: Node[0]; }");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.FixedArraySize }), compiled.Describe);
    }

    /// <summary>
    /// A cycle-breaking wrapper anywhere on the path is enough, including outside the fixed array.
    /// </summary>
    [TestCase("Node[4]?", TestName = "Cycle_OptionalOutsideFixed")]
    [TestCase("Node?[4]", TestName = "Cycle_OptionalInsideFixed")]
    [TestCase("Node~[4]", TestName = "Cycle_PartialInsideFixed")]
    [TestCase("Set<Node[4]>", TestName = "Cycle_FixedInsideSet")]
    [TestCase("Map<string, Node[4]>", TestName = "Cycle_FixedInsideMap")]
    public void FixedArray_UnderACycleBreaker_IsNotACycle(string type)
    {
        var compiled = LanguageFeature.Compile($"msg Node {{ kids: {type}; }}");

        Assert.That(compiled.WithCode(LanguageFeature.CircularType), Is.Empty, compiled.Describe);
    }

    /// <summary>
    /// <c>Node[4][]</c> does <em>not</em> break the cycle, and the reason is worth pinning: a
    /// repeated array suffix is unrepresentable (ION0019) and collapses to a single wrapper, which
    /// keeps the size — so the type is <c>Array&lt;Node, 4&gt;</c> and still owned. The ION0030 that
    /// follows is a consequence of the ION0019, not a second independent mistake, but it does mean
    /// the "an array breaks the cycle" advice in the ION0030 text is wrong for this one spelling.
    /// </summary>
    [Test]
    public void RepeatedArraySuffix_CollapsesOntoTheSizeAndStaysACycle()
    {
        var compiled = LanguageFeature.Compile("msg Node { kids: Node[4][]; }");

        Assert.That(compiled.ErrorCodes,
            Is.EquivalentTo(new[] { "ION0019", LanguageFeature.CircularType }), compiled.Describe);
    }
}
