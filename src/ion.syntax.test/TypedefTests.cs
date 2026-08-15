namespace ion.syntax.test;

using ion.compiler;
using ion.compiler.CodeGen;
using ion.runtime;
using Pidgin;

/// <summary>
/// End-to-end coverage for <c>typedef</c>.
/// <para>
/// A typedef is a <em>transparent alias</em>, not a newtype: it is erased by
/// <c>RestoreUnresolvedTypeStage</c>, so every use site carries the underlying type and nothing
/// about the alias reaches the wire. These tests pin all three halves of that: the grammar, the
/// erasure, and the absence of any serialization machinery for the alias.
/// </para>
/// </summary>
public class TypedefTests
{
    // ═══════════════════════════════════════════════════════════════════
    // HARNESS
    // ═══════════════════════════════════════════════════════════════════

    private static IonTypedefSyntax ParseTypedef(string input)
    {
        var result = IonParser.Typedef.Before(Parser<char>.End).Parse(input);
        Assert.That(result.Success, Is.True, () => $"parse failed: {result.Error}");
        return result.Value;
    }

    private static void AssertTypedefRejected(string input)
    {
        var result = IonParser.Typedef.Before(Parser<char>.End).Parse(input);
        Assert.That(result.Success, Is.False,
            () => $"expected a parse failure, but it parsed as {result.Value}");
    }

    private static List<IonSyntaxMember> ParseFileStrict(string input)
    {
        var result = IonParser.IonFile.Parse(input);
        Assert.That(result.Success, Is.True, () => $"parse failed: {result.Error}");
        return result.Value.ToList();
    }

    private sealed record Compiled(CompilationContext Context, bool Success)
    {
        public IReadOnlyList<IonDiagnostic> Diagnostics => Context.Diagnostics;

        public IReadOnlyList<string> ErrorCodes => Diagnostics
            .Where(d => d.Severity == IonDiagnosticSeverity.Error)
            .Select(d => d.Code)
            .ToList();

        public IReadOnlyList<IonType> Definitions => Context.ProcessedModules
            .SelectMany(m => m.Definitions)
            .DistinctBy(d => d.name.Identifier)
            .ToList();

        public IonType Definition(string name) =>
            Definitions.FirstOrDefault(d => d.name.Identifier == name)
            ?? throw new AssertionException($"no definition named '{name}' (have: " +
                                            $"{string.Join(", ", Definitions.Select(d => d.name.Identifier))})");

        public IonType FieldType(string typeName, string fieldName) =>
            Definition(typeName).fields.FirstOrDefault(f => f.name.Identifier == fieldName)?.type
            ?? throw new AssertionException($"no field '{fieldName}' on '{typeName}'");

        public IonMethod Method(string serviceName, string methodName) =>
            Context.ProcessedModules.SelectMany(m => m.Services)
                .First(s => s.name.Identifier == serviceName)
                .methods.First(m => m.name.Identifier == methodName);
    }

    private static Compiled Compile(string source, IonSchemaLock? existingLock = null)
        => CompileMany([source], existingLock);

    private static Compiled CompileMany(IReadOnlyList<string> sources, IonSchemaLock? existingLock = null)
    {
        var files = sources
            .Select((s, i) => IonParser.Parse($"typedeftest{i}", s))
            .ToList();

        var ctx = CompilationContext.Create(["std"], files);
        var success = new CompilationPipeline(ctx, null, existingLock).Execute();
        return new Compiled(ctx, success);
    }

