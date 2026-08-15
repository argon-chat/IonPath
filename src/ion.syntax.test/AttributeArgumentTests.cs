namespace ion.syntax.test;

using System.Diagnostics;
using System.Numerics;
using Pidgin;

/// <summary>
/// Coverage for the attribute use site and the attribute declaration's <c>on</c> clause
/// (<c>src/ion.syntax/Ion.Attributes.cs</c>).
/// <code>
/// use          := '@' identifier ( '(' ( arg ( ',' arg )* )? ')' )?
/// arg          := ( identifier ':' )? literal
/// declaration  := "attribute" '@' identifier argList ( "on" target ( ',' target )* )? ';'
/// </code>
/// Pinned decisions: no trailing comma; a positional argument after a named one is accepted by the
/// grammar (so the semantic layer can diagnose it precisely); <c>@Foo</c> and <c>@Foo()</c> both
/// mean zero arguments; omitting <c>on</c> means "any target" and yields
/// <see langword="null"/> <see cref="IonAttributeDefSyntax.Targets"/>.
/// </summary>
public class AttributeArgumentTests
{
    #region helpers

    /// <summary>Parses a whole file and returns the attributes on its single message.</summary>
    private static List<IonAttributeArgumentSyntax> ArgsOf(string attributeSource)
    {
        var source = attributeSource + "\nmsg M { x: u4; }";
        var result = IonParser.IonFile.Parse(source);
        Assert.That(result.Success, Is.True, () => $"parse failed: {result.Error}");

        var attr = result.Value.OfType<IonMessageSyntax>().Single().Attributes.Single();
        return attr.Args;
    }

    private static void RejectAttribute(string attributeSource)
    {
        var source = attributeSource + "\nmsg M { x: u4; }";
        var result = IonParser.IonFile.Parse(source);

        Assert.That(result.Success, Is.False,
            () => $"expected `{attributeSource}` to be rejected");
    }

    private static IonAttributeDefSyntax Decl(string source)
    {
        var result = IonParser.AttributeDef.Parse(source);
        Assert.That(result.Success, Is.True, () => $"parse failed: {result.Error}");
        return result.Value;
    }

    private static void RejectDecl(string source)
    {
        var result = IonParser.AttributeDef.Before(Parser<char>.End).Parse(source);

        Assert.That(result.Success, Is.False, () => $"expected `{source}` to be rejected");
    }

    private static BigInteger Int(IonAttributeArgumentSyntax arg)
        => ((IonIntegerLiteralSyntax)arg.Value).Value;

    private static string Str(IonAttributeArgumentSyntax arg)
        => ((IonStringLiteralSyntax)arg.Value).Value;

    #endregion

    #region zero arguments

    /// <summary><c>@Foo</c> and <c>@Foo()</c> are both legal and both mean zero arguments.</summary>
    [TestCase("@Foo", TestName = "ZeroArgs_NoParens")]
    [TestCase("@Foo()", TestName = "ZeroArgs_EmptyParens")]
    [TestCase("@Foo(   )", TestName = "ZeroArgs_WhitespaceOnly")]
    [TestCase("@Foo(\n)", TestName = "ZeroArgs_NewlineOnly")]
    [TestCase("@Foo(/* nothing */)", TestName = "ZeroArgs_CommentOnly")]
    [TestCase("@Foo(// nothing\n)", TestName = "ZeroArgs_LineCommentOnly")]
    public void ZeroArguments(string source) => Assert.That(ArgsOf(source), Is.Empty);

    [Test]
    public void ZeroArguments_NoParens_StillCarriesName()
    {
        var result = IonParser.IonFile.Parse("@Foo\nmsg M { x: u4; }");
        Assert.That(result.Success, Is.True, () => $"parse failed: {result.Error}");

        var attr = result.Value.OfType<IonMessageSyntax>().Single().Attributes.Single();

        Assert.Multiple(() =>
        {
            Assert.That(attr.Name.Identifier, Is.EqualTo("Foo"));
            Assert.That(attr.Args, Is.Empty);
        });
    }

    #endregion

    #region positional / named / mixed

