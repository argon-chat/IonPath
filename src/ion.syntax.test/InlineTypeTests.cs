namespace ion.syntax.test;

using Pidgin;

/// <summary>
/// Coverage for inline anonymous types.
/// <code>
/// inlineMsg := "msg" &amp;'{' fieldList
/// </code>
/// <para>
/// An inline <c>msg { … }</c> is a type reference like any other: it composes with every modifier
/// suffix and is legal in every position a type may be written. It carries no name — the compiler
/// hoists it to a derived one — so <c>IonUnderlyingTypeSyntax.Name</c> is the unlexable
/// <c>$inline</c> placeholder and <c>InlineBody</c> is what a consumer must branch on.
/// </para>
/// </summary>
public class InlineTypeTests
{
    private static IonUnderlyingTypeSyntax FieldType(string written)
    {
        var result = IonParser.Message.Parse($"msg M {{ a: {written}; }}");
        Assert.That(result.Success, Is.True, () => $"parse of '{written}' failed: {result.Error}");
        return ((IonMessageSyntax)result.Value).Fields.Single().Type;
    }

    #region the shape

    [Test]
    public void InlineType_OnAField()
    {
        var msg = (IonMessageSyntax)IonParser.Message.ParseOrThrow("""
                                                                   msg Order {
                                                                       id: guid;
                                                                       shipping: msg { address: string; postcode: string; };
                                                                   }
                                                                   """);

        var shipping = msg.Fields[1].Type;
        Assert.Multiple(() =>
        {
            Assert.That(msg.Fields[0].Type.InlineBody, Is.Null, "an ordinary reference is not inline");
            Assert.That(shipping.IsInline, Is.True);
            Assert.That(shipping.Name.Identifier, Is.EqualTo(IonUnderlyingTypeSyntax.InlineTypeName));
            Assert.That(shipping.InlineBody!.Fields.Select(f => f.Name.Identifier),
                Is.EqualTo(new[] { "address", "postcode" }));
        });
    }

    [Test]
    public void InlineType_WithAnEmptyBody()
    {
        var type = FieldType("msg { }");

        Assert.That(type.IsInline, Is.True);
        Assert.That(type.InlineBody!.Fields, Is.Empty);
    }

    /// <summary>
    /// The body is the <c>msg</c> body production, so field doc comments and attributes work.
    /// </summary>
    [Test]
    public void InlineTypeFields_CarryDocCommentsAndAttributes()
    {
        var type = FieldType("""
                             msg {
                                 /// The street address.
                                 @idx(1)
                                 address: string;
                             }
                             """);

        var field = type.InlineBody!.Fields.Single();
        Assert.Multiple(() =>
        {
            Assert.That(field.Comments, Is.EqualTo("The street address."));
            Assert.That(field.Attributes.Single().Name.Identifier, Is.EqualTo("idx"));
        });
    }

    #endregion

    #region composition with modifiers

    [TestCase("msg { at: datetime; }[]", TestName = "Inline_Array")]
    [TestCase("msg { at: datetime; }?", TestName = "Inline_Optional")]
    [TestCase("msg { at: datetime; }~", TestName = "Inline_Partial")]
    [TestCase("msg { at: datetime; }[4]", TestName = "Inline_FixedArray")]
    [TestCase("msg { at: datetime; }~[4]?", TestName = "Inline_EveryModifier")]
    public void InlineType_ComposesWithEveryModifier(string written)
    {
        Assert.That(FieldType(written).IsInline, Is.True);
    }

