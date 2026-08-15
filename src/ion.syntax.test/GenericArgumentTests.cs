namespace ion.syntax.test;

using Pidgin;

/// <summary>
/// Coverage for generic argument lists at a type use site.
/// <code>
/// genericArgs := '&lt;' ( typeArg ( ',' typeArg )* )? '&gt;'
/// typeArg     := type ( ':' type ( ',' type )* )?
/// </code>
/// <para>
/// <c>Map&lt;K, V&gt;</c> and <c>Set&lt;T&gt;</c> need no grammar of their own — they are ordinary
/// generic references. What they did need is for an argument to be a <em>type</em> rather than a
/// bare identifier, which is what the nesting tests here pin.
/// </para>
/// </summary>
public class GenericArgumentTests
{
    private static IonUnderlyingTypeSyntax FieldType(string written)
    {
        var result = IonParser.Message.Parse($"msg M {{ a: {written}; }}");
        Assert.That(result.Success, Is.True, () => $"parse of '{written}' failed: {result.Error}");
        return ((IonMessageSyntax)result.Value).Fields.Single().Type;
    }

    private static ParseError<char>? FieldTypeError(string written)
    {
        var result = IonParser.Message.Parse($"msg M {{ a: {written}; }}");
        Assert.That(result.Success, Is.False, () => $"'{written}' was expected to fail but parsed");
        return result.Error;
    }

    #region Map and Set are ordinary generic references

    [Test]
    public void Map_WithTwoArguments()
    {
        var type = FieldType("Map<string, User>");

        Assert.That(type.Name.Identifier, Is.EqualTo("Map"));
        Assert.That(type.generics.Select(g => g.Name.Identifier), Is.EqualTo(new[] { "string", "User" }));
    }

    [Test]
    public void Set_WithOneArgument()
    {
        var type = FieldType("Set<Status>");

        Assert.That(type.Name.Identifier, Is.EqualTo("Set"));
        Assert.That(type.generics.Single().Name.Identifier, Is.EqualTo("Status"));
    }

    /// <summary>
    /// The regression this whole file exists for: a nested argument used to be a hard parse error.
    /// <c>Array&lt;User&gt;</c> read as the identifier <c>Array</c>, the argument list then looked
    /// for its <c>&gt;</c> and found <c>&lt;</c>, and the enclosing declaration died.
    /// </summary>
    [Test]
    public void Map_WithANestedGenericArgument()
    {
        var type = FieldType("Map<string, Array<User>>");

        Assert.Multiple(() =>
        {
            Assert.That(type.Name.Identifier, Is.EqualTo("Map"));
            Assert.That(type.generics, Has.Count.EqualTo(2));
            Assert.That(type.generics[0].Name.Identifier, Is.EqualTo("string"));
            Assert.That(type.generics[1].Name.Identifier, Is.EqualTo("Array"));
            Assert.That(type.generics[1].Type!.generics.Single().Name.Identifier, Is.EqualTo("User"));
        });
    }

    [Test]
    public void Map_IsOptional()
    {
        var type = FieldType("Map<string, User>?");

        Assert.Multiple(() =>
        {
            Assert.That(type.IsOptional, Is.True);
            Assert.That(type.generics, Has.Count.EqualTo(2));
            Assert.That(type.ModifierTokens, Is.EqualTo(new[] { "?" }));
        });
    }

    [Test]
    public void Set_IsAnArray()
    {
        var type = FieldType("Set<i4>[]");

        Assert.Multiple(() =>
        {
            Assert.That(type.IsArray, Is.True);
            Assert.That(type.ArraySize, Is.Null);
            Assert.That(type.generics.Single().Name.Identifier, Is.EqualTo("i4"));
        });
    }

    #endregion

    #region the whole argument is carried, not just its head name

    [Test]
    public void ArgumentModifiers_AreCarried()
    {
        var type = FieldType("Map<string, User?>");

        var value = type.generics[1].Type!;
        Assert.Multiple(() =>
        {
            Assert.That(value.Name.Identifier, Is.EqualTo("User"));
            Assert.That(value.IsOptional, Is.True);
            Assert.That(value.ModifierTokens, Is.EqualTo(new[] { "?" }));
        });
    }

    [Test]
    public void ArgumentArraySize_IsCarried()
    {
        var value = FieldType("Set<f4[16]>").generics.Single().Type!;

        Assert.Multiple(() =>
        {
            Assert.That(value.IsArray, Is.True);
            Assert.That(value.ArraySize, Is.EqualTo(16));
        });
    }