    [Test]
    public void Positional_Single()
    {
        var args = ArgsOf("@Retry(3)");

        Assert.Multiple(() =>
        {
            Assert.That(args, Has.Count.EqualTo(1));
            Assert.That(args[0].Name, Is.Null, "a positional argument has a null Name");
            Assert.That(Int(args[0]), Is.EqualTo(new BigInteger(3)));
        });
    }

    [Test]
    public void Named_Single()
    {
        var args = ArgsOf("@Retry(maxAttempts: 3)");

        Assert.Multiple(() =>
        {
            Assert.That(args, Has.Count.EqualTo(1));
            Assert.That(args[0].Name!.Identifier, Is.EqualTo("maxAttempts"));
            Assert.That(Int(args[0]), Is.EqualTo(new BigInteger(3)));
        });
    }

    [Test]
    public void Mixed_PositionalThenNamed()
    {
        var args = ArgsOf("""@Cache(30, key: "x")""");

        Assert.Multiple(() =>
        {
            Assert.That(args, Has.Count.EqualTo(2));
            Assert.That(args[0].Name, Is.Null);
            Assert.That(Int(args[0]), Is.EqualTo(new BigInteger(30)));
            Assert.That(args[1].Name!.Identifier, Is.EqualTo("key"));
            Assert.That(Str(args[1]), Is.EqualTo("x"));
        });
    }

    /// <summary>
    /// The C# ordering rule (once named, always named) is a <em>semantic</em> diagnostic.
    /// The grammar has to represent the mistake faithfully so the semantic layer can say
    /// "positional arguments must precede named arguments" instead of the parser dying with
    /// "expected ')'".
    /// </summary>
    [Test]
    public void PositionalAfterNamed_IsRepresentable()
    {
        var args = ArgsOf("""@Cache(key: "x", 30)""");

        Assert.Multiple(() =>
        {
            Assert.That(args, Has.Count.EqualTo(2));
            Assert.That(args[0].Name!.Identifier, Is.EqualTo("key"));
            Assert.That(args[1].Name, Is.Null, "the offending positional argument is preserved");
            Assert.That(Int(args[1]), Is.EqualTo(new BigInteger(30)));
        });
    }

    /// <summary>Every literal form is usable as an argument.</summary>
    [Test]
    public void EveryLiteralForm_IsAcceptedAsAnArgument()
    {
        var args = ArgsOf("""@All(1, -2, 0xFF, 1.5, "s", true, false, null, Status.Active, [1, 2], [])""");

        Assert.That(args.Select(a => a.Value.GetType()), Is.EqualTo(new[]
        {
            typeof(IonIntegerLiteralSyntax),
            typeof(IonIntegerLiteralSyntax),
            typeof(IonIntegerLiteralSyntax),
            typeof(IonFloatLiteralSyntax),
            typeof(IonStringLiteralSyntax),
            typeof(IonBoolLiteralSyntax),
            typeof(IonBoolLiteralSyntax),
            typeof(IonNullLiteralSyntax),
            typeof(IonEnumRefLiteralSyntax),
            typeof(IonArrayLiteralSyntax),
            typeof(IonArrayLiteralSyntax)
        }));
    }

