namespace ion.syntax.test;

using Pidgin;

/// <summary>
/// Coverage for the comment layer of the Ion grammar.
/// <code>
/// // text        ordinary line comment      pure trivia
/// /// text       doc comment                attaches to the next declaration / member
/// //! text       module doc comment         collected into IonFileSyntax.ModuleDoc
/// /* ... */      ordinary block comment     pure trivia, non nesting
/// /** ... */     doc block comment          attaches to the next declaration / member
/// </code>
/// </summary>
public class CommentTests
{
    private static IEnumerable<IonSyntaxMember> ParseFile(string input)
    {
        var result = IonParser.IonFile.Parse(input);
        Assert.That(result.Success, Is.True, () => $"parse failed: {result.Error}");
        return result.Value.ToList();
    }

    private static T ParseOne<T>(Parser<char, T> parser, string input)
    {
        var result = parser.Parse(input);
        Assert.That(result.Success, Is.True, () => $"parse failed: {result.Error}");
        return result.Value;
    }

    #region doc comments on every top level form

    [Test]
    public void Doc_OnMessage()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                /// The user record.
                                                                msg User { id: guid; }
                                                                """);

        Assert.That(msg.Comments, Is.EqualTo("The user record."));
        Assert.That(msg.Name.Identifier, Is.EqualTo("User"));
    }

    [Test]
    public void Doc_OnService()
    {
        var svc = ParseOne(IonParser.Service, """
                                              /// The user service.
                                              service UserService() { Get(): i4; }
                                              """);

        Assert.That(svc.Comments, Is.EqualTo("The user service."));
    }

    [Test]
    public void Doc_OnUnion()
    {
        var union = ParseOne(IonParser.Union, """
                                              /// A result.
                                              union Result { Ok(v: i4), Err(m: string) }
                                              """);

        Assert.That(union.Comments, Is.EqualTo("A result."));
    }

    [Test]
    public void Doc_OnEnum()
    {
        var e = ParseOne(IonParser.Enums, """
                                          /// Channel kinds.
                                          enum ChannelType { Text, Voice }
                                          """);

        Assert.That(e.Comments, Is.EqualTo("Channel kinds."));
    }

    [Test]
    public void Doc_OnFlags()
    {
        var f = ParseOne(IonParser.Flags, """
                                          /// Permission bits.
                                          flags Permissions : u4 { READ = 1 }
                                          """);

        Assert.That(f.Comments, Is.EqualTo("Permission bits."));
    }

    [Test]
    public void Doc_OnTypedef()
    {
        var t = ParseOne(IonParser.Typedef, """
                                            /// An opaque handle.
                                            typedef Handle = string {};
                                            """);

        Assert.That(t.Comments, Is.EqualTo("An opaque handle."));
    }

    [Test]
    public void Doc_OnAttributeDef()
    {
        var a = ParseOne(IonParser.AttributeDef, """
                                                 /// Marks an endpoint as anonymous.
                                                 attribute @AllowAnonymous();
                                                 """);

        Assert.That(a.Comments, Is.EqualTo("Marks an endpoint as anonymous."));
    }

    [Test]
    public void Doc_OnUseDirective()
    {
        var d = ParseOne(IonParser.Definition, """
                                               /// Pull in the common module.
                                               #use "common"
                                               """);

        Assert.That(d, Is.InstanceOf<IonUseSyntax>());
        Assert.That(d.Comments, Is.EqualTo("Pull in the common module."));
    }

    [Test]
    public void Doc_OnImportDirective()
    {
        var d = ParseOne(IonParser.Definition, """
                                               /// Bring in two types.
                                               #import { User, Role } from "identity"
                                               """);

        Assert.That(d, Is.InstanceOf<IonImportSyntax>());
        Assert.That(d.Comments, Is.EqualTo("Bring in two types."));
    }

    [Test]
    public void Doc_OnFeatureDirective()
    {
        var d = ParseOne(IonParser.Definition, """
                                               /// Requires the streaming feature.
                                               #feature "streaming"
                                               """);

        Assert.That(d, Is.InstanceOf<IonFeatureSyntax>());
        Assert.That(d.Comments, Is.EqualTo("Requires the streaming feature."));
    }

    /// <summary>
    /// Regression for the hoisted leading section: the doc comment used to be consumed by the
    /// first <c>OneOf</c> alternative (<c>attribute</c>), which then aborted the whole dispatch.
    /// </summary>
    [Test]
    public void Doc_OnEveryTopLevelForm_InOneFile()
    {
        var members = ParseFile("""
                                /// use
                                #use "common"

                                /// import
                                #import { A } from "m"

                                /// feature
                                #feature "f"

                                /// attribute def
                                attribute @Anon();

                                /// message
                                msg M { a: i4; }