    /// <summary>
    /// <c>Type</c> is filled in by the parser for every argument, including a plain one — a consumer
    /// never has to reconstruct the argument from <c>Name</c>.
    /// </summary>
    [Test]
    public void PlainArgument_AlsoCarriesItsType()
    {
        var argument = FieldType("Set<Status>").generics.Single();

        Assert.That(argument.Type, Is.Not.Null);
        Assert.That(argument.Type!.Name.Identifier, Is.EqualTo("Status"));
    }

    #endregion

    #region arity is representable, not enforced

    [TestCase("Map<>", 0)]
    [TestCase("Map<string>", 1)]
    [TestCase("Map<a, b, c>", 3)]
    public void AnyArity_Parses(string written, int expected)
    {
        // The compiler diagnoses arity against the declaration. The grammar has to represent the
        // mistake for it to be able to, exactly as it does for a repeated modifier.
        Assert.That(FieldType(written).generics, Has.Count.EqualTo(expected));
    }

    #endregion

    #region trivia

    [Test]
    public void Comments_EverywhereInsideAnArgumentList()
    {
        var type = FieldType("""
                             Map /* a */ < /* b */ string /* c */ , /* d */
                                 // e
                                 Array < User > /* f */ > /* g */ []
                             """);

        Assert.Multiple(() =>
        {
            Assert.That(type.generics, Has.Count.EqualTo(2));
            Assert.That(type.generics[1].Type!.generics.Single().Name.Identifier, Is.EqualTo("User"));
            Assert.That(type.IsArray, Is.True);
        });
    }