    /// <summary>
    /// An <c>i4[]</c> parameter is already declarable, so an array argument has to lex properly —
    /// this is the bug class the raw span silently mishandled.
    /// </summary>
    [Test]
    public void ArrayArgument_IsOneArgumentNotThree()
    {
        var args = ArgsOf("@Idx([1, 2, 3])");

        Assert.Multiple(() =>
        {
            Assert.That(args, Has.Count.EqualTo(1), "the commas inside [] must not split the list");
            Assert.That(((IonArrayLiteralSyntax)args[0].Value).Items, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void NamedArrayArgument()
    {
        var args = ArgsOf("@Idx(cols: [1, 2], name: \"k\")");

        Assert.Multiple(() =>
        {
            Assert.That(args, Has.Count.EqualTo(2));
            Assert.That(args[0].Name!.Identifier, Is.EqualTo("cols"));
            Assert.That(((IonArrayLiteralSyntax)args[0].Value).Items, Has.Count.EqualTo(2));
            Assert.That(Str(args[1]), Is.EqualTo("k"));
        });
    }

    /// <summary>
    /// A named argument's value may itself start with an identifier, so the <c>name :</c> probe has
    /// to be atomic — otherwise <c>Status.Active</c> would be eaten as a (nameless) name.
    /// </summary>
    [Test]
    public void NamedArgument_WithEnumRefValue()
    {
        var args = ArgsOf("@State(initial: Status.Active)");

        var value = (IonEnumRefLiteralSyntax)args[0].Value;

        Assert.Multiple(() =>
        {
            Assert.That(args[0].Name!.Identifier, Is.EqualTo("initial"));
            Assert.That(value.TypeName.Identifier, Is.EqualTo("Status"));
            Assert.That(value.Member.Identifier, Is.EqualTo("Active"));
        });
    }

    [Test]
    public void PositionalEnumRef_IsNotMistakenForANamedArgument()
    {
        var args = ArgsOf("@State(Status.Active)");

        Assert.Multiple(() =>
        {
            Assert.That(args[0].Name, Is.Null);
            Assert.That(args[0].Value, Is.InstanceOf<IonEnumRefLiteralSyntax>());
        });
    }

    #endregion

    #region trivia inside the parens

    /// <summary>Trivia is legal in every position inside the parens.</summary>
    [TestCase("@Foo( 1 , 2 )", TestName = "Trivia_Spaces")]
    [TestCase("@Foo(\n  1,\n  2\n)", TestName = "Trivia_Newlines")]
    [TestCase("@Foo(/*a*/1/*b*/,/*c*/2/*d*/)", TestName = "Trivia_BlockCommentsEverywhere")]
    [TestCase("@Foo( // lead\n1, 2 // trail\n)", TestName = "Trivia_LineComments")]
    [TestCase("@Foo(1, 2 /** dangling doc */)", TestName = "Trivia_DanglingDocBeforeClose")]
    // Nothing inside the parens can carry documentation, so a doc comment in there is plain
    // trivia rather than a parse error.
    [TestCase("@Foo(/** d */ 1, 2)", TestName = "Trivia_DocAfterOpen")]
    [TestCase("@Foo(1 /** d */, 2)", TestName = "Trivia_DocBeforeComma")]
    [TestCase("@Foo(1, /** d */ 2)", TestName = "Trivia_DocAfterComma")]
    [TestCase("@Foo(/// d\n1, 2)", TestName = "Trivia_LineDocInside")]
    [TestCase("@Foo(\r\n1,\r\n2\r\n)", TestName = "Trivia_CrLf")]
    [TestCase("@Foo(1,\n\n\n2)", TestName = "Trivia_BlankLines")]
    public void Trivia_InEveryPosition(string source)
        => Assert.That(ArgsOf(source), Has.Count.EqualTo(2));

    [TestCase("@Foo(a /* x */ : 1)", TestName = "Trivia_BetweenNameAndColon")]
    [TestCase("@Foo(a : /* x */ 1)", TestName = "Trivia_BetweenColonAndValue")]
    [TestCase("@Foo(a\n:\n1)", TestName = "Trivia_NewlinesAroundColon")]
    public void Trivia_AroundTheNameColon(string source)
    {
        var args = ArgsOf(source);

        Assert.Multiple(() =>
        {
            Assert.That(args, Has.Count.EqualTo(1));
            Assert.That(args[0].Name!.Identifier, Is.EqualTo("a"));
        });
    }

    /// <summary>
    /// A <c>)</c> or <c>,</c> that only occurs inside a string or a comment must not terminate the
    /// argument list. This was the original raw-span bug and it stays fixed.
    /// </summary>
    [Test]
    public void CloseParenInsideAString()
    {
        var args = ArgsOf("""@Foo("a)b")""");

        Assert.Multiple(() =>
        {
            Assert.That(args, Has.Count.EqualTo(1));
            Assert.That(Str(args[0]), Is.EqualTo("a)b"));
        });
    }

    [Test]
    public void CommaInsideAString()
    {
        var args = ArgsOf("""@Foo("a,b")""");

        Assert.Multiple(() =>
        {
            Assert.That(args, Has.Count.EqualTo(1));
            Assert.That(Str(args[0]), Is.EqualTo("a,b"));
        });
    }

    [Test]
    public void CloseParenInsideAComment()
    {
        var args = ArgsOf("@Foo(/* ) */ 1 /* ) */)");

        Assert.Multiple(() =>
        {
            Assert.That(args, Has.Count.EqualTo(1));
            Assert.That(Int(args[0]), Is.EqualTo(BigInteger.One));
        });
    }

    [Test]
    public void CommaInsideAComment()
    {
        var args = ArgsOf("@Foo(1 /* , */ 	, 2)");

        Assert.That(args, Has.Count.EqualTo(2));
    }

    #endregion

    #region rejected argument shapes

    /// <summary>
    /// A trailing comma is rejected, matching every other comma separated list in Ion.
    /// </summary>
    [TestCase("@Foo(1,)", TestName = "Rejected_TrailingComma")]
    [TestCase("@Foo(,)", TestName = "Rejected_LoneComma")]
    [TestCase("@Foo(,1)", TestName = "Rejected_LeadingComma")]
    [TestCase("@Foo(1 2)", TestName = "Rejected_MissingComma")]
    [TestCase("@Foo(1,,2)", TestName = "Rejected_DoubleComma")]
    [TestCase("@Foo(a:)", TestName = "Rejected_NamedWithNoValue")]
    [TestCase("@Foo(: 1)", TestName = "Rejected_ColonWithNoName")]
    [TestCase("@Foo(a: b: 1)", TestName = "Rejected_TwoNames")]
    [TestCase("@Foo(bareIdentifier)", TestName = "Rejected_BareIdentifierArgument")]
    [TestCase("@Foo(1", TestName = "Rejected_UnclosedParens")]
    [TestCase("@Foo(", TestName = "Rejected_LoneOpenParen")]
    [TestCase("""@Foo("unterminated)""", TestName = "Rejected_UnterminatedString")]
    [TestCase("@Foo([1, 2)", TestName = "Rejected_UnterminatedArray")]
    public void Rejected(string source) => RejectAttribute(source);

    /// <summary>
    /// The old raw-span parser wrapped the parenthesised section in a <c>Try</c>, so a malformed
    /// argument list silently degraded to "attribute with no arguments". It must be an error now.
    /// </summary>
    [Test]
    public void MalformedArgumentList_DoesNotSilentlyBecomeZeroArguments()
    {
        var file = IonParser.Parse("attr", "@Foo(1 2)\nmsg M { x: u4; }");

        Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Not.Empty,
            "a malformed argument list must surface, not vanish");
    }

    #endregion

    #region source positions

    [Test]
    public void Argument_CarriesItsOwnPosition()
    {
        var args = ArgsOf("@Foo(1,\n     key: 22)");

        Assert.Multiple(() =>
        {
            Assert.That(args[0].StartPosition.Line, Is.EqualTo(1));
            Assert.That(args[0].StartPosition.Col, Is.EqualTo(6), "'1' is the 6th column");

            Assert.That(args[1].StartPosition.Line, Is.EqualTo(2));
            Assert.That(args[1].Name!.StartPosition.Col, Is.EqualTo(6), "'key' starts the argument");
            Assert.That(args[1].Value.StartPosition.Col, Is.EqualTo(11), "the value points past 'key: '");
            Assert.That(args[1].Value.StartPosition.Line, Is.EqualTo(2));
        });
    }

    #endregion

    #region `on` clause

    /// <summary>Omitting <c>on</c> means "any target" and is the pre-existing spelling.</summary>
    [Test]
    public void On_Absent_YieldsNullTargets()
        => Assert.That(Decl("attribute @Cache(duration: i4, key: string);").Targets, Is.Null);

    [Test]
    public void On_Absent_ZeroArgDeclaration()
    {
        var decl = Decl("attribute @AllowAnonymous();");

        Assert.Multiple(() =>
        {
            Assert.That(decl.Name.Identifier, Is.EqualTo("AllowAnonymous"));
            Assert.That(decl.Args, Is.Empty);
            Assert.That(decl.Targets, Is.Null);
        });
    }

    [Test]
    public void On_SingleTarget()
    {
        var decl = Decl("attribute @idx(n: u4) on field;");

        Assert.That(decl.Targets!.Select(t => t.Identifier), Is.EqualTo(new[] { "field" }));
    }

    [Test]
    public void On_ManyTargets()
    {
        var decl = Decl("attribute @idx(n: u4) on field, unionCase;");

        Assert.That(decl.Targets!.Select(t => t.Identifier), Is.EqualTo(new[] { "field", "unionCase" }));
    }

    /// <summary>
    /// The complete, settled keyword set. The grammar does not enforce it (see
    /// <see cref="On_UnknownTarget_IsRepresentable"/>), but every keyword in it must lex as one
    /// target, so this is the pin that catches a keyword being dropped or renamed.
    /// </summary>
    private static readonly string[] TargetKeywords =
    [
        "msg", "field", "enum", "flags", "enumMember", "union", "unionCase",
        "service", "method", "argument", "typedef", "attribute"
    ];

    [Test]
    public void On_EveryTargetKeyword()
    {
        var decl = Decl($"attribute @a(v: i4) on {string.Join(", ", TargetKeywords)};");

        Assert.That(decl.Targets!.Select(t => t.Identifier), Is.EqualTo(TargetKeywords));
    }

    /// <summary>Duplicates are representable; reporting them is the semantic layer's job.</summary>
    [Test]
    public void On_DuplicateTarget_IsAccepted()
    {
        var decl = Decl("attribute @a(v: i4) on field, field;");

        Assert.That(decl.Targets!.Select(t => t.Identifier), Is.EqualTo(new[] { "field", "field" }));
    }

    /// <summary>
    /// An unknown or mis-cased target is <em>not</em> a parse error. Failing here would turn the
    /// whole declaration into an <see cref="InvalidIonBlock"/> and cascade "attribute not declared"
    /// onto every use site; carrying the identifier through lets the semantic layer answer with
    /// "unknown attribute target 'x'" and keep the rest of the file intact. Same reasoning as the
    /// repeated-type-modifier case in <c>Ion.Messages.cs</c>.
    /// </summary>
    [TestCase("attribute @a(v: i4) on nonsense;", "nonsense", TestName = "On_UnknownKeyword")]
    [TestCase("attribute @a(v: i4) on Field;", "Field", TestName = "On_WrongCase")]
    [TestCase("attribute @a(v: i4) on fields;", "fields", TestName = "On_PluralTypo")]
    public void On_UnknownTarget_IsRepresentable(string source, string expected)
    {
        var decl = Decl(source);

        Assert.That(decl.Targets!.Select(t => t.Identifier), Is.EqualTo(new[] { expected }));
    }

    [Test]
    public void On_TargetsCarryPositions()
    {
        var decl = Decl("attribute @a(v: i4) on field, method;");

        Assert.Multiple(() =>
        {
            Assert.That(decl.Targets![0].StartPosition.Col, Is.EqualTo(24));
            Assert.That(decl.Targets![1].StartPosition.Col, Is.EqualTo(31));
        });
    }

    [TestCase("attribute @a(v: i4) on field /* x */, method;", TestName = "On_BlockComment")]
    [TestCase("attribute @a(v: i4)\n  on field,\n     method;", TestName = "On_Newlines")]
    [TestCase("attribute @a(v: i4) on // pick one\n field, method;", TestName = "On_LineComment")]
    public void On_Trivia(string source)
        => Assert.That(Decl(source).Targets, Has.Count.EqualTo(2));

    [TestCase("attribute @a(v: i4) on;", TestName = "On_NoTargets")]
    [TestCase("attribute @a(v: i4) on 42;", TestName = "On_NonIdentifierTarget")]
    [TestCase("attribute @a(v: i4) on field,;", TestName = "On_TrailingComma")]
    [TestCase("attribute @a(v: i4) on field method;", TestName = "On_MissingComma")]
    [TestCase("attribute @a(v: i4) on field", TestName = "On_MissingSemicolon")]
    [TestCase("attribute @a(v: i4) onfield;", TestName = "On_NoWordBoundary")]
    public void On_Rejected(string source) => RejectDecl(source);

    /// <summary>The <c>on</c> clause coexists with a doc comment and with leading attributes.</summary>
    [Test]
    public void On_WithDocComment()
    {
        var decl = Decl("""
                        /// Marks a wire index.
                        attribute @idx(n: u4) on field, unionCase;
                        """);

        Assert.Multiple(() =>
        {
            Assert.That(decl.Comments, Is.EqualTo("Marks a wire index."));
            Assert.That(decl.Targets!.Select(t => t.Identifier), Is.EqualTo(new[] { "field", "unionCase" }));
        });
    }

    [Test]
    public void On_DeclarationInsideAFile()
    {
        var file = IonParser.Parse("attr", """
                                           attribute @idx(n: u4) on field;
                                           msg M { x: u4; }
                                           """);

        Assert.Multiple(() =>
        {
            Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Empty);
            Assert.That(file.attributeDefSyntaxes.Single().Targets!.Single().Identifier, Is.EqualTo("field"));
        });
    }

    #endregion

    #region regression: spellings that exist in the repo today

    /// <summary>
    /// The exact attribute spellings that appear in the checked-in corpus. If any of these stops
    /// parsing, real contracts break.
    /// </summary>
    [TestCase("@Grain()\nservice S(@GrainId() id: guid) { Get(): i4; }", TestName = "Repo_GrainAndGrainId")]
    [TestCase("@Grain()\nservice MathInteraction(leftOperand: i4) { Add(r: i4): i4; }", TestName = "Repo_GrainOnService")]
    [TestCase("@Serializable()\n@Version(1)\nmsg M { x: u4; }", TestName = "Repo_SerializableAndVersion")]
    [TestCase("@Auth()\n@Log()\n@Retry(3)\nservice S() { P(d: string): bool; }", TestName = "Repo_AuthLogRetry")]
    [TestCase("attribute @AllowAnonymous();", TestName = "Repo_AllowAnonymousDecl")]
    [TestCase("attribute @Cache(duration: i4, key: string);", TestName = "Repo_CacheDecl")]
    [TestCase("attribute @mark(v: i4~);\nmsg M { a: i4; }", TestName = "Repo_MarkDeclWithModifier")]
    [TestCase("attribute @mark(v: i4?~);\nmsg M { a: i4; }", TestName = "Repo_MarkDeclWithTwoModifiers")]
    [TestCase("attribute @Foo(v: i4);\nmsg Foo { q: i8; }", TestName = "Repo_ShadowingDecl")]
    [TestCase("union Foo { A(x: i4), B(x: i4) }\nattribute @Foo(v: i4);", TestName = "Repo_UnionVsAttribute")]
    [TestCase("@deprecated\ntypedef UserId = u4;\nmsg User { id: UserId; }", TestName = "Repo_BareAttributeOnTypedef")]
    [TestCase("attribute @MachineIdOptional();", TestName = "Repo_MachineIdOptionalDecl")]
    public void Repo_SpellingsStillParse(string source)
    {
        var result = IonParser.IonFile.Parse(source);

        Assert.That(result.Success, Is.True, () => $"parse failed: {result.Error}");
    }

    /// <summary>
    /// The use site parser is reached through <c>LeadingSection</c> from every member kind, so an
    /// argument list has to lex identically in all of them — a field, an enum member, a flags
    /// member, a union case, a service, a method, a method argument, a service base argument,
    /// a typedef and a top-level message.
    /// </summary>
    [TestCase("@idx(1)\nmsg M { x: u4; }", TestName = "Position_Message")]
    [TestCase("msg M { @idx(1) x: u4; }", TestName = "Position_Field")]
    [TestCase("enum E { @idx(1) A, B }", TestName = "Position_EnumMember")]
    [TestCase("flags F : u4 { @idx(1) A = 1, B = 2 }", TestName = "Position_FlagsMember")]
    [TestCase("union U { @idx(1) A(x: i4), B(x: i4) }", TestName = "Position_UnionCase")]
    [TestCase("@idx(1)\nservice S() { M(): i4; }", TestName = "Position_Service")]
    [TestCase("service S() { @idx(1) M(): i4; }", TestName = "Position_Method")]
    [TestCase("service S() { M(@idx(1) x: i4): i4; }", TestName = "Position_MethodArgument")]
    [TestCase("service S(@idx(1) x: i4) { M(): i4; }", TestName = "Position_ServiceBaseArgument")]
    [TestCase("@idx(1)\ntypedef UserId = u4;", TestName = "Position_Typedef")]
    [TestCase("@idx(1)\nattribute @other(v: i4);", TestName = "Position_AttributeDeclaration")]
    public void ArgumentsLex_AtEveryMemberPosition(string source)
    {
        var result = IonParser.IonFile.Parse(source);
        Assert.That(result.Success, Is.True, () => $"parse failed: {result.Error}");

        var attrs = result.Value
            .SelectMany(CollectAttributes)
            .Where(a => a.Name.Identifier == "idx")
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(attrs, Has.Count.EqualTo(1), "the @idx attribute must be attached somewhere");
            Assert.That(attrs[0].Args, Has.Count.EqualTo(1));
            Assert.That(Int(attrs[0].Args[0]), Is.EqualTo(BigInteger.One));
        });
    }