                                /// service
                                service S() { m(); }

                                /// union
                                union U { A(x: i4) }

                                /// enumeration
                                enum E { A }

                                /// flags
                                flags F { A = 1 }

                                /// typedef
                                typedef T = string {};
                                """).ToList();

        Assert.That(members.Count, Is.EqualTo(10));
        Assert.That(members.Select(x => x.Comments), Is.EqualTo(new[]
        {
            "use", "import", "feature", "attribute def", "message",
            "service", "union", "enumeration", "flags", "typedef"
        }));
        Assert.That(members.OfType<InvalidIonBlock>(), Is.Empty);
    }

    #endregion

    #region doc text shapes and normalization

    [Test]
    public void Doc_MultipleLines_JoinedWithNewline()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                /// first line
                                                                /// second line
                                                                msg M {}
                                                                """);

        Assert.That(msg.Comments, Is.EqualTo("first line\nsecond line"));
    }

    [Test]
    public void Doc_InteriorBlankLine_IsPreserved()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                /// summary
                                                                ///
                                                                /// remarks
                                                                msg M {}
                                                                """);

        Assert.That(msg.Comments, Is.EqualTo("summary\n\nremarks"));
    }

    /// <summary>The third slash used to leak into the text (<c>/// Foo</c> produced <c>"/ Foo"</c>).</summary>
    [Test]
    public void Doc_MarkerIsFullyStripped()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, "/// Foo\nmsg M {}");

        Assert.That(msg.Comments, Is.EqualTo("Foo"));
    }

    [Test]
    public void Doc_OnlyOneLeadingSpaceIsStripped()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, "///   indented\nmsg M {}");

        Assert.That(msg.Comments, Is.EqualTo("  indented"));
    }

    [Test]
    public void Doc_TrailingWhitespaceIsStripped()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, "/// text   \nmsg M {}");

        Assert.That(msg.Comments, Is.EqualTo("text"));
    }

    [Test]
    public void DocBlock_WithStarContinuationLines()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                /**
                                                                 * A documented message.
                                                                 *
                                                                 * Second paragraph.
                                                                 */
                                                                msg M {}
                                                                """);

        Assert.That(msg.Comments, Is.EqualTo("A documented message.\n\nSecond paragraph."));
    }

    [Test]
    public void DocBlock_SingleLine()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, "/** short doc */\nmsg M {}");

        Assert.That(msg.Comments, Is.EqualTo("short doc"));
    }

    [Test]
    public void DocBlock_AndLineDocs_AreMerged()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                /** block */
                                                                /// line
                                                                msg M {}
                                                                """);

        Assert.That(msg.Comments, Is.EqualTo("block\nline"));
    }

    [Test]
    public void ModuleDoc_IsCollectedIntoFileSyntax()
    {
        var file = IonParser.Parse("test", """
                                           //! Module summary.
                                           //!
                                           //! Details go here.

                                           msg M {}
                                           """);

        Assert.That(file.ModuleDoc, Is.EqualTo("Module summary.\n\nDetails go here."));
        Assert.That(file.messageSyntaxes.Count, Is.EqualTo(1));
        Assert.That(file.allTokens!.OfType<IonModuleDocSyntax>(), Is.Empty,
            "module docs must be lifted out of the member list");
    }

    [Test]
    public void ModuleDoc_AfterADeclaration_IsAlsoCollected()
    {
        var file = IonParser.Parse("test", """
                                           //! first

                                           msg A {}

                                           //! second
                                           msg B {}
                                           """);

        Assert.That(file.ModuleDoc, Is.EqualTo("first\nsecond"));
        Assert.That(file.messageSyntaxes.Count, Is.EqualTo(2));
    }

    [Test]
    public void ModuleDoc_DoesNotAttachToNextDeclaration()
    {
        var file = IonParser.Parse("test", """
                                           //! module level
                                           msg A {}
                                           """);

        Assert.That(file.ModuleDoc, Is.EqualTo("module level"));
        Assert.That(file.messageSyntaxes.Single().Comments, Is.Null);
    }

    [Test]
    public void ModuleDoc_Absent_IsNull()
    {
        var file = IonParser.Parse("test", "msg A {}");

        Assert.That(file.ModuleDoc, Is.Null);
    }

    /// <summary>Outside a top level position <c>//!</c> is just an ordinary comment.</summary>
    [Test]
    public void ModuleDoc_InsideABody_IsPlainTrivia()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                msg M {
                                                                    a: i4;
                                                                    //! not a module doc down here
                                                                    b: i4;
                                                                    //! nor here
                                                                }
                                                                """);

        Assert.That(msg.Fields.Count, Is.EqualTo(2));
        Assert.That(msg.Fields.Select(x => x.Comments), Is.All.Null);
    }

    [TestCase("/// one\r\n/// two\r\nmsg M {}", "one\ntwo")]
    [TestCase("/**\r\n * one\r\n * two\r\n */\r\nmsg M {}", "one\ntwo")]
    public void Doc_WithWindowsLineEndings(string input, string expected)
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, input);

        Assert.That(msg.Comments, Is.EqualTo(expected));
    }

    #endregion

    #region ordinary comments are pure trivia in every position

    [Test]
    public void LineComment_IsNotAttached()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                // not a doc comment
                                                                msg M {}
                                                                """);

        Assert.That(msg.Comments, Is.Null);
    }

    [Test]
    public void FourSlashes_AreAnOrdinaryComment()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                //// separator ////////
                                                                msg M {}
                                                                """);

        Assert.That(msg.Comments, Is.Null);
    }

    [Test]
    public void BlockComment_IsNotAttached()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, "/* not a doc */\nmsg M {}");

        Assert.That(msg.Comments, Is.Null);
    }

    [Test]
    public void EmptyBlockComment_IsOrdinary_NotADocBlock()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, "/**/\nmsg M {}");

        Assert.That(msg.Comments, Is.Null);
    }

    [Test]
    public void BlockComment_OnOneLine()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, "/* one line */ msg M { a: i4; }");

        Assert.That(msg.Fields.Count, Is.EqualTo(1));
    }

    [Test]
    public void BlockComment_IsNotNesting()
    {
        // terminated by the FIRST */ ; the trailing "*/" is then plain (invalid) source
        var result = IonParser.IonFile.Parse("/* outer /* inner */ msg M {}");

        Assert.That(result.Success, Is.True, () => $"{result.Error}");
        Assert.That(result.Value.OfType<IonMessageSyntax>().Count(), Is.EqualTo(1));
    }

    [Test]
    public void Comments_BetweenKeywordAndName()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, "msg /* here */ M /* and here */ {}");

        Assert.That(msg.Name.Identifier, Is.EqualTo("M"));
    }

    [Test]
    public void Comments_AfterOpeningBrace_AndBetweenMembers()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                msg M { // after brace
                                                                    a: i4;
                                                                    /* between members */
                                                                    // and a line comment
                                                                    b: i4;
                                                                }
                                                                """);

        Assert.That(msg.Fields.Count, Is.EqualTo(2));
    }

    [Test]
    public void Comments_BeforeClosingBrace_OfAMessage()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                msg M {
                                                                    a: i4;
                                                                    // trivia directly before the closing brace
                                                                    /* and a block one */
                                                                }
                                                                """);

        Assert.That(msg.Fields.Count, Is.EqualTo(1));
    }

    [Test]
    public void DanglingDocComment_BeforeClosingBrace_DoesNotKillTheBlock()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                msg M {
                                                                    a: i4;
                                                                    /// dangling doc with nothing to attach to
                                                                }
                                                                """);

        Assert.That(msg.Fields.Count, Is.EqualTo(1));
    }

    [Test]
    public void Comments_BeforeClosingBrace_OfAService()
    {
        var svc = ParseOne(IonParser.Service, """
                                              service S() {
                                                  m(): i4;
                                                  // trivia in an awkward spot: directly before the closing brace
                                              }
                                              """);

        Assert.That(svc.Methods.Count, Is.EqualTo(1));
    }

    [Test]
    public void Comments_BeforeClosingParen_OfAnArgumentList()
    {
        var svc = ParseOne(IonParser.Service, """
                                              service S(
                                                  a: i4,
                                                  b: i4
                                                  // trailing trivia before the closing paren
                                                  /// and a dangling doc
                                              ) {
                                                  m(x: i4 /* before paren */): i4;
                                              }
                                              """);

        Assert.That(svc.BaseArguments.Count, Is.EqualTo(2));
        Assert.That(svc.Methods.Single().arguments.Count, Is.EqualTo(1));
    }

    [Test]
    public void Comments_BeforeClosingBrace_OfAUnion()
    {
        var union = ParseOne(IonParser.Union, """
                                              union U {
                                                  A(x: i4),
                                                  B
                                                  // dangling
                                              }
                                              """);

        Assert.That(union.cases.Count, Is.EqualTo(2));
    }

    [Test]
    public void Comment_AfterLastDeclarationInFile()
    {
        var members = ParseFile("""
                                msg M {}
                                // trailing comment at end of file
                                """);

        Assert.That(members.OfType<IonMessageSyntax>().Count(), Is.EqualTo(1));
    }

    [Test]
    public void DanglingDocComment_AtEndOfFile()
    {
        var members = ParseFile("""
                                msg M {}
                                /// dangling doc at end of file
                                """);

        Assert.That(members.OfType<IonMessageSyntax>().Count(), Is.EqualTo(1));
        Assert.That(members.OfType<IonMessageSyntax>().Single().Comments, Is.Null);
    }

    [Test]
    public void Comments_AroundColonAndTypeModifiers()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                msg M {
                                                                    a /* 1 */ : /* 2 */ i4 /* 3 */ ;
                                                                    b: string /* 4 */ [] /* 5 */ ? /* 6 */ ;
                                                                    c: Data /* 7 */ ~ /* 8 */ ;
                                                                }
                                                                """);

        Assert.That(msg.Fields.Count, Is.EqualTo(3));
        Assert.That(msg.Fields[1].Type.IsArray, Is.True);
        Assert.That(msg.Fields[1].Type.IsOptional, Is.True);
        Assert.That(msg.Fields[2].Type.IsPartial, Is.True);
    }

    [Test]
    public void Comments_InsideGenericArgumentList()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                msg M {
                                                                    a: Result /* 1 */ < /* 2 */ User /* 3 */ > /* 4 */ [] ;
                                                                }
                                                                """);

        Assert.That(msg.Fields.Single().Type.generics.Count, Is.EqualTo(1));
        Assert.That(msg.Fields.Single().Type.IsArray, Is.True);
    }

    [Test]
    public void Comments_BetweenAttributes_AndBeforeTheDeclaration()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                @First()
                                                                // ordinary trivia between attributes
                                                                @Second()
                                                                /* and before the declaration */
                                                                msg M {}
                                                                """);

        Assert.That(msg.Attributes.Select(x => x.Name.Identifier), Is.EqualTo(new[] { "First", "Second" }));
    }

    [Test]
    public void Comments_InsideImportTypeList()
    {
        var import = ParseFile("""
                               #import { /* a */ A /* b */, // c
                                         B } from /* d */ "identity";
                               """).OfType<IonImportSyntax>().Single();

        Assert.That(import.TypeNames, Is.EqualTo(new[] { "A", "B" }));
        Assert.That(import.ModuleName, Is.EqualTo("identity"));
    }

    /// <summary>
    /// Attribute arguments are lexed literals now (see <c>AttributeArgumentTests</c>), so this
    /// asserts on the parsed values rather than on a raw span. Bare identifiers such as the old
    /// <c>@a(x, z)</c> are no longer a legal argument form — a literal is required.
    /// </summary>
    [Test]
    public void Comments_InsideAttributeArguments_AreSkipped()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                @a(1 /* y */, 2)
                                                                msg M {}
                                                                """);

        Assert.That(
            msg.Attributes.Single().Args.Select(a => ((IonIntegerLiteralSyntax)a.Value).Raw),
            Is.EqualTo(new[] { "1", "2" }));
    }

    #endregion

    #region doc comments and attributes interleaved

    [TestCase("/// d\n@A()\nmsg M {}")]
    [TestCase("@A()\n/// d\nmsg M {}")]
    public void DocAndAttribute_InAnyOrder(string input)
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, input);

        Assert.That(msg.Comments, Is.EqualTo("d"));
        Assert.That(msg.Attributes.Single().Name.Identifier, Is.EqualTo("A"));
    }

    [Test]
    public void DocLines_AroundAttributes_AreMerged()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                /// one
                                                                @A()
                                                                /// two
                                                                @B()
                                                                /// three
                                                                msg M {}
                                                                """);

        Assert.That(msg.Comments, Is.EqualTo("one\ntwo\nthree"));
        Assert.That(msg.Attributes.Select(x => x.Name.Identifier), Is.EqualTo(new[] { "A", "B" }));
    }

    #endregion

    #region members carry docs

    [Test]
    public void Doc_OnMessageFields()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                msg M {
                                                                    /// the identifier
                                                                    id: guid;
                                                                    /** the display name */
                                                                    name: string;
                                                                    // ordinary trivia, not attached
                                                                    other: i4;
                                                                }
                                                                """);

        Assert.That(msg.Fields.Select(x => x.Comments),
            Is.EqualTo(new[] { "the identifier", "the display name", null }));
    }

    [Test]
    public void Doc_OnServiceMethodsAndArguments()
    {
        var svc = ParseOne(IonParser.Service, """
                                              service S(
                                                  /// bound operand
                                                  left: i4
                                              ) {
                                                  /// adds a number
                                                  Add(
                                                      /// the addend
                                                      right: i4
                                                  ): i4;
                                              }
                                              """);

        Assert.That(svc.BaseArguments.Single().Comments, Is.EqualTo("bound operand"));
        Assert.That(svc.Methods.Single().Comments, Is.EqualTo("adds a number"));
        Assert.That(svc.Methods.Single().arguments.Single().Comments, Is.EqualTo("the addend"));
    }

    [Test]
    public void Doc_OnUnionCases()
    {
        var union = ParseOne(IonParser.Union, """
                                              union U {
                                                  /// the happy path
                                                  Ok(value: i4),
                                                  /** the sad path */
                                                  Err(message: string)
                                              }
                                              """);

        Assert.That(union.cases.Select(x => x.Comments), Is.EqualTo(new[] { "the happy path", "the sad path" }));
    }

    #endregion

    #region enum / flags members

    [Test]
    public void Doc_OnEnumMembers()
    {
        var e = (IonEnumSyntax)ParseOne(IonParser.Enums, """
                                                         enum E {
                                                             /// plain text channel
                                                             Text,
                                                             /** voice channel */
                                                             Voice,
                                                             // ordinary trivia
                                                             Announcement
                                                         }
                                                         """);

        Assert.That(e.Entries.Select(x => x.Comments),
            Is.EqualTo(new[] { "plain text channel", "voice channel", null }));
    }

    [Test]
    public void Doc_OnFlagsMembers()
    {
        var f = (IonFlagsSyntax)ParseOne(IonParser.Flags, """
                                                          flags F : u4 {
                                                              /// can read
                                                              READ = 1,
                                                              /// can write
                                                              WRITE = 1 << 1
                                                          }
                                                          """);

        Assert.That(f.Entries.Select(x => x.Comments), Is.EqualTo(new[] { "can read", "can write" }));
    }

    [Test]
    public void EnumValue_StopsAtLineComment()
    {
        var e = (IonEnumSyntax)ParseOne(IonParser.Enums, """
                                                         enum E {
                                                             A = 1, // note about A
                                                             B = 2 // note about B
                                                         }
                                                         """);

        Assert.That(e.Entries.Select(x => x.ValueExpression.Value.value), Is.EqualTo(new[] { "1", "2" }));
    }

    [Test]
    public void EnumValue_StopsAtBlockComment()
    {
        var e = (IonEnumSyntax)ParseOne(IonParser.Enums, """
                                                         enum E {
                                                             A = 1 /* note about A */,
                                                             B = 2 /* note about B */
                                                         }
                                                         """);

        Assert.That(e.Entries.Select(x => x.ValueExpression.Value.value), Is.EqualTo(new[] { "1", "2" }));
    }

    [Test]
    public void FlagsValue_ShiftExpressionWithTrailingComment()
    {
        var f = (IonFlagsSyntax)ParseOne(IonParser.Flags, """
                                                          flags F {
                                                              NONE = 0, // nothing
                                                              A = 1 << 1, // first
                                                              B = 1 << 2 // second
                                                          }
                                                          """);

        Assert.That(f.Entries.Select(x => x.ValueExpression.Value.value),
            Is.EqualTo(new[] { "0", "1 << 1", "1 << 2" }));
    }

    [Test]
    public void EnumMembers_CommentBeforeClosingBrace()
    {
        var e = (IonEnumSyntax)ParseOne(IonParser.Enums, """
                                                         enum E {
                                                             A,
                                                             B
                                                             // dangling trivia
                                                             /// and a dangling doc
                                                         }
                                                         """);

        Assert.That(e.Entries.Count, Is.EqualTo(2));
    }

    #endregion

    #region regressions and edge cases

    /// <summary>
    /// <c>///</c> on its own line used to swallow the following line of source, silently
    /// deleting the declaration.
    /// </summary>
    [Test]
    public void EmptyDocLine_DoesNotSwallowTheNextLine()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                ///
                                                                msg Foo { }
                                                                """);

        Assert.That(msg.Name.Identifier, Is.EqualTo("Foo"));
        Assert.That(msg.Comments, Is.Null);
    }

    [Test]
    public void EmptyDocLine_FollowedByRealDoc()
    {
        var msg = (IonMessageSyntax)ParseOne(IonParser.Message, """
                                                                ///
                                                                /// real text
                                                                ///
                                                                msg Foo { }
                                                                """);

        Assert.That(msg.Name.Identifier, Is.EqualTo("Foo"));
        Assert.That(msg.Comments, Is.EqualTo("real text"));
    }

    [Test]
    public void UnterminatedBlockComment_AtEndOfFile_DoesNotHang()
    {
        var members = ParseFile("""
                                msg M {}
                                /* this block comment is never closed
                                """);

        Assert.That(members.OfType<IonMessageSyntax>().Count(), Is.EqualTo(1));
    }

    [Test]
    public void UnterminatedBlockComment_SwallowingWholeFile()
    {
        var members = ParseFile("/* everything after this is a comment\nmsg M {}");

        Assert.That(members, Is.Empty);
    }

    [Test]
    public void UnterminatedDocBlockComment_DoesNotHang()
    {
        var members = ParseFile("msg M {}\n/** never closed");

        Assert.That(members.OfType<IonMessageSyntax>().Count(), Is.EqualTo(1));
    }

    [Test]
    public void DoubleSlashInsideStringLiteral_DoesNotStartAComment()
    {
        var use = ParseFile("""
                            #use "http://example.com/schema"
                            msg M {}
                            """).OfType<IonUseSyntax>().Single();

        Assert.That(use.Path, Is.EqualTo("http://example.com/schema"));
    }

    [Test]
    public void BlockCommentTerminatorInsideStringLiteral_DoesNotTerminateAnything()
    {
        var use = ParseFile("""
                            #use "a*/b/*c"
                            msg M {}
                            """).OfType<IonUseSyntax>().Single();

        Assert.That(use.Path, Is.EqualTo("a*/b/*c"));
    }

    [Test]
    public void StringLiteralInsideBlockComment_DoesNotProtectTheTerminator()
    {
        // comments are lexed before strings: the first */ ends the comment, full stop
        var members = ParseFile("""
                                /* a "quoted */ msg M {}
                                """);

        Assert.That(members.OfType<IonMessageSyntax>().Count(), Is.EqualTo(1));
    }

    [Test]
    public void CommentOnlyFile_ParsesToNothing()
    {
        var file = IonParser.Parse("test", """
                                           // just an ordinary comment
                                           /* and a block one */
                                           """);

        Assert.That(file.allTokens, Is.Empty);
        Assert.That(file.ModuleDoc, Is.Null);
    }

    [Test]
    public void DocCommentOnlyFile_ParsesToNothing()
    {
        var file = IonParser.Parse("test", "/// a doc with nothing to attach to\n");

        Assert.That(file.allTokens, Is.Empty);
    }

    [Test]
    public void ModuleDocOnlyFile()
    {
        var file = IonParser.Parse("test", "//! nothing but module docs\n");

        Assert.That(file.allTokens, Is.Empty);
        Assert.That(file.ModuleDoc, Is.EqualTo("nothing but module docs"));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\n\n\t\n")]
    public void EmptyFile_Parses(string input)
    {
        var file = IonParser.Parse("test", input);

        Assert.That(file.allTokens, Is.Empty);
        Assert.That(file.ModuleDoc, Is.Null);
    }

    /// <summary>
    /// Error recovery used to re-synchronise on a definition keyword found at ANY offset,
    /// including inside comments and string literals.
    /// </summary>
    [Test]
    public void Recovery_DoesNotResyncInsideAComment()
    {
        var file = IonParser.Parse("test", """
                                           msg Ok { a: i4; }

                                           this is not valid ion
                                           // the word service appears in this comment
                                           /*
                                           msg NotReal {
                                           */

                                           msg AlsoOk { b: i4; }
                                           """);

        Assert.That(file.messageSyntaxes.Select(x => x.Name.Identifier), Is.EqualTo(new[] { "Ok", "AlsoOk" }));
        var invalid = file.allTokens!.OfType<InvalidIonBlock>().Single();
        Assert.That(invalid.block, Does.Contain("not valid ion"));
        Assert.That(invalid.block, Does.Contain("service appears"),
            "a comment must be consumed as part of the invalid block, not used as a resync point");
        Assert.That(invalid.block, Does.Contain("msg NotReal"),
            "a keyword at the start of a line inside a block comment is not a resync point");
    }

    [Test]
    public void Recovery_DoesNotResyncInsideAStringLiteral()
    {
        var file = IonParser.Parse("test", "broken \"\nmsg NotReal {\n\" broken\n\nmsg Real { a: i4; }\n");

        Assert.That(file.messageSyntaxes.Select(x => x.Name.Identifier), Is.EqualTo(new[] { "Real" }));
        Assert.That(file.allTokens!.OfType<InvalidIonBlock>().Single().block, Does.Contain("msg NotReal"));
    }

    [Test]
    public void Recovery_StillResyncsOnAnIndentedKeyword()
    {
        var file = IonParser.Parse("test", "garbage\n\n   msg Real { a: i4; }\n");

        Assert.That(file.messageSyntaxes.Select(x => x.Name.Identifier), Is.EqualTo(new[] { "Real" }));
    }

    [Test]
    public void Recovery_TrailingCommentIsNotReportedAsAnInvalidBlock()
    {
        var file = IonParser.Parse("test", """
                                           garbage here
                                           msg Real { a: i4; }
                                           // trailing comment
                                           """);

        Assert.That(file.messageSyntaxes.Count, Is.EqualTo(1));
        Assert.That(file.allTokens!.OfType<InvalidIonBlock>().Count(), Is.EqualTo(1));
    }

    #endregion

    #region full-file smoke test

    [Test]
    public void FullyCommentedContract_Parses()
    {
        var file = IonParser.Parse("test", """
                                           //! Arithmetic RPC surface.
                                           //!
                                           //! Every service here is stateless.

                                           /// Common types.
                                           #use "common"

                                           /**
                                            * Integer arithmetic over a fixed left-hand operand.
                                            *
                                            * The operand is bound at construction time.
                                            */
                                           @Grain()
                                           service MathInteraction(
                                               /// The left-hand operand.
                                               leftOperand: i4
                                           ) {
                                               /// Adds rightOperand to the bound operand.
                                               Add(rightOperand: i4): i4;

                                               // Trivia in an awkward spot: directly before the closing brace.
                                           }

                                           /// Rounding behaviour.
                                           enum Rounding : u1 {
                                               /// Round toward zero.
                                               Truncate = 0, // the default
                                               /// Round half away from zero.
                                               HalfUp = 1 /* the friendly one */
                                               // dangling
                                           }
                                           """);

        Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Empty);
        Assert.That(file.ModuleDoc, Is.EqualTo("Arithmetic RPC surface.\n\nEvery service here is stateless."));
        Assert.That(file.useSyntaxes.Single().Comments, Is.EqualTo("Common types."));

        var svc = file.serviceSyntaxes.Single();
        Assert.That(svc.Comments,
            Is.EqualTo("Integer arithmetic over a fixed left-hand operand.\n\nThe operand is bound at construction time."));
        Assert.That(svc.Attributes.Single().Name.Identifier, Is.EqualTo("Grain"));
        Assert.That(svc.BaseArguments.Single().Comments, Is.EqualTo("The left-hand operand."));
        Assert.That(svc.Methods.Single().Comments, Is.EqualTo("Adds rightOperand to the bound operand."));

        var rounding = file.enumSyntaxes.Single();
        Assert.That(rounding.Comments, Is.EqualTo("Rounding behaviour."));
        Assert.That(rounding.Entries.Select(x => x.Comments),
            Is.EqualTo(new[] { "Round toward zero.", "Round half away from zero." }));
        Assert.That(rounding.Entries.Select(x => x.ValueExpression.Value.value), Is.EqualTo(new[] { "0", "1" }));
    }

    #endregion

    #region regressions found by adversarial fuzzing

    /// <summary>
    /// A UTF-8 BOM is not <c>char.IsWhiteSpace</c> on .NET, so before it was accepted as trivia
    /// it turned the entire file into a single <see cref="InvalidIonBlock"/>.
    /// <c>VectorInteraction.ion</c> in this repo actually starts with one.
    /// </summary>
    [TestCase("﻿msg A { x: u4; }", TestName = "Bom_Plain")]
    [TestCase("﻿/// doc\nmsg A { x: u4; }", TestName = "Bom_BeforeDocComment")]
    [TestCase("﻿//! mod\nmsg A { x: u4; }", TestName = "Bom_BeforeModuleDoc")]
    [TestCase("﻿// trivia\nmsg A { x: u4; }", TestName = "Bom_BeforeLineComment")]
    [TestCase("﻿/* trivia */\nmsg A { x: u4; }", TestName = "Bom_BeforeBlockComment")]
    public void ByteOrderMark_IsTrivia(string source)
    {
        var file = IonParser.Parse("bom", source);

        Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Empty);
        Assert.That(file.messageSyntaxes.Single().Name.Identifier, Is.EqualTo("A"));
    }

    [Test]
    public void ByteOrderMark_DoesNotSwallowTheDocComment()
    {
        var file = IonParser.Parse("bom", "﻿/// doc\nmsg A { x: u4; }");

        Assert.That(file.messageSyntaxes.Single().Comments, Is.EqualTo("doc"));
    }

    /// <summary>
    /// The enum/flags value scanner stops at <c>/</c> so that a trailing <c>// note</c> is not
    /// swallowed. A <c>/* */</c> *inside* the value must still collapse to a space rather than
    /// truncate it — <c>1 &lt;&lt; 1</c> parsed before comment support landed and must keep parsing.
    /// </summary>
    [TestCase("flags F { A = 1 /*c*/ << /*c*/ 1 }", "1 << 1", TestName = "Flags_BlockCommentAroundShift")]
    [TestCase("flags F { A = /*c*/ 1 << 1 }", "1 << 1", TestName = "Flags_BlockCommentBeforeValue")]
    [TestCase("flags F { A = 1 << 1 /*c*/ }", "1 << 1", TestName = "Flags_BlockCommentAfterValue")]
    [TestCase("flags F { A = 1 << 1 // note\n }", "1 << 1", TestName = "Flags_LineCommentEndsValue")]
    [TestCase("flags F { A = 0x0F /*c*/ }", "0x0F", TestName = "Flags_BlockCommentAfterHex")]
    public void FlagsValue_ToleratesEmbeddedComments(string source, string expected)
    {
        var file = IonParser.Parse("flags", source);

        Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Empty);
        var entry = file.flagsSyntaxes.Single().Entries.Single();
        // whitespace is normalized by the consumer (TransformStage splits on "<<" with TrimEntries)
        Assert.That(Squash(entry.ValueExpression.Value.value), Is.EqualTo(expected));
    }

    /// <summary>
    /// A <c>)</c> or <c>,</c> that only occurs inside a comment or a string literal must not
    /// terminate the argument list early. Originally a regression guard for the raw span splitter;
    /// it still guards the same inputs now that the arguments are lexed literals, and the
    /// expectation is the <em>decoded</em> value of each argument.
    /// </summary>
    [TestCase("@Foo(/* ) */ 1)\nmsg A { x: u4; }", new[] { "1" }, TestName = "Attr_ParenInBlockComment")]
    [TestCase("@Foo(1 /* , */ )\nmsg A { x: u4; }", new[] { "1" }, TestName = "Attr_CommaInBlockComment")]
    [TestCase("@Foo(\"a)b\")\nmsg A { x: u4; }", new[] { "a)b" }, TestName = "Attr_ParenInString")]
    [TestCase("@Foo(\"a,b\")\nmsg A { x: u4; }", new[] { "a,b" }, TestName = "Attr_CommaInString")]
    [TestCase("@Foo(1, 2)\nmsg A { x: u4; }", new[] { "1", "2" }, TestName = "Attr_PlainTwoArgs")]
    [TestCase("@Foo(1 /*x*/, 2)\nmsg A { x: u4; }", new[] { "1", "2" }, TestName = "Attr_CommentBetweenArgs")]
    public void AttributeArguments_ToleratesCommentsAndStrings(string source, string[] expected)
    {
        var file = IonParser.Parse("attr", source);

        Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Empty);
        var attr = file.messageSyntaxes.Single().Attributes.Single();
        Assert.That(attr.Name.Identifier, Is.EqualTo("Foo"));
        Assert.That(attr.Args.Select(a => a.Value switch
        {
            IonIntegerLiteralSyntax i => i.Raw,
            IonStringLiteralSyntax s => s.Value,
            var other => other.ToString()
        }), Is.EqualTo(expected));
    }

    /// <summary>
    /// Nothing in the comment layer may throw or hang: malformed input has to come back as an
    /// <see cref="InvalidIonBlock"/> through error recovery.
    /// </summary>
    [TestCase("/", TestName = "Degenerate_LoneSlash")]
    [TestCase("/*", TestName = "Degenerate_UnterminatedBlock")]
    [TestCase("/**", TestName = "Degenerate_UnterminatedDocBlock")]
    [TestCase("///", TestName = "Degenerate_BareDocMarker")]
    [TestCase("//!", TestName = "Degenerate_BareModuleDocMarker")]
    [TestCase("msg A { x: u4; }\n/", TestName = "Degenerate_TrailingSlash")]
    [TestCase("msg A { x: u4; }\n/* *", TestName = "Degenerate_TrailingHalfClose")]
    [TestCase("/// doc\nthis is not ion\nmsg A { x: u4; }", TestName = "Degenerate_GarbageAfterDoc")]
    [TestCase("/// doc\n}\nmsg A { x: u4; }", TestName = "Degenerate_StrayBrace")]
    [TestCase("/// doc\nmsg A { x: u4;", TestName = "Degenerate_UnclosedMessage")]
    [TestCase("# not an ion comment\nmsg A { x: u4; }", TestName = "Degenerate_HashComment")]
    public void DegenerateInput_RecoversInsteadOfThrowing(string source)
    {
        Assert.DoesNotThrow(() => IonParser.Parse("degenerate", source));
    }

    [Test]
    public void PathologicalInput_DoesNotHangOrOverflow()
    {
        var cases = new[]
        {
            string.Concat(Enumerable.Repeat("/* c */ ", 5_000)) + "msg A { x: u4; }",
            string.Concat(Enumerable.Range(0, 5_000).Select(i => $"/// line {i}\n")) + "msg A { x: u4; }",
            "/*" + new string('x', 100_000) + "*/\nmsg A { x: u4; }",
            new string('/', 10_000),
            "/*" + new string('*', 10_000)
        };

        foreach (var source in cases)
        {
            var task = Task.Run(() => IonParser.Parse("pathological", source));
            Assert.That(task.Wait(TimeSpan.FromSeconds(30)), Is.True, "parser did not terminate");
            Assert.DoesNotThrow(() => task.GetAwaiter().GetResult());
        }
    }

    private static string Squash(string value) =>
        string.Join(' ', value.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    #endregion
}