    [Test]
    public void NewlinesInsideAnArgumentList()
    {
        var type = FieldType("""
                             Map<
                                 string,
                                 Array<
                                     User
                                 >
                             >
                             """);

        Assert.That(type.generics, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// A doc comment cannot attach to anything between <c>&lt;</c> and <c>&gt;</c>, so
    /// <c>SkipTriviaAll</c> before the closing token swallows a dangling one rather than dying on it.
    /// </summary>
    [Test]
    public void DanglingDocComment_BeforeTheClosingAngleBracket()
    {
        var type = FieldType("""
                             Map<string, User
                                 /// dangling
                             >
                             """);

        Assert.That(type.generics, Has.Count.EqualTo(2));
    }

    #endregion

    #region positions

    [Test]
    public void EachArgument_CarriesItsOwnSpan()
    {
        var type = FieldType("Map<string, Array<User>>");

        // `msg M { a: Map<string, Array<User>>; }` — column 1 is 'm' of msg.
        Assert.Multiple(() =>
        {
            Assert.That(type.generics[0].StartPosition.Col, Is.EqualTo(16), "'string' starts at col 16");
            Assert.That(type.generics[1].StartPosition.Col, Is.EqualTo(24), "'Array' starts at col 24");
            Assert.That(type.generics[1].EndPosition, Is.Not.Null);
        });
    }

    #endregion

    #region nesting depth

    private static string NestedGenerics(int levels) =>
        string.Concat(Enumerable.Repeat("A<", levels)) + "B" + new string('>', levels);

    [Test]
    public void DeeplyNestedGenerics_UpToTheLimit_Parse()
    {
        var type = FieldType(NestedGenerics(IonParser.MaxTypeNestingDepth));

        var depth = 0;
        for (var t = type; t.generics.Count > 0; t = t.generics[0].Type!)
            depth++;

        Assert.That(depth, Is.EqualTo(IonParser.MaxTypeNestingDepth));
    }

    [Test]
    public void DeeplyNestedGenerics_OneLevelTooDeep_IsAParseError()
    {
        var error = FieldTypeError(NestedGenerics(IonParser.MaxTypeNestingDepth + 1));

        Assert.That(error!.ToString(), Does.Contain("nested more than"));
    }

    /// <summary>
    /// The point of the finite chain: a pathological input has to fail as an ordinary parse error.
    /// A self-referential parser would recurse 100 000 deep and take the test host down with an
    /// uncatchable <see cref="StackOverflowException"/>.
    /// </summary>
    [Test]
    public void PathologicallyNestedGenerics_FailFastWithoutOverflowingTheStack()
    {
        var input = "msg M { a: " + string.Concat(Enumerable.Repeat("A<", 100_000)) + "; }";

        Assert.That(ParseBudget.Within(() => IonParser.Message.Parse(input).Success), Is.False);
    }

    #endregion

    #region a comma inside an argument list is not the enclosing list's separator

    /// <summary>
    /// Every comma-separated list in the language — arguments, union cases, enum members — now has
    /// to survive an argument list that contains commas of its own. It does because the type parser
    /// is greedy and consumes through its own <c>&gt;</c> before the enclosing separator is tried,
    /// but that was untestable while an argument could only be a bare identifier.
    /// </summary>
    [TestCase("service S(a: Map<i4, i4>, b: i4) { m(): i4; }", TestName = "InnerComma_InAServiceArgumentList")]
    [TestCase("service S() { m(a: Map<i4, i4>, b: i4): i4; }", TestName = "InnerComma_InAMethodArgumentList")]
    [TestCase("union U { Ok(a: Map<i4, i4>), Err(b: i4) }", TestName = "InnerComma_InAUnionCaseList")]
    [TestCase("msg M { a: Map<i4, i4>; b: i4; }", TestName = "InnerComma_BetweenFields")]
    [TestCase("attribute @a(x: Map<i4, i4>, y: i4);", TestName = "InnerComma_InAnAttributeDeclaration")]
    [TestCase("typedef T = Map<i4, i4>;", TestName = "InnerComma_InATypedef")]
    public void CommasInsideAnArgumentList_DoNotSplitTheEnclosingList(string source)
    {
        var result = IonParser.IonFile.Parse(source);

        Assert.That(result.Success, Is.True, () => $"{result.Error}");
    }

    [Test]
    public void InnerComma_InAServiceArgumentList_KeepsBothArguments()
    {
        var service = IonParser.Service.ParseOrThrow("service S(a: Map<i4, i4>, b: i4) { m(): i4; }");

        Assert.That(service.BaseArguments.Select(x => x.argName.Identifier), Is.EqualTo(new[] { "a", "b" }));
        Assert.That(service.BaseArguments[0].type.generics, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// A modifier written after a run of closing angle brackets. Character-level parsing means there
    /// is no <c>&gt;&gt;</c> token to split, but the suffix must still be found after them.
    /// </summary>
    [TestCase("Map<string, Array<User>>?", "?")]
    [TestCase("Map<string, Array<User>>[]", "[]")]
    [TestCase("Map<string, Array<User>>~", "~")]
    public void ModifierAfterNestedClosingBrackets(string written, string token)
    {
        Assert.That(FieldType(written).ModifierTokens, Is.EqualTo(new[] { token }));
    }

    #endregion

    #region the vestigial constraint tail

    /// <summary>
    /// <c>&lt;T: Base&gt;</c> is still accepted and still discarded, exactly as before full types
    /// became legal arguments. Pinned so that nobody "cleans it up" and breaks a source that has it.
    /// </summary>
    [Test]
    public void ConstraintTail_IsAcceptedAndDiscarded()
    {
        var type = FieldType("Box<T: Base>");

        Assert.Multiple(() =>
        {
            Assert.That(type.generics.Single().Name.Identifier, Is.EqualTo("T"));
            Assert.That(type.generics.Single().Type!.generics, Is.Empty);
        });
    }

    #endregion

    #region decimal is nothing special

    /// <summary>
    /// Feature 5 is "no grammar change". <c>decimal</c> is not a keyword, is not shadowed by one,
    /// and lexes as an ordinary identifier in every position a type name can appear.
    /// </summary>
    [TestCase("decimal")]
    [TestCase("decimal?")]
    [TestCase("decimal[]")]
    [TestCase("decimal[4]")]
    [TestCase("Array<decimal>")]
    [TestCase("Map<string, decimal>")]
    public void Decimal_LexesAsAnOrdinaryTypeName(string written)
    {
        Assert.That(FieldType(written).Name.Identifier, Does.StartWith("decimal").Or.EqualTo("Array")
            .Or.EqualTo("Map"));
    }

    [Test]
    public void Decimal_IsUsableAsAFieldNameAndADeclarationName()
    {
        var result = IonParser.IonFile.Parse("msg decimal { decimal: decimal; }\n");

        Assert.That(result.Success, Is.True, () => $"{result.Error}");
        var msg = result.Value.OfType<IonMessageSyntax>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(msg.Name.Identifier, Is.EqualTo("decimal"));
            Assert.That(msg.Fields.Single().Name.Identifier, Is.EqualTo("decimal"));
            Assert.That(msg.Fields.Single().Type.Name.Identifier, Is.EqualTo("decimal"));
        });
    }

    #endregion
}