    private static IEnumerable<IonAttributeSyntax> CollectAttributes(IonSyntaxMember member)
    {
        foreach (var a in member.Attributes)
            yield return a;

        var nested = member switch
        {
            IonMessageSyntax m => m.Fields.Cast<IonSyntaxMember>(),
            IonEnumSyntax e => e.Entries,
            IonFlagsSyntax f => f.Entries,
            IonUnionSyntax u => u.baseFields.Cast<IonSyntaxMember>().Concat(u.cases),
            IonServiceSyntax s => s.BaseArguments.Cast<IonSyntaxMember>().Concat(s.Methods),
            IonMethodSyntax me => me.arguments,
            IonUnionTypeCaseSyntax c => c.arguments,
            IonAttributeDefSyntax d => d.Args,
            _ => []
        };

        foreach (var child in nested)
        foreach (var a in CollectAttributes(child))
            yield return a;
    }

    /// <summary>
    /// The std attributes declared in <c>ion.compiler.runtime/IonModule.cs</c>, both as they are
    /// declared and as they are used.
    /// </summary>
    [TestCase("@tag(4)", 4)]
    [TestCase("@bits(8)", 8)]
    [TestCase("@deadline(30)", 30)]
    public void Repo_StdAttributeUses(string source, int expected)
    {
        var args = ArgsOf(source);

        Assert.Multiple(() =>
        {
            Assert.That(args, Has.Count.EqualTo(1));
            Assert.That(args[0].Name, Is.Null);
            Assert.That(Int(args[0]), Is.EqualTo(new BigInteger(expected)));
        });
    }