    [Test]
    public void InlineType_ArrayOfInline()
    {
        var msg = (IonMessageSyntax)IonParser.Message.ParseOrThrow("""
                                                                   msg Order {
                                                                       history: msg { at: datetime; note: string; }[];
                                                                   }
                                                                   """);

        var type = msg.Fields.Single().Type;
        Assert.Multiple(() =>
        {
            Assert.That(type.IsInline, Is.True);
            Assert.That(type.IsArray, Is.True);
            Assert.That(type.ModifierTokens, Is.EqualTo(new[] { "[]" }));
            Assert.That(type.InlineBody!.Fields, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void InlineType_FixedArrayCarriesItsSize()
    {
        var type = FieldType("msg { at: datetime; }[4]");

        Assert.That(type.ArraySize, Is.EqualTo(4));
        Assert.That(type.IsInline, Is.True);
    }

    [Test]
    public void InlineType_StackedModifiersAreRecordedInOrder()
    {
        var type = FieldType("msg { a: i4; }~[]?");

        Assert.That(type.ModifierTokens, Is.EqualTo(new[] { "~", "[]", "?" }));
    }

    /// <summary>
    /// The modifier machinery does not know inline types exist, so a repeat on one is diagnosed the
    /// same way a repeat on a named type is.
    /// </summary>
    [Test]
    public void InlineType_RepeatedModifierIsStillRepresented()
    {
        Assert.That(FieldType("msg { a: i4; }[][]").ModifierTokens, Is.EqualTo(new[] { "[]", "[]" }));
    }

    #endregion

    #region every type position

    [TestCase("msg M { a: msg { x: i4; }; }", TestName = "Inline_OnAMessageField")]
    [TestCase("mixin X { a: msg { x: i4; }; }", TestName = "Inline_OnAMixinField")]
    [TestCase("service S(a: msg { x: i4; }) { m(): i4; }", TestName = "Inline_OnAServiceBaseArgument")]
    [TestCase("service S() { m(a: msg { x: i4; }): i4; }", TestName = "Inline_OnAMethodArgument")]
    [TestCase("service S() { m(): msg { x: i4; }; }", TestName = "Inline_OnAMethodReturn")]
    [TestCase("union U { Ok(a: msg { x: i4; }) }", TestName = "Inline_OnAUnionCaseField")]
    [TestCase("attribute @a(x: msg { y: i4; });", TestName = "Inline_OnAnAttributeParameter")]
    [TestCase("typedef Anon = msg { x: i4; };", TestName = "Inline_OnATypedefBase")]
    [TestCase("msg M { a: Array<msg { x: i4; }>; }", TestName = "Inline_AsAGenericArgument")]
    public void InlineType_IsAcceptedInEveryTypePosition(string source)
    {
        // Uniformly accepted wherever `type` appears, rather than allow-listed per position. The
        // grammar already parses a typedef's *name* side and an enum's base type with the full type
        // parser for the same reason: a meaningless form reaches the compiler and gets a targeted
        // diagnostic instead of a bare parse error.
        var result = IonParser.IonFile.Parse(source);

        Assert.That(result.Success, Is.True, () => $"{result.Error}");
    }

    [Test]
    public void InlineType_AsAGenericArgument_IsReachableFromTheArgumentList()
    {
        var argument = FieldType("Array<msg { x: i4; }>").generics.Single();

        Assert.Multiple(() =>
        {
            Assert.That(argument.Name.Identifier, Is.EqualTo(IonUnderlyingTypeSyntax.InlineTypeName));
            Assert.That(argument.Type!.InlineBody!.Fields.Single().Name.Identifier, Is.EqualTo("x"));
        });
    }

    #endregion

    #region nesting

    /// <summary>
    /// Nesting an inline type inside an inline type is <b>supported</b>, not diagnosed: the field
    /// list of an inline body is the same production one level down, so it falls out of the
    /// construction, and refusing it would need a second type parser that could drift from this one.
    /// It is bounded by <see cref="IonParser.MaxTypeNestingDepth"/> like every other nesting.
    /// </summary>
    [Test]
    public void InlineType_NestedInsideAnInlineType_IsSupported()
    {
        var type = FieldType("msg { inner: msg { deep: msg { x: i4; }; }; }");

        var level1 = type.InlineBody!.Fields.Single().Type;
        var level2 = level1.InlineBody!.Fields.Single().Type;

        Assert.Multiple(() =>
        {
            Assert.That(type.IsInline, Is.True);
            Assert.That(level1.IsInline, Is.True);
            Assert.That(level2.IsInline, Is.True);
            Assert.That(level2.InlineBody!.Fields.Single().Name.Identifier, Is.EqualTo("x"));
        });
    }

    [Test]
    public void InlineType_NestedThroughAGenericArgument()
    {
        var type = FieldType("msg { inner: Array<msg { x: i4; }>; }");

        var inner = type.InlineBody!.Fields.Single().Type.generics.Single().Type!;
        Assert.That(inner.IsInline, Is.True);
    }

    private static string NestedInline(int levels)
    {
        var text = "i4";
        for (var i = 0; i < levels; i++)
            text = $"msg {{ a: {text}; }}";
        return text;
    }

    [Test]
    public void NestedInlineTypes_UpToTheLimit_Parse()
    {
        var type = FieldType(NestedInline(IonParser.MaxTypeNestingDepth));

        var depth = 0;
        for (var t = type; t.InlineBody is not null; t = t.InlineBody.Fields.Single().Type)
            depth++;

        Assert.That(depth, Is.EqualTo(IonParser.MaxTypeNestingDepth));
    }

    [Test]
    public void NestedInlineTypes_OneLevelTooDeep_IsAParseError()
    {
        var result = IonParser.Message.Parse($"msg M {{ a: {NestedInline(IonParser.MaxTypeNestingDepth + 1)}; }}");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ToString(), Does.Contain("nested more than"));
    }

    /// <summary>
    /// Inline bodies and generic arguments share one depth budget, because they compose: an
    /// argument may be an inline type whose field is a generic. Two budgets would bound nothing.
    /// </summary>
    [Test]
    public void InlineAndGenericNesting_ShareOneBudget()
    {
        var half = IonParser.MaxTypeNestingDepth / 2;
        var text = "i4";
        for (var i = 0; i < half; i++)
            text = $"Array<msg {{ a: {text}; }}>";

        Assert.That(IonParser.Message.Parse($"msg M {{ a: {text}; }}").Success, Is.True,
            $"{half} inline levels plus {half} generic levels is exactly the budget");
    }

    /// <summary>
    /// The reason the type parser is a finite chain and not a self-referential parser: a
    /// <see cref="StackOverflowException"/> is uncatchable on .NET and would kill the test host.
    /// </summary>
    [Test]
    public void PathologicallyNestedInlineTypes_FailFastWithoutOverflowingTheStack()
    {
        var input = "msg M { a: " + string.Concat(Enumerable.Repeat("msg { a: ", 100_000)) + "; }";

        Assert.That(ParseBudget.Within(() => IonParser.Message.Parse(input).Success), Is.False);
    }

    [Test]
    public void PathologicallyManyOpenBraces_FailFast()
    {
        var input = "msg M { a: msg " + new string('{', 100_000);

        Assert.That(ParseBudget.Within(() => IonParser.Message.Parse(input).Success), Is.False);
    }

    #endregion

    #region backwards compatibility of the `msg` keyword in type position

    /// <summary>
    /// The inline alternative commits only once a <c>{</c> is in sight, so a reference to a type
    /// that happens to be spelled <c>msg</c> parses exactly as it did before inline types existed.
    /// </summary>
    [Test]
    public void ATypeNamedMsg_StillParsesAsANamedReference()
    {
        var type = FieldType("msg");

        Assert.Multiple(() =>
        {
            Assert.That(type.IsInline, Is.False);
            Assert.That(type.Name.Identifier, Is.EqualTo("msg"));
        });
    }

    [TestCase("msgs", TestName = "MsgPrefix_LongerIdentifier")]
    [TestCase("message", TestName = "MsgPrefix_Message")]
    [TestCase("msg_body", TestName = "MsgPrefix_Underscore")]
    public void AnIdentifierMerelyStartingWithMsg_IsNotAnInlineType(string written)
    {
        var type = FieldType(written);

        Assert.That(type.IsInline, Is.False);
        Assert.That(type.Name.Identifier, Is.EqualTo(written));
    }

    /// <summary>
    /// An inline type takes no <c>with</c> clause — deliberately, because keeping the commit point
    /// one character wide is what makes the guarantee above cheap.
    /// </summary>
    [Test]
    public void InlineType_TakesNoWithClause()
    {
        Assert.That(IonParser.Message.Parse("msg M { a: msg with Audited { x: i4; }; }").Success, Is.False);
    }

    #endregion

    #region malformed

    [TestCase("msg M { a: msg { x i4; }; }", TestName = "Inline_MalformedField")]
    [TestCase("msg M { a: msg { x: i4; }", TestName = "Inline_UnterminatedOuterBody")]
    [TestCase("msg M { a: msg { x: i4; ; }", TestName = "Inline_UnterminatedInlineBody")]
    [TestCase("msg M { a: msg Named { x: i4; }; }", TestName = "Inline_WithAName")]
    public void MalformedInlineType_IsAParseError(string source)
    {
        Assert.That(IonParser.IonFile.Parse(source).Success, Is.False);
    }

    [Test]
    public void ABrokenInlineType_DoesNotSwallowTheRestOfTheFile()
    {
        var file = IonParser.Parse("test", """
                                           msg Broken { a: msg { x i4; }; }

                                           msg Fine { b: i4; }
                                           """);

        Assert.Multiple(() =>
        {
            Assert.That(file.messageSyntaxes.Select(m => m.Name.Identifier), Does.Contain("Fine"));
            Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Not.Empty);
        });
    }

    #endregion

    #region trivia

    [Test]
    public void Comments_AroundAndInsideAnInlineBody()
    {
        var type = FieldType("""
                             msg /* a */ {
                                 // b
                                 address: string; /* c */
                                 /// dangling before the brace
                             } /* d */ []
                             """);

        Assert.Multiple(() =>
        {
            Assert.That(type.IsInline, Is.True);
            Assert.That(type.InlineBody!.Fields.Single().Name.Identifier, Is.EqualTo("address"));
            Assert.That(type.IsArray, Is.True);
        });
    }

    [Test]
    public void WhitespaceBetweenTheBodyAndTheTerminator()
    {
        var type = FieldType("msg { x: i4; }   ");

        Assert.That(type.IsInline, Is.True);
    }

    #endregion

    #region positions

    /// <summary>
    /// The body spans <c>msg</c> to the closing <c>}</c>, so the hoisting pass can name the derived
    /// type after where it was written and a diagnostic can anchor on the construct itself.
    /// </summary>
    [Test]
    public void InlineBody_SpansTheKeywordToTheClosingBrace()
    {
        var type = FieldType("msg { x: i4; }");

        Assert.Multiple(() =>
        {
            // `msg M { a: msg { x: i4; }; }` — the inline `msg` is col 12, the `}` ends at col 26.
            Assert.That(type.StartPosition.Col, Is.EqualTo(12), "the reference starts at the keyword");
            Assert.That(type.InlineBody!.StartPosition.Col, Is.EqualTo(12));
            Assert.That(type.InlineBody!.EndPosition!.Value.Col, Is.EqualTo(26));
        });
    }

    [Test]
    public void NestedInlineBodies_CarryDistinctPositions()
    {
        var type = FieldType("msg { inner: msg { x: i4; }; }");
        var inner = type.InlineBody!.Fields.Single().Type;

        Assert.That(inner.InlineBody!.StartPosition.Col,
            Is.GreaterThan(type.InlineBody!.StartPosition.Col));
    }

    #endregion
}
