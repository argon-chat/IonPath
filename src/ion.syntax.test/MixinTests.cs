namespace ion.syntax.test;

using Pidgin;

/// <summary>
/// Coverage for <c>mixin</c> declarations and the <c>with</c> clause.
/// <code>
/// mixin      := leading "mixin" identifier withClause? fieldList
/// message    := leading "msg"   identifier withClause? fieldList
/// withClause := "with" identifier ( ',' identifier )*
/// </code>
/// </summary>
public class MixinTests
{
    private static T ParseOne<T>(Parser<char, T> parser, string input)
    {
        var result = parser.Parse(input);
        Assert.That(result.Success, Is.True, () => $"parse failed: {result.Error}");
        return result.Value;
    }

    private static IonFileSyntax ParseFile(string input)
    {
        var result = IonParser.IonFile.Parse(input);
        Assert.That(result.Success, Is.True, () => $"parse failed: {result.Error}");
        return IonParser.Parse("test", input);
    }

    #region the declaration

    [Test]
    public void Mixin_WithFields()
    {
        var mixin = ParseOne(IonParser.Mixin, """
                                              mixin Audited {
                                                  createdAt: datetime;
                                                  createdBy: guid;
                                              }
                                              """);

        Assert.Multiple(() =>
        {
            Assert.That(mixin.Name.Identifier, Is.EqualTo("Audited"));
            Assert.That(mixin.Fields.Select(f => f.Name.Identifier),
                Is.EqualTo(new[] { "createdAt", "createdBy" }));
            Assert.That(mixin.Mixins, Is.Null, "no with clause written");
        });
    }

    [Test]
    public void Mixin_WithAnEmptyBody()
    {
        var mixin = ParseOne(IonParser.Mixin, "mixin Empty { }");

        Assert.That(mixin.Fields, Is.Empty);
        Assert.That(mixin.Mixins, Is.Null);
    }

    [Test]
    public void Mixin_ComposingAnother()
    {
        var mixin = ParseOne(IonParser.Mixin, "mixin Traced with Audited { traceId: guid; }");

        Assert.Multiple(() =>
        {
            Assert.That(mixin.Mixins!.Select(m => m.Identifier), Is.EqualTo(new[] { "Audited" }));
            Assert.That(mixin.Fields.Single().Name.Identifier, Is.EqualTo("traceId"));
        });
    }

    [Test]
    public void Mixin_WithAnEmptyBodyAndAWithClause()
    {
        var mixin = ParseOne(IonParser.Mixin, "mixin Alias with Audited, Traced { }");

        Assert.That(mixin.Fields, Is.Empty);
        Assert.That(mixin.Mixins!.Select(m => m.Identifier), Is.EqualTo(new[] { "Audited", "Traced" }));
    }

    /// <summary>
    /// The body is literally the <c>msg</c> body production, so field-level doc comments and
    /// attributes cannot behave differently here.
    /// </summary>
    [Test]
    public void MixinFields_CarryDocCommentsAndAttributes()
    {
        var mixin = ParseOne(IonParser.Mixin, """
                                              mixin Audited {
                                                  /// When the row was created.
                                                  @idx(1)
                                                  createdAt: datetime;

                                                  /** Who created it. */
                                                  createdBy: guid;
                                              }
                                              """);

        Assert.Multiple(() =>
        {
            Assert.That(mixin.Fields[0].Comments, Is.EqualTo("When the row was created."));
            Assert.That(mixin.Fields[0].Attributes.Single().Name.Identifier, Is.EqualTo("idx"));
            Assert.That(mixin.Fields[1].Comments, Is.EqualTo("Who created it."));
        });
    }