    [TestCase("attribute @builtin();")]
    [TestCase("attribute @scalar();")]
    [TestCase("attribute @tag(tagId: i4);")]
    [TestCase("attribute @deadline(time: i4);")]
    [TestCase("attribute @deprecated();")]
    [TestCase("attribute @internal();")]
    [TestCase("attribute @bits(bitCount: i4);")]
    public void Repo_StdAttributeDeclarations(string source)
        => Assert.That(Decl(source).Targets, Is.Null, "std attributes declare no `on` clause");

    /// <summary>
    /// The real integration fixtures under <c>src/tests/Contracts/Contracts</c> must keep parsing
    /// byte for byte.
    /// <para>
    /// This also used to pin "…and not one of them contains an <c>@</c>", which was true when the
    /// grammar landed and was the evidence that nothing in the corpus depended on the old raw-span
    /// behaviour. The corpus has since gained <c>AttributeInteraction.ion</c>, whose entire purpose
    /// is to exercise attributes, so that half is inverted rather than deleted: the corpus must now
    /// contain attributes, and every one of them must come back as a real node with a name and a
    /// fully parsed argument list. Silently degrading <c>@Foo(…)</c> to "an attribute with no
    /// arguments" is exactly what the raw-span parser did, and it is what this pins against.
    /// </para>
    /// </summary>
    [Test]
    public void Repo_RealContractFixturesStillParse()
    {
        var dir = FindContractsDirectory();
        var files = Directory.GetFiles(dir, "*.ion");

        Assert.That(files, Is.Not.Empty, $"no .ion fixtures under {dir}");

        var attributes = new List<(string File, IonAttributeSyntax Attribute)>();
        var declarations = 0;

        Assert.Multiple(() =>
        {
            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                var result = IonParser.IonFile.Parse(File.ReadAllText(path));

                Assert.That(result.Success, Is.True,
                    () => $"{name} failed to parse: {result.Error}");

                if (!result.Success)
                    continue;

                var members = result.Value.ToList();

                declarations += members.OfType<IonAttributeDefSyntax>().Count();
                attributes.AddRange(members.SelectMany(m => m.Attributes).Select(a => (name, a)));
            }
        });