    /// <summary>
    /// Compiles on a background thread and fails the test if it does not finish in time.
    /// </summary>
    /// <remarks>
    /// Guards the typedef cycle tests: without cycle protection the alias expansion never
    /// terminates, and a bare <c>Compile</c> would hang CI instead of reporting a failure. The
    /// worker is a background thread so an abandoned hang cannot keep the process alive.
    /// (Unbounded <em>recursion</em> faults the process outright — also a failure, just a louder
    /// one; only a non-recursive spin is catchable here.)
    /// </remarks>
    private static Compiled CompileWithDeadline(string source, int seconds = 20)
    {
        Compiled? compiled = null;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                compiled = Compile(source);
            }
            catch (Exception e)
            {
                failure = e;
            }
        }, maxStackSize: 4 * 1024 * 1024) { IsBackground = true };

        worker.Start();

        if (!worker.Join(TimeSpan.FromSeconds(seconds)))
            Assert.Fail($"compilation did not terminate within {seconds}s — typedef cycle protection is missing");

        if (failure is not null)
            throw failure;

        return compiled!;
    }

    /// <summary>
    /// Whether a type is a typedef <em>declaration</em> — an alias that survived erasure.
    /// </summary>
    /// <remarks>
    /// The bare <c>isTypedef</c> flag cannot be used for this: every std builtin is declared with
    /// <c>isTypedef: true</c>, because the flag is the fourth positional argument of the
    /// <c>new("u4", ["scalar", "builtin", ...], [], true)</c> entries in
    /// <c>IonModule.GetStdModule</c>. So the erased <c>u4</c> itself reports true. A real alias is
    /// distinguished by the single <c>Value</c> field <c>TransformStage.CompileTypedefs</c> emits.
    /// See <see cref="Quirk_EveryStdBuiltinCarriesIsTypedefTrue"/>.
    /// </remarks>
    private static bool IsAliasDeclaration(IonType type)
        => type is { isTypedef: true, fields.Count: > 0 } and not IonArray and not IonUnresolvedType;

    private static IonSchemaLock LockOf(string source)
    {
        var compiled = Compile(source);
        Assert.That(compiled.Success, Is.True,
            () => $"baseline source did not compile: {string.Join("; ", compiled.Diagnostics.Select(d => d.Message))}");
        return SchemaLockGenerator.Generate("typedeftests", compiled.Context.ProcessedModules);
    }

    private static IReadOnlyList<IonDiagnostic> LockDiagnostics(IonSchemaLock existing, string source)
        => Compile(source, existing).Diagnostics
            .Where(d => d.Code.StartsWith("ION002", StringComparison.Ordinal))
            .ToList();

    // ═══════════════════════════════════════════════════════════════════
    // GRAMMAR
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Grammar_BlockLessForm_Parses()
    {
        var t = ParseTypedef("typedef UserId = u4;");

        Assert.That(t.TypeName.Name.Identifier, Is.EqualTo("UserId"));
        Assert.That(t.BaseType!.Name.Identifier, Is.EqualTo("u4"));
    }

    [Test]
    public void Grammar_LegacyEmptyBlock_StillParses()
    {
        var t = ParseTypedef("typedef UserId = u4 {}");

        Assert.That(t.TypeName.Name.Identifier, Is.EqualTo("UserId"));
        Assert.That(t.BaseType!.Name.Identifier, Is.EqualTo("u4"));
    }

    [Test]
    public void Grammar_LegacyBlockWithTrailingSemicolon_StillParses()
    {
        var t = ParseTypedef("typedef UserId = u4 {};");
        Assert.That(t.BaseType!.Name.Identifier, Is.EqualTo("u4"));
    }

    /// <summary>The block body was never read; it stays accepted only for back compatibility.</summary>
    [Test]
    public void Grammar_LegacyBlockBody_IsIgnored()
    {
        var t = ParseTypedef("typedef UserId = u4 { anything at all 123 }");
        Assert.That(t.BaseType!.Name.Identifier, Is.EqualTo("u4"));
    }

    [Test]
    public void Grammar_MissingTerminator_IsRejected()
        => AssertTypedefRejected("typedef UserId = u4");

    [Test]
    public void Grammar_NameOnlyWithSemicolon_ParsesWithNoBaseType()
    {
        var t = ParseTypedef("typedef Handle;");

        Assert.That(t.TypeName.Name.Identifier, Is.EqualTo("Handle"));
        Assert.That(t.BaseType, Is.Null);
    }

    [Test]
    public void Grammar_NameOnlyWithBlock_ParsesWithNoBaseType()
    {
        var t = ParseTypedef("typedef Handle {}");
        Assert.That(t.BaseType, Is.Null);
    }

    [Test]
    public void Grammar_NameOnlyWithoutTerminator_IsRejected()
        => AssertTypedefRejected("typedef Handle");

    /// <summary>
    /// <c>String("typedef")</c> has no word boundary, so <c>typedefFoo</c> used to parse as
    /// <c>typedef Foo</c>.
    /// </summary>
    [Test]
    public void Grammar_KeywordRequiresWordBoundary()
        => AssertTypedefRejected("typedefFoo = u4;");

    [Test]
    public void Grammar_KeywordBoundary_DoesNotBreakTheRealKeyword()
        => Assert.That(ParseTypedef("typedef\tFoo\t=\tu4;").TypeName.Name.Identifier, Is.EqualTo("Foo"));

    /// <summary>The same hole existed on <c>msg</c> and <c>union</c>.</summary>
    [Test]
    public void Grammar_MsgAndUnionKeywords_RequireWordBoundary()
    {
        Assert.That(IonParser.Message.Before(Parser<char>.End).Parse("msgFoo { a: i4; }").Success, Is.False);
        Assert.That(IonParser.Union.Before(Parser<char>.End).Parse("unionFoo { A(x: i4) }").Success, Is.False);
    }

    [Test]
    public void Grammar_TypedefFoo_IsAnInvalidBlockAtFileLevel()
    {
        // Recovery, not a silent `typedef Foo`.
        var file = IonParser.Parse("recovery", "typedefFoo = u4;\nmsg M { a: i4; }\n");

        Assert.That(file.typedefSyntaxes, Is.Empty);
        Assert.That(file.messageSyntaxes.Select(m => m.Name.Identifier), Is.EqualTo(new[] { "M" }));
    }

    [Test]
    public void Grammar_DocComment_AttachesToTypedef()
    {
        var t = ParseTypedef("""
                             /// An opaque handle.
                             typedef Handle = string;
                             """);

        Assert.That(t.Comments, Is.EqualTo("An opaque handle."));
    }

    [Test]
    public void Grammar_DocBlockComment_AttachesToTypedef()
    {
        var t = ParseTypedef("""
                             /** A user identifier. */
                             typedef UserId = u4;
                             """);

        Assert.That(t.Comments, Is.EqualTo("A user identifier."));
    }

    /// <summary>
    /// <c>SkipTrivia</c> stops in front of <c>///</c> and <c>/** */</c>, which used to make a doc
    /// block between the underlying type and the terminator a hard parse error.
    /// </summary>
    [Test]
    public void Grammar_DocBlockBeforeTerminator_IsAccepted()
    {
        Assert.That(ParseTypedef("typedef Foo = Bar /** x */ {}").BaseType!.Name.Identifier, Is.EqualTo("Bar"));
        Assert.That(ParseTypedef("typedef Foo = Bar /** x */;").BaseType!.Name.Identifier, Is.EqualTo("Bar"));
        Assert.That(ParseTypedef("typedef Foo = Bar /// x\n;").BaseType!.Name.Identifier, Is.EqualTo("Bar"));
    }

    [Test]
    public void Grammar_TriviaInEveryPosition()
    {
        var t = ParseTypedef("""
                             typedef /* a */ UserId /* b */ = /* c */ u4 /* d */ ;
                             """);

        Assert.That(t.TypeName.Name.Identifier, Is.EqualTo("UserId"));
        Assert.That(t.BaseType!.Name.Identifier, Is.EqualTo("u4"));
    }

    [Test]
    public void Grammar_LineCommentsAroundTypedef()
    {
        var t = ParseTypedef("""
                             // leading trivia
                             typedef UserId // after the name
                                 = u4 // after the underlying type
                                 ;
                             """);

        Assert.That(t.BaseType!.Name.Identifier, Is.EqualTo("u4"));
    }

    [Test]
    public void Grammar_MultiLine_IsAccepted()
        => Assert.That(ParseTypedef("typedef\n  UserId\n  =\n  u4\n;").BaseType!.Name.Identifier, Is.EqualTo("u4"));

    /// <summary>
    /// The doc comment after a block-less typedef belongs to the <em>next</em> declaration; the
    /// typedef's trailing-trivia handling must not swallow it.
    /// </summary>
    [Test]
    public void Grammar_DocAfterTypedef_AttachesToTheNextDeclaration()
    {
        var members = ParseFileStrict("""
                                      typedef UserId = u4;

                                      /// The user record.
                                      msg User { id: UserId; }
                                      """);

        Assert.That(members.Count, Is.EqualTo(2));
        Assert.That(members[0].Comments, Is.Null);
        Assert.That(members[1].Comments, Is.EqualTo("The user record."));
    }

    [Test]
    public void Grammar_UnderlyingTypeModifiers_AreParsedOnTheBaseTypeSide()
    {
        Assert.That(ParseTypedef("typedef Ids = u4[];").BaseType!.IsArray, Is.True);
        Assert.That(ParseTypedef("typedef MaybeId = u4?;").BaseType!.IsOptional, Is.True);
    }

    [Test]
    public void Grammar_SeveralTypedefsInOneFile()
    {
        var members = ParseFileStrict("""
                                      typedef A = u4;
                                      typedef B = u4 {}
                                      typedef C = u4 {};
                                      """);

        Assert.That(members.OfType<IonTypedefSyntax>().Select(t => t.TypeName.Name.Identifier),
            Is.EqualTo(new[] { "A", "B", "C" }));
    }

    // ═══════════════════════════════════════════════════════════════════
    // COMPILATION — the IonType shape
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Compile_Typedef_ProducesATypedefIonTypeWithTheUnderlyingInFieldZero()
    {
        var compiled = Compile("""
                               /// A user identifier.
                               typedef UserId = u4;
                               msg User { id: UserId; }
                               """);

        Assert.That(compiled.Success, Is.True, () => string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));

        var typedef = compiled.Definition("UserId");

        Assert.Multiple(() =>
        {
            Assert.That(typedef.isTypedef, Is.True);
            Assert.That(typedef.Doc, Is.EqualTo("A user identifier."));
            Assert.That(typedef.fields, Has.Count.EqualTo(1));
            Assert.That(typedef.fields[0].name.Identifier, Is.EqualTo("Value"));
            Assert.That(typedef.fields[0].type.name.Identifier, Is.EqualTo("u4"));
        });
    }

    /// <summary>
    /// An attribute on a typedef parses and is attached to the alias. Nothing consumes it yet, but
    /// it must not be dropped or crash the transform.
    /// </summary>
    [Test]
    public void Compile_AttributeOnTypedef_IsAttachedToTheAlias()
    {
        var compiled = Compile("""
                               @deprecated
                               typedef UserId = u4;
                               msg User { id: UserId; }
                               """);

        Assert.That(compiled.Success, Is.True, () => string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        Assert.That(compiled.Definition("UserId").attributes.Select(a => a.name.Identifier),
            Does.Contain("deprecated"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // ERASURE
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Erasure_FieldType_BecomesTheUnderlyingType()
    {
        var compiled = Compile("""
                               typedef UserId = u4;
                               msg User { id: UserId; }
                               """);

        var fieldType = compiled.FieldType("User", "id");

        Assert.Multiple(() =>
        {
            Assert.That(fieldType.name.Identifier, Is.EqualTo("u4"));
            Assert.That(fieldType.IsBuiltin, Is.True);
            Assert.That(IsAliasDeclaration(fieldType), Is.False);
        });
    }

    /// <summary>
    /// Pins the trap that makes a bare <c>isTypedef</c> check unusable: <c>IonType</c>'s fourth
    /// positional parameter is <c>isTypedef</c>, and every std builtin passes <c>true</c> there.
    /// </summary>
    [Test]
    public void Quirk_EveryStdBuiltinCarriesIsTypedefTrue()
    {
        var builtins = IonModule.GetStdModule.Value.Definitions
            .Where(d => d.IsBuiltin && d is not IonGenericType)
            .ToList();

        Assert.That(builtins, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            // The flag really is set on builtins...
            Assert.That(builtins.Where(b => b.isTypedef).Select(b => b.name.Identifier), Does.Contain("u4"));
            // ...so every typedef predicate must also require the alias's `Value` field.
            Assert.That(builtins.Any(IsAliasDeclaration), Is.False);
        });
    }

    [Test]
    public void Erasure_ServiceArgumentAndReturnType_BecomeTheUnderlyingType()
    {
        var compiled = Compile("""
                               typedef UserId = u4;
                               typedef Email = string;
                               service Users(owner: UserId) {
                                   Lookup(id: UserId): Email;
                               }
                               """);

        Assert.That(compiled.Success, Is.True, () => string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));

        var method = compiled.Method("Users", "Lookup");
        var baseArg = compiled.Method("Users", "Lookup").arguments[0];

        Assert.Multiple(() =>
        {
            // The service-level argument is prepended to every method.
            Assert.That(baseArg.name.Identifier, Is.EqualTo("owner"));
            Assert.That(baseArg.type.name.Identifier, Is.EqualTo("u4"));
            Assert.That(method.arguments[1].type.name.Identifier, Is.EqualTo("u4"));
            Assert.That(method.returnType.name.Identifier, Is.EqualTo("string"));
            Assert.That(IsAliasDeclaration(method.returnType), Is.False);
        });
    }

    [Test]
    public void Erasure_UnionSharedFieldAndCase_BecomeTheUnderlyingType()
    {
        var compiled = Compile("""
                               typedef UserId = u4;
                               union Event(actor: UserId) { Joined(who: UserId), Left(who: UserId) }
                               """);

        Assert.That(compiled.Success, Is.True, () => string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));

        var union = (IonUnion)compiled.Definition("Event");
        var joined = union.types.First(t => t.name.Identifier == "Joined");

        Assert.Multiple(() =>
        {
            Assert.That(union.sharedFields[0].type.name.Identifier, Is.EqualTo("u4"));
            Assert.That(joined.fields.First(f => f.name.Identifier == "who").type.name.Identifier, Is.EqualTo("u4"));
        });
    }

    [Test]
    public void Erasure_Chain_CollapsesFully()
    {
        var compiled = Compile("""
                               typedef A = B;
                               typedef B = C;
                               typedef C = u4;
                               msg M { a: A; }
                               """);

        Assert.That(compiled.Success, Is.True, () => string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));

        Assert.Multiple(() =>
        {
            Assert.That(compiled.FieldType("M", "a").name.Identifier, Is.EqualTo("u4"));
            // The declaration itself collapses too, so the emitted alias targets a concrete type.
            Assert.That(compiled.Definition("A").fields[0].type.name.Identifier, Is.EqualTo("u4"));
            Assert.That(compiled.Definition("B").fields[0].type.name.Identifier, Is.EqualTo("u4"));
        });
    }

    /// <summary>Declaration order must not matter: the restore stage runs after the whole file is transformed.</summary>
    [Test]
    public void Erasure_Chain_CollapsesRegardlessOfDeclarationOrder()
    {
        var compiled = Compile("""
                               msg M { a: A; }
                               typedef A = B;
                               typedef B = u8;
                               """);

        Assert.That(compiled.FieldType("M", "a").name.Identifier, Is.EqualTo("u8"));
    }

    [Test]
    public void Erasure_WorksAcrossFiles()
    {
        var compiled = CompileMany([
            "typedef UserId = u4;",
            "msg User { id: UserId; }"
        ]);

        Assert.That(compiled.Success, Is.True, () => string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        Assert.That(compiled.FieldType("User", "id").name.Identifier, Is.EqualTo("u4"));
    }

    [Test]
    public void Erasure_InsideAnArrayUseSite()
    {
        var compiled = Compile("""
                               typedef UserId = u4;
                               msg M { ids: UserId[]; }
                               """);

        var fieldType = compiled.FieldType("M", "ids");

        Assert.That(fieldType, Is.InstanceOf<IonGenericType>());
        var generic = (IonGenericType)fieldType;

        Assert.Multiple(() =>
        {
            Assert.That(generic.IsArray, Is.True);
            Assert.That(generic.TypeArguments[0].name.Identifier, Is.EqualTo("u4"));
            Assert.That(IsAliasDeclaration(generic.TypeArguments[0]), Is.False);
        });
    }

    [Test]
    public void Erasure_InsideAnOptionalUseSite()
    {
        var compiled = Compile("""
                               typedef UserId = u4;
                               msg M { id: UserId?; }
                               """);

        var generic = (IonGenericType)compiled.FieldType("M", "id");

        Assert.Multiple(() =>
        {
            Assert.That(generic.IsMaybe, Is.True);
            Assert.That(generic.TypeArguments[0].name.Identifier, Is.EqualTo("u4"));
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // ERASURE — every shape of underlying type
    // ═══════════════════════════════════════════════════════════════════

    [TestCase("u4", "u4")]
    [TestCase("i8", "i8")]
    [TestCase("bool", "bool")]
    [TestCase("f8", "f8")]
    [TestCase("string", "string")]
    [TestCase("guid", "guid")]
    [TestCase("datetime", "datetime")]
    [TestCase("bytes", "bytes")]
    public void Erasure_BuiltinUnderlying(string underlying, string expected)
    {
        var compiled = Compile($"typedef Alias = {underlying};\nmsg M {{ a: Alias; }}\n");

        Assert.That(compiled.Success, Is.True, () => string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        Assert.That(compiled.FieldType("M", "a").name.Identifier, Is.EqualTo(expected));
    }

    [Test]
    public void Erasure_MessageUnderlying()
    {
        var compiled = Compile("""
                               msg Point { x: f4; y: f4; }
                               typedef Position = Point;
                               msg M { at: Position; }
                               """);

        var fieldType = compiled.FieldType("M", "at");

        Assert.Multiple(() =>
        {
            Assert.That(fieldType.name.Identifier, Is.EqualTo("Point"));
            Assert.That(IsAliasDeclaration(fieldType), Is.False);
            Assert.That(fieldType.fields.Select(f => f.name.Identifier), Is.EqualTo(new[] { "x", "y" }));
        });
    }

    [Test]
    public void Erasure_EnumUnderlying()
    {
        var compiled = Compile("""
                               enum Colour { Red, Green }
                               typedef Shade = Colour;
                               msg M { c: Shade; }
                               """);

        var fieldType = compiled.FieldType("M", "c");

        Assert.Multiple(() =>
        {
            Assert.That(fieldType, Is.InstanceOf<IonEnum>());
            Assert.That(fieldType.name.Identifier, Is.EqualTo("Colour"));
        });
    }

    [Test]
    public void Erasure_FlagsUnderlying()
    {
        var compiled = Compile("""
                               flags Perm : u4 { Read = 1, Write = 2 }
                               typedef Access = Perm;
                               msg M { p: Access; }
                               """);

        Assert.That(compiled.FieldType("M", "p"), Is.InstanceOf<IonFlags>());
    }

    /// <summary>
    /// The modifier belongs to the underlying type, so the alias expands to the whole wrapper.
    /// </summary>
    [Test]
    public void Erasure_ArrayUnderlying()
    {
        var compiled = Compile("""
                               typedef Ids = u4[];
                               msg M { a: Ids; }
                               """);

        var generic = (IonGenericType)compiled.FieldType("M", "a");

        Assert.Multiple(() =>
        {
            Assert.That(generic.IsArray, Is.True);
            Assert.That(generic.TypeArguments[0].name.Identifier, Is.EqualTo("u4"));
        });
    }

    [Test]
    public void Erasure_OptionalUnderlying()
    {
        var compiled = Compile("""
                               typedef MaybeId = u4?;
                               msg M { a: MaybeId; }
                               """);

        var generic = (IonGenericType)compiled.FieldType("M", "a");

        Assert.Multiple(() =>
        {
            Assert.That(generic.IsMaybe, Is.True);
            Assert.That(generic.TypeArguments[0].name.Identifier, Is.EqualTo("u4"));
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // DIAGNOSTICS
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Diagnostic_ION0014_FiresWhenThereIsNoUnderlyingType()
    {
        var compiled = Compile("typedef Handle;\nmsg M { a: i4; }\n");

        Assert.That(compiled.ErrorCodes, Does.Contain("ION0014"));
        Assert.That(compiled.Diagnostics.First(d => d.Code == "ION0014").Message, Does.Contain("Handle"));
    }

    [Test]
    public void Diagnostic_ION0014_AlsoFiresForTheBlockForm()
        => Assert.That(Compile("typedef Handle {}\nmsg M { a: i4; }\n").ErrorCodes, Does.Contain("ION0014"));

    [Test]
    public void Diagnostic_ION0014_DoesNotFireWhenAnUnderlyingTypeIsPresent()
        => Assert.That(Compile("typedef Handle = string;\nmsg M { a: Handle; }\n").ErrorCodes,
            Does.Not.Contain("ION0014"));

    [TestCase("typedef Foo? = u4;", "?")]
    [TestCase("typedef Foo[] = u4;", "[]")]
    [TestCase("typedef Foo~ = u4;", "~")]
    public void Diagnostic_ION0015_FiresForAModifierOnTheNameSide(string source, string modifier)
    {
        var compiled = Compile(source + "\nmsg M { a: i4; }\n");

        Assert.That(compiled.ErrorCodes, Does.Contain("ION0015"));
        Assert.That(compiled.Diagnostics.First(d => d.Code == "ION0015").Message, Does.Contain(modifier));
    }

    /// <summary>A modifier on the <em>underlying</em> type is legal and must not be flagged.</summary>
    [TestCase("typedef Foo = u4?;")]
    [TestCase("typedef Foo = u4[];")]
    public void Diagnostic_ION0015_DoesNotFireForAModifierOnTheUnderlyingType(string source)
        => Assert.That(Compile(source + "\nmsg M { a: Foo; }\n").ErrorCodes, Does.Not.Contain("ION0015"));

    [Test]
    public void Diagnostic_ION0016_FiresForAGenericTypedef()
    {
        var compiled = Compile("typedef Box<T> = Maybe<T>;\nmsg M { a: i4; }\n");

        Assert.That(compiled.ErrorCodes, Does.Contain("ION0016"));
        Assert.That(compiled.Diagnostics.First(d => d.Code == "ION0016").Message, Does.Contain("Box"));
    }

    [Test]
    public void Diagnostic_ION0016_DoesNotFireForANonGenericTypedef()
        => Assert.That(Compile("typedef Foo = u4;\nmsg M { a: Foo; }\n").ErrorCodes, Does.Not.Contain("ION0016"));

    /// <summary>
    /// <c>CircularTypeReferenceStage</c> deliberately skips self references, so a self
    /// referential typedef is invisible to it and needs its own diagnostic.
    /// </summary>
    [Test]
    public void Diagnostic_ION0017_SelfReferentialTypedef_TerminatesAndReports()
    {
        var compiled = CompileWithDeadline("typedef A = A;\nmsg M { a: A; }\n");

        Assert.That(compiled.Success, Is.False);
        Assert.That(compiled.ErrorCodes, Does.Contain("ION0017"));
        Assert.That(compiled.Diagnostics.First(d => d.Code == "ION0017").Message, Does.Contain("A → A"));
    }

    [Test]
    public void Diagnostic_ION0017_MutualCycle_TerminatesAndReports()
    {
        var compiled = CompileWithDeadline("typedef A = B;\ntypedef B = A;\nmsg M { a: A; }\n");

        Assert.That(compiled.Success, Is.False);
        Assert.That(compiled.ErrorCodes, Does.Contain("ION0017"));
    }

    [Test]
    public void Diagnostic_ION0017_LongerCycle_TerminatesAndReports()
    {
        var compiled = CompileWithDeadline("typedef A = B;\ntypedef B = C;\ntypedef C = A;\nmsg M { a: A; }\n");

        Assert.That(compiled.Success, Is.False);
        Assert.That(compiled.ErrorCodes, Does.Contain("ION0017"));
    }

    /// <summary>A cycle reached through a wrapper is still a cycle, and still has to terminate.</summary>
    [Test]
    public void Diagnostic_ION0017_CycleThroughAnArrayWrapper_Terminates()
    {
        var compiled = CompileWithDeadline("typedef A = B[];\ntypedef B = A;\nmsg M { a: A; }\n");

        Assert.That(compiled.Success, Is.False);
        Assert.That(compiled.ErrorCodes, Does.Contain("ION0017"));
    }

    /// <summary>An unused cyclic typedef must still be reported — the definition itself is walked.</summary>
    [Test]
    public void Diagnostic_ION0017_FiresEvenWhenTheAliasIsNeverUsed()
    {
        var compiled = CompileWithDeadline("typedef A = A;\nmsg M { a: i4; }\n");
        Assert.That(compiled.ErrorCodes, Does.Contain("ION0017"));
    }

    [Test]
    public void Diagnostic_ION0017_DoesNotFireForAnAcyclicChain()
        => Assert.That(Compile("typedef A = B;\ntypedef B = u4;\nmsg M { a: A; }\n").ErrorCodes,
            Does.Not.Contain("ION0017"));

    /// <summary>
    /// Two aliases of the same underlying type share no expansion state — this used to be a
    /// plausible false positive if the cycle path were shared between sibling expansions.
    /// </summary>
    [Test]
    public void Diagnostic_ION0017_DoesNotFireForTwoFieldsUsingTheSameAlias()
    {
        var compiled = Compile("""
                               typedef UserId = u4;
                               msg M { a: UserId; b: UserId; c: UserId[]; }
                               """);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.ErrorCodes, Does.Not.Contain("ION0017"));
            Assert.That(compiled.Success, Is.True);
        });
    }

    [Test]
    public void Diagnostic_ION0002_TypedefCollidingWithAnEnum_IsReported()
    {
        // DuplicateSymbolValidationStage never registered enum/flags/union, so this pair was
        // silently accepted and type resolution picked whichever came first.
        var compiled = Compile("typedef Foo = u4;\nenum Foo { A }\nmsg M { a: i4; }\n");

        Assert.That(compiled.ErrorCodes, Does.Contain("ION0002"));
    }

    [Test]
    public void Diagnostic_ION0009_UnresolvedUnderlyingType_IsReported()
        => Assert.That(Compile("typedef Foo = Nope;\nmsg M { a: Foo; }\n").ErrorCodes, Does.Contain("ION0009"));

    // ═══════════════════════════════════════════════════════════════════
    // CODE GENERATION
    // ═══════════════════════════════════════════════════════════════════

    private const string AliasSource =
        """
        /// A user identifier.
        typedef UserId = u4;
        msg User { id: UserId; name: string; }
        """;

    private static IReadOnlyList<IonType> DefinitionsOf(string source)
    {
        var compiled = Compile(source);
        Assert.That(compiled.Success, Is.True, () => string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        return compiled.Definitions;
    }

    [Test]
    public void CodeGen_CSharp_NoFormatterIsGeneratedForATypedef()
    {
        var formatters = new IonCSharpGenerator("Gen").GenerateAllFormatters(DefinitionsOf(AliasSource));

        Assert.Multiple(() =>
        {
            Assert.That(formatters, Does.Not.Contain("Ion_UserId_Formatter"));
            // ...but the message that uses it still gets one.
            Assert.That(formatters, Does.Contain("Ion_User_Formatter"));
        });
    }

    [Test]
    public void CodeGen_CSharp_TypedefIsNotRegisteredInModuleInit()
    {
        var moduleInit = new IonCSharpGenerator("Gen").GenerateModuleInit(DefinitionsOf(AliasSource), [], false, false);

        Assert.Multiple(() =>
        {
            Assert.That(moduleInit, Does.Not.Contain("IonFormatterStorage<UserId>"));
            Assert.That(moduleInit, Does.Contain("IonFormatterStorage<User>"));
        });
    }

    [Test]
    public void CodeGen_CSharp_TypedefBecomesAGlobalUsingAlias()
    {
        var moduleInit = new IonCSharpGenerator("Gen").GenerateModuleInit(DefinitionsOf(AliasSource), [], false, false);

        Assert.Multiple(() =>
        {
            // The target must be a real CLR name: a using alias may not target another alias,
            // so `global using UserId = u4;` would fail with CS0246.
            Assert.That(moduleInit, Does.Contain("global using UserId = System.UInt32;"));
            // The alias has to precede the namespace declaration to be legal C#.
            Assert.That(moduleInit.IndexOf("global using UserId", StringComparison.Ordinal),
                Is.LessThan(moduleInit.IndexOf("namespace Gen;", StringComparison.Ordinal)));
        });
    }

    /// <summary>The old stub emitted <c>public readonly record struct UserId(uint Value);</c> — an
    /// undocumented newtype that changed the wire format.</summary>
    [Test]
    public void CodeGen_CSharp_TypedefIsNotEmittedAsARecordStruct()
    {
        var types = new IonCSharpGenerator("Gen").GenerateTypes(DefinitionsOf(AliasSource));

        Assert.Multiple(() =>
        {
            Assert.That(types, Does.Not.Contain("record struct UserId"));
            Assert.That(types, Does.Contain("public sealed record User("));
            // Erasure: the field carries the underlying type, not the alias.
            Assert.That(types, Does.Contain("u4 id"));
        });
    }

    [Test]
    public void CodeGen_CSharp_ModuleInitWithoutTypedefs_IsUnchanged()
    {
        var generator = new IonCSharpGenerator("Gen");
        var moduleInit = generator.GenerateModuleInit(DefinitionsOf("msg User { id: u4; }"), [], false, false);

        Assert.Multiple(() =>
        {
            Assert.That(moduleInit, Does.Not.Contain("global using"));
            Assert.That(moduleInit, Does.Contain(generator.FileHeader()));
        });
    }

    [TestCase("u4", "System.UInt32")]
    [TestCase("i4", "System.Int32")]
    [TestCase("u8", "System.UInt64")]
    [TestCase("f8", "System.Double")]
    [TestCase("bool", "System.Boolean")]
    [TestCase("string", "System.String")]
    [TestCase("guid", "System.Guid")]
    [TestCase("datetime", "System.DateTimeOffset")]
    [TestCase("decimal", "System.Decimal")]
    [TestCase("bytes", "ion.runtime.IonBytes")]
    public void CodeGen_CSharp_AliasTargetsAFullyQualifiedClrType(string underlying, string clr)
    {
        var source = $"typedef Alias = {underlying};\nmsg M {{ a: Alias; }}\n";
        var moduleInit = new IonCSharpGenerator("Gen").GenerateModuleInit(DefinitionsOf(source), [], false, false);

        Assert.That(moduleInit, Does.Contain($"global using Alias = {clr};"));
    }

    [Test]
    public void CodeGen_CSharp_AliasOfAMessageIsNamespaceQualified()
    {
        var source = "msg Point { x: f4; }\ntypedef Position = Point;\nmsg M { at: Position; }\n";
        var moduleInit = new IonCSharpGenerator("Gen").GenerateModuleInit(DefinitionsOf(source), [], false, false);

        Assert.That(moduleInit, Does.Contain("global using Position = Gen.Point;"));
    }

    [Test]
    public void CodeGen_CSharp_AliasOfAnArrayUsesIonArray()
    {
        var source = "typedef Ids = u4[];\nmsg M { a: Ids; }\n";
        var moduleInit = new IonCSharpGenerator("Gen").GenerateModuleInit(DefinitionsOf(source), [], false, false);

        Assert.That(moduleInit, Does.Contain("global using Ids = ion.runtime.IonArray<System.UInt32>;"));
    }

    [Test]
    public void CodeGen_TypeScript_EmitsATypeAliasAndNoFormatter()
    {
        var defs = DefinitionsOf(AliasSource);
        var generator = new IonTypeScriptGenerator("Gen");

        var types = generator.GenerateTypes(defs);
        var formatters = generator.GenerateAllFormatters(defs);

        Assert.Multiple(() =>
        {
            // A scalar underlying type resolves to its structural TS type: `u4` is only a
            // non-exported `declare type` in the bundle preamble.
            Assert.That(types, Does.Contain("export type UserId = number;"));
            Assert.That(formatters, Does.Not.Contain("\"UserId\""));
            Assert.That(formatters, Does.Contain("\"User\""));
            // The alias must precede the interface that (textually) sits after it.
            Assert.That(types.IndexOf("export type UserId", StringComparison.Ordinal),
                Is.LessThan(types.IndexOf("export interface User", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void CodeGen_TypeScript_AliasOfANonScalarKeepsTheGeneratedName()
    {
        var types = new IonTypeScriptGenerator("Gen")
            .GenerateTypes(DefinitionsOf("msg Point { x: f4; }\ntypedef Position = Point;\nmsg M { at: Position; }\n"));

        Assert.That(types, Does.Contain("export type Position = Point;"));
    }

    [Test]
    public void CodeGen_Rust_EmitsATypeAliasAndNoFormatter()
    {
        var defs = DefinitionsOf(AliasSource);
        var generator = new RustCodeGenerator("Gen");

        var types = generator.GenerateTypes(defs);
        var formatters = generator.GenerateAllFormatters(defs);

        Assert.Multiple(() =>
        {
            Assert.That(types, Does.Contain("pub type UserId = u32;"));
            Assert.That(formatters, Does.Not.Contain("UserId"));
            Assert.That(types.IndexOf("pub type UserId", StringComparison.Ordinal),
                Is.LessThan(types.IndexOf("struct User", StringComparison.Ordinal)));
        });
    }

    /// <summary>
    /// <see cref="IonArray"/> copies <c>isTypedef</c> off its element type, so an array whose
    /// element is a typedef reports <c>isTypedef: true</c> and would be routed to the typedef
    /// emitter and rendered as an alias of its own element.
    /// </summary>
    [Test]
    public void CodeGen_ArrayOfTypedef_IsNotMistakenForATypedefDeclaration()
    {
        var typedef = new IonType("UserId", [], [new IonField("Value", new IonType("u4", [], []), [])], true);
        var arrayOfTypedef = new IonArray(typedef, 1, false);

        Assert.That(arrayOfTypedef.isTypedef, Is.True, "precondition: IonArray propagates isTypedef");

        var generated = new IonCSharpGenerator("Gen").GenerateTypes([arrayOfTypedef]);

        Assert.That(generated, Does.Not.Contain("global using"));
        Assert.That(generated, Does.Not.Contain("record struct UserId"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // SCHEMA LOCK
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void SchemaLock_TypedefsAreNotRecorded()
    {
        var snapshot = LockOf(AliasSource);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Definitions.Keys, Does.Not.Contain("UserId"));
            Assert.That(snapshot.Definitions.Keys, Does.Contain("User"));
            // Erasure means the use site already records the underlying type.
            Assert.That(snapshot.Definitions["User"].Fields![0].Type, Is.EqualTo("u4"));
        });
    }

    [Test]
    public void SchemaLock_DocCommentChangeOnATypedef_ProducesNoDiagnostic()
    {
        var snapshot = LockOf("""
                              /// The original wording.
                              typedef UserId = u4;
                              msg User { id: UserId; }
                              """);

        var diagnostics = LockDiagnostics(snapshot, """
                                                    /// Completely rewritten prose, same type.
                                                    typedef UserId = u4;
                                                    msg User { id: UserId; }
                                                    """);

        Assert.That(diagnostics, Is.Empty,
            () => string.Join("; ", diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    /// <summary>
    /// The load-bearing justification for keeping typedefs out of the lock: because the alias is
    /// erased, widening it is reported once per <em>use site</em>, which is where the wire
    /// actually changes.
    /// </summary>
    [Test]
    public void SchemaLock_ChangingTheUnderlyingType_ReportsION0022AtEveryUseSite()
    {
        var snapshot = LockOf("""
                              typedef UserId = u4;
                              msg User { id: UserId; friend: UserId; name: string; }
                              """);

        var diagnostics = LockDiagnostics(snapshot, """
                                                    typedef UserId = u8;
                                                    msg User { id: UserId; friend: UserId; name: string; }
                                                    """);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo(new[] { "ION0022", "ION0022" }));
            Assert.That(diagnostics[0].Message,
                Is.EqualTo("Breaking change: field 'id' in 'User' changed type from 'u4' to 'u8'."));
            Assert.That(diagnostics[1].Message,
                Is.EqualTo("Breaking change: field 'friend' in 'User' changed type from 'u4' to 'u8'."));
        });
    }

    [Test]
    public void SchemaLock_ChangingTheUnderlyingType_AlsoReportsOnServiceSignatures()
    {
        var snapshot = LockOf("""
                              typedef UserId = u4;
                              service Users() { Get(id: UserId): UserId; }
                              """);

        var diagnostics = LockDiagnostics(snapshot, """
                                                    typedef UserId = u8;
                                                    service Users() { Get(id: UserId): UserId; }
                                                    """);

        Assert.That(diagnostics.Select(d => d.Code), Does.Contain("ION0026"));
    }

    [Test]
    public void SchemaLock_AddingATypedefWithoutChangingUseSites_ProducesNoDiagnostic()
    {
        var snapshot = LockOf("msg User { id: u4; }");

        // Introducing an alias over the same underlying type is a pure refactor.
        var diagnostics = LockDiagnostics(snapshot, "typedef UserId = u4;\nmsg User { id: UserId; }\n");

        Assert.That(diagnostics, Is.Empty,
            () => string.Join("; ", diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    [Test]
    public void SchemaLock_LockVersionIsUnchanged()
        => Assert.That(IonSchemaLock.CurrentVersion, Is.EqualTo(1),
            "typedef support must not change the lock format");
}