    /// <summary>
    /// A mixin reaches the hoisted leading section in <c>Definition</c> like every other
    /// declaration, so its own doc comment and attributes attach instead of turning it into a parse
    /// error (the failure mode that made the leading section hoisted in the first place).
    /// </summary>
    [Test]
    public void Mixin_CarriesItsOwnDocCommentAndAttributes()
    {
        var file = ParseFile("""
                             /// Audit columns shared by every persisted row.
                             @deprecated("use Tracked")
                             mixin Audited { createdAt: datetime; }
                             """);

        var mixin = file.mixinSyntaxes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(mixin.Comments, Is.EqualTo("Audit columns shared by every persisted row."));
            Assert.That(mixin.Attributes.Single().Name.Identifier, Is.EqualTo("deprecated"));
        });
    }

    #endregion

    #region the with clause on msg

    [Test]
    public void Message_WithSeveralMixins()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                msg Document with Audited, Traced {
                                                                    title: string;
                                                                    body: string;
                                                                }
                                                                """);

        Assert.Multiple(() =>
        {
            Assert.That(msg.Name.Identifier, Is.EqualTo("Document"));
            Assert.That(msg.Mixins!.Select(m => m.Identifier), Is.EqualTo(new[] { "Audited", "Traced" }));
            Assert.That(msg.Fields, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void Message_WithoutAWithClause_HasNullMixins()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, "msg Plain { a: i4; }");

        Assert.That(msg.Mixins, Is.Null,
            "null distinguishes 'no clause' from a clause; an empty clause is unreachable");
    }

    [Test]
    public void Message_WithAnEmptyBodyAndAWithClause()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, "msg Document with Audited {}");

        Assert.That(msg.Fields, Is.Empty);
        Assert.That(msg.Mixins!.Single().Identifier, Is.EqualTo("Audited"));
    }

    /// <summary>
    /// <c>with</c> needs a word boundary, or a field or message whose name merely starts with those
    /// four letters is silently renamed. <c>Keyword</c> supplies it.
    /// </summary>
    [TestCase("msg withers { a: i4; }", TestName = "WithBoundary_MessageNameStartingWithWith")]
    [TestCase("msg M { within: i4; }", TestName = "WithBoundary_FieldNameStartingWithWith")]
    [TestCase("mixin withheld { a: i4; }", TestName = "WithBoundary_MixinNameStartingWithWith")]
    [TestCase("msg M with withheld { a: i4; }", TestName = "WithBoundary_MixinReferenceStartingWithWith")]
    public void With_NeedsAWordBoundary(string source)
    {
        var result = IonParser.IonFile.Parse(source);

        Assert.That(result.Success, Is.True, () => $"{result.Error}");
    }

    [Test]
    public void MixinKeyword_NeedsAWordBoundary()
    {
        // `mixinFoo { }` must not parse as `mixin Foo { }`, the exact bug Keyword exists to stop.
        var file = IonParser.Parse("test", "mixinFoo { a: i4; }");

        Assert.That(file.mixinSyntaxes, Is.Empty);
        Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Not.Empty);
    }

    #endregion

    #region the clause is on msg and mixin only

    [TestCase("union U with Audited { Ok(a: i4) }", TestName = "NoWithClause_OnUnion")]
    [TestCase("service S() with Audited { m(): i4; }", TestName = "NoWithClause_OnService")]
    [TestCase("enum E with Audited { A }", TestName = "NoWithClause_OnEnum")]
    [TestCase("flags F with Audited { A }", TestName = "NoWithClause_OnFlags")]
    [TestCase("typedef T with Audited = i4;", TestName = "NoWithClause_OnTypedef")]
    public void WithClause_IsRejectedEverywhereElse(string source)
    {
        Assert.That(IonParser.IonFile.Parse(source).Success, Is.False,
            "only a field list can be mixed into");
    }

    #endregion

    #region malformed

    [TestCase("msg M with { a: i4; }", TestName = "WithNamingNothing_OnMessage")]
    [TestCase("mixin X with { a: i4; }", TestName = "WithNamingNothing_OnMixin")]
    [TestCase("msg M with ; { a: i4; }", TestName = "WithNamingNothing_FollowedByJunk")]
    [TestCase("msg M with Audited, { a: i4; }", TestName = "WithTrailingComma")]
    [TestCase("msg M with Audited Traced { a: i4; }", TestName = "WithMissingComma")]
    public void MalformedWithClause_IsAParseError(string source)
    {
        Assert.That(IonParser.IonFile.Parse(source).Success, Is.False);
    }

    [TestCase("mixin { a: i4; }", TestName = "Mixin_MissingName")]
    [TestCase("mixin X", TestName = "Mixin_MissingBody")]
    [TestCase("mixin X { a i4; }", TestName = "Mixin_MalformedField")]
    [TestCase("mixin X { a: i4; ", TestName = "Mixin_UnterminatedBody")]
    public void MalformedMixin_IsAParseError(string source)
    {
        Assert.That(IonParser.IonFile.Parse(source).Success, Is.False);
    }

    /// <summary>
    /// Error recovery: a broken mixin becomes an <see cref="InvalidIonBlock"/> and everything after
    /// it still parses. <c>mixin</c> is in <c>DefinitionKeywords</c>, so recovery also resynchronises
    /// <em>on</em> a mixin.
    /// </summary>
    [Test]
    public void ABrokenMixin_DoesNotSwallowTheRestOfTheFile()
    {
        var file = IonParser.Parse("test", """
                                           msg Before { a: i4; }

                                           mixin Broken { this is not a field }

                                           msg After { b: i4; }
                                           """);

        Assert.Multiple(() =>
        {
            Assert.That(file.messageSyntaxes.Select(m => m.Name.Identifier),
                Is.EqualTo(new[] { "Before", "After" }));
            Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Not.Empty);
        });
    }

    [Test]
    public void RecoveryResynchronisesOnAMixin()
    {
        var file = IonParser.Parse("test", """
                                           total garbage that starts no declaration

                                           mixin Audited { createdAt: datetime; }
                                           """);

        Assert.That(file.mixinSyntaxes.Select(m => m.Name.Identifier), Is.EqualTo(new[] { "Audited" }));
    }

    #endregion

    #region trivia

    [Test]
    public void Comments_BetweenWithAndTheMixinList()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                msg Document with /* a */ Audited /* b */ ,
                                                                    // c
                                                                    Traced
                                                                { title: string; }
                                                                """);

        Assert.That(msg.Mixins!.Select(m => m.Identifier), Is.EqualTo(new[] { "Audited", "Traced" }));
    }

    [Test]
    public void Comments_InsideAMixinBody()
    {
        var mixin = ParseOne(IonParser.Mixin, """
                                              mixin /* a */ Audited /* b */ {
                                                  // c
                                                  createdAt: datetime; /* d */
                                                  /// dangling before the brace
                                              }
                                              """);

        Assert.That(mixin.Fields.Single().Name.Identifier, Is.EqualTo("createdAt"));
    }

    #endregion

    #region positions and file collection

    [Test]
    public void EachMixinName_CarriesItsOwnPosition()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, "msg Document with Audited, Traced { }");

        Assert.Multiple(() =>
        {
            Assert.That(msg.Mixins![0].StartPosition.Col, Is.EqualTo(19), "'Audited'");
            Assert.That(msg.Mixins![1].StartPosition.Col, Is.EqualTo(28), "'Traced'");
            Assert.That(msg.Mixins![1].EndPosition!.Value.Col, Is.EqualTo(34));
        });
    }

    [Test]
    public void MixinDeclaration_SpansTheWholeDeclaration()
    {
        var mixin = ParseOne(IonParser.Mixin, "mixin Audited { createdAt: datetime; }");

        Assert.That(mixin.StartPosition.Col, Is.EqualTo(1));
        Assert.That(mixin.EndPosition!.Value.Col, Is.EqualTo(39));
    }

    [Test]
    public void Mixins_AreCollectedOnTheFileAndKeptOutOfDefinitions()
    {
        var file = ParseFile("""
                             mixin Audited { createdAt: datetime; }
                             mixin Traced with Audited { traceId: guid; }
                             msg Document with Audited, Traced { title: string; }
                             """);

        Assert.Multiple(() =>
        {
            Assert.That(file.mixinSyntaxes.Select(m => m.Name.Identifier),
                Is.EqualTo(new[] { "Audited", "Traced" }));
            Assert.That(file.messageSyntaxes.Select(m => m.Name.Identifier),
                Is.EqualTo(new[] { "Document" }));
            Assert.That(file.Definitions.OfType<IonMixinSyntax>(), Is.Empty,
                "Definitions is the list of types; a mixin is not one");
            Assert.That(file.allTokens!.OfType<IonMixinSyntax>().Count(), Is.EqualTo(2),
                "but they are still in allTokens, in source order");
        });
    }

    [Test]
    public void AFileWithNoMixins_HasAnEmptyListRatherThanNull()
    {
        Assert.That(ParseFile("msg M { a: i4; }").mixinSyntaxes, Is.Empty);
    }

    #endregion

    #region the five features together

    /// <summary>
    /// One file exercising all of them at once. Each has its own coverage; this is the guard against
    /// a combination nobody thought about — a <c>with</c> clause in front of a body whose fields hold
    /// inline types, nested generics and fixed sizes.
    /// </summary>
    [Test]
    public void EveryNewFeatureInOneFile()
    {
        var file = ParseFile("""
                             //! Everything at once.

                             /// Audit columns.
                             mixin Audited {
                                 /// When the row was created.
                                 createdAt: datetime;
                                 createdBy: guid;
                             }

                             mixin Traced with Audited { traceId: guid; }

                             msg Document with Audited, Traced {
                                 title: string;
                                 tags: Set<string>;
                                 index: Map<string, Array<Document>>?;
                                 basis: f4[16];
                                 price: decimal;
                                 shipping: msg { address: string; postcode: string; };
                                 history: msg { at: datetime; note: string; }[];
                             }
                             """);

        var document = file.messageSyntaxes.Single();
        var fields = document.Fields.ToDictionary(f => f.Name.Identifier, f => f.Type);

        Assert.Multiple(() =>
        {
            Assert.That(file.ModuleDoc, Is.EqualTo("Everything at once."));
            Assert.That(file.mixinSyntaxes.Select(m => m.Name.Identifier),
                Is.EqualTo(new[] { "Audited", "Traced" }));
            Assert.That(file.mixinSyntaxes[0].Comments, Is.EqualTo("Audit columns."));
            Assert.That(file.mixinSyntaxes[0].Fields[0].Comments, Is.EqualTo("When the row was created."));
            Assert.That(file.mixinSyntaxes[1].Mixins!.Single().Identifier, Is.EqualTo("Audited"));

            Assert.That(document.Mixins!.Select(m => m.Identifier), Is.EqualTo(new[] { "Audited", "Traced" }));
            Assert.That(fields["tags"].generics.Single().Name.Identifier, Is.EqualTo("string"));
            Assert.That(fields["index"].generics[1].Type!.generics.Single().Name.Identifier,
                Is.EqualTo("Document"));
            Assert.That(fields["index"].IsOptional, Is.True);
            Assert.That(fields["basis"].ArraySize, Is.EqualTo(16));
            Assert.That(fields["price"].Name.Identifier, Is.EqualTo("decimal"));
            Assert.That(fields["shipping"].InlineBody!.Fields, Has.Count.EqualTo(2));
            Assert.That(fields["history"].IsInline, Is.True);
            Assert.That(fields["history"].IsArray, Is.True);
            Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Empty);
        });
    }

    #endregion
}