        Assert.That(attributes, Is.Not.Empty,
            "no fixture uses an attribute any more. This assertion replaced the original "
            + "'the corpus contains no @' pin; if the corpus really has gone back to having none, "
            + "invert it back rather than deleting it.");

        Assert.Multiple(() =>
        {
            Assert.That(declarations, Is.GreaterThan(0),
                "the corpus uses attributes but declares none — an `attribute @x(...)` declaration "
                + "is the other half of the grammar and needs a fixture too");

            foreach (var (file, attribute) in attributes)
            {
                Assert.That(attribute.Name.Identifier, Is.Not.Empty, $"{file}: unnamed attribute");

                foreach (var argument in attribute.Args)
                    Assert.That(argument.Value, Is.Not.Null,
                        $"{file}: @{attribute.Name.Identifier} has an argument with no parsed value");
            }
        });
    }

    private static string FindContractsDirectory()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "tests", "Contracts", "Contracts");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        Assert.Fail("could not locate src/tests/Contracts/Contracts above the test directory");
        return string.Empty;
    }

    #endregion

    #region pathological input

    /// <summary>Malformed input comes back through error recovery; it never hangs or throws.</summary>
    [TestCase("@Foo(", TestName = "Pathological_UnclosedParen")]
    [TestCase("@Foo([", TestName = "Pathological_UnclosedBracket")]
    [TestCase("@Foo(\"", TestName = "Pathological_UnclosedString")]
    [TestCase("@Foo(1, 2", TestName = "Pathological_UnclosedAfterArgs")]
    [TestCase("attribute @a(v: i4) on", TestName = "Pathological_DanglingOn")]
    public void Pathological_RecoversWithoutHanging(string source)
    {
        var sw = Stopwatch.StartNew();

        Assert.That(() => IonParser.Parse("attr", source), Throws.Nothing);

        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void Pathological_TenThousandElementArrayArgument()
    {
        var source = "@Idx([" + string.Join(",", Enumerable.Range(0, 10_000)) + "])";

        var sw = Stopwatch.StartNew();
        var args = ArgsOf(source);
        sw.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(((IonArrayLiteralSyntax)args[0].Value).Items, Has.Count.EqualTo(10_000));
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)));
        });
    }

    [Test]
    public void Pathological_DeeplyNestedArrayArgumentIsBounded()
    {
        const int depth = IonParser.MaxLiteralNestingDepth + 1;
        var source = "@Idx(" + new string('[', depth) + new string(']', depth) + ")";

        var sw = Stopwatch.StartNew();
        var result = IonParser.IonFile.Parse(source + "\nmsg M { x: u4; }");
        sw.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)));
        });
    }

    [Test]
    public void Pathological_ThousandArgumentsIsFine()
    {
        var source = "@Many(" + string.Join(", ", Enumerable.Range(0, 1_000)) + ")";

        Assert.That(ArgsOf(source), Has.Count.EqualTo(1_000));
    }

    [Test]
    public void Pathological_HugeUnclosedBracketRunDoesNotCrash()
    {
        var source = "@Idx(" + new string('[', 100_000);

        var sw = Stopwatch.StartNew();

        Assert.That(() => IonParser.Parse("attr", source), Throws.Nothing);

        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(60)));
    }

    #endregion
}
