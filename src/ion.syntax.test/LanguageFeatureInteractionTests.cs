namespace ion.syntax.test;

using ion.runtime;

/// <summary>
/// The seams between the five features that landed together, the guarantee that none of them changed
/// how a file predating them compiles, and the pins for behaviour that is currently questionable.
/// </summary>
/// <remarks>
/// Each feature has its own suite; this one exists for the combinations nobody designed — a mixin
/// field whose type is an inline anonymous message, a fixed array of a hoisted type, a
/// <c>Map</c> keyed by a mixin — and for the properties that are only meaningful across all of them:
/// one mistake produces one diagnostic, and nothing the compiler invented is ever quoted back at the
/// author.
/// </remarks>
public class LanguageFeatureInteractionTests
{
    // ═══════════════════════════════════════════════════════════════════
    // THE REAL FIXTURES
    // ═══════════════════════════════════════════════════════════════════

    private static DirectoryInfo FixtureDirectory()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        for (; dir is not null; dir = dir.Parent)
        {
            var candidate = new DirectoryInfo(Path.Combine(dir.FullName, "src", "tests", "Contracts", "Contracts"));
            if (candidate.Exists)
                return candidate;
        }

        throw new DirectoryNotFoundException(
            $"src/tests/Contracts/Contracts not found above {TestContext.CurrentContext.TestDirectory}");
    }

    /// <remarks>
    /// The names are prefixed per consumer. <c>TestCaseData.SetName</c> replaces the <em>whole</em>
    /// test name, method included, so two <c>[TestCaseSource]</c> methods sharing a naming scheme
    /// produce colliding names and the runner reports them as one — which is how a genuine fixture
    /// failure can hide behind a sibling that passed.
    /// </remarks>
    private static IEnumerable<TestCaseData> FixturesNamed(string prefix) => FixtureDirectory()
        .GetFiles("*.ion")
        .OrderBy(f => f.Name, StringComparer.Ordinal)
        .Select(f => new TestCaseData(f.Name)
            .SetName($"{prefix}_{Path.GetFileNameWithoutExtension(f.Name)}"));

    private static IEnumerable<TestCaseData> CompileFixtures() => FixturesNamed("Compiles");

    private static IEnumerable<TestCaseData> DiagnosticFixtures() => FixturesNamed("NoNewDiagnostics");

    /// <summary>
    /// The integration suite's checked-in contracts still compile, error-free, through the whole
    /// pipeline — not merely parse, which is what <see cref="ContractFixtureParseTests"/> covers.
    /// None of them uses a generic argument, a fixed size, a mixin or an inline type, which is the
    /// point: five new stages now run over every file, and not one of them may raise a diagnostic
    /// against a schema written before they existed.
    /// </summary>
    [TestCaseSource(nameof(CompileFixtures))]
    public void Fixture_StillCompilesWithoutErrors(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(FixtureDirectory().FullName, fileName));
        var compiled = LanguageFeature.Compile(text);

        Assert.That(compiled.HasParseErrors, Is.False, $"{fileName} no longer parses");
        Assert.That(compiled.Errors, Is.Empty, () => $"{fileName}: {compiled.Describe()}");
    }

    /// <summary>
    /// And none of the five new codes fires on any of them, at any severity — a fixture that started
    /// drawing an ION0060 as a warning would still be a regression.
    /// </summary>
    [TestCaseSource(nameof(DiagnosticFixtures))]
    public void Fixture_RaisesNoneOfTheNewDiagnostics(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(FixtureDirectory().FullName, fileName));
        var compiled = LanguageFeature.Compile(text);

        Assert.That(compiled.Diagnostics
                .Where(d => string.CompareOrdinal(d.Code, "ION0060") >= 0 &&
                            string.CompareOrdinal(d.Code, "ION0069") < 0)
                .Select(d => $"{d.Code}: {d.Message}"),
            Is.Empty);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ALL FIVE AT ONCE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// One schema using every new feature, asserted all the way down to the lock. This is the guard
    /// against a combination that works in isolation and not together — a mixin whose field is an
    /// inline type, inside a message whose other fields are nested collections and fixed arrays.
    /// </summary>
    [Test]
    public void EveryFeature_InOneSchema()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin Audited {
                                                   createdAt: datetime;
                                                   createdBy: msg { id: guid; name: string; };
                                               }

                                               mixin Traced with Audited { traceId: guid; }

                                               msg Document with Traced {
                                                   title: string;
                                                   tags: Set<string>;
                                                   index: Map<string, Array<Document>>;
                                                   basis: f4[16];
                                                   price: decimal;
                                                   shipping: msg { address: string; };
                                               }

                                               service Api() { Get(): Document; }
                                               """);

        compiled.AssertAccepted();

        var locked = compiled.Lock();

        Assert.Multiple(() =>
        {
            // The mixin itself is not a type and has no entry; its inline field hoisted once, named
            // after the mixin.
            Assert.That(locked.Definitions.Keys, Is.EquivalentTo(new[]
            {
                "Document", "DocumentShipping", "AuditedCreatedBy", "Api"
            }));

            Assert.That(locked.Definitions["Document"].Fields!.Select(f => $"{f.Index}:{f.Name}:{f.Type}"),
                Is.EqualTo(new[]
                {
                    "0:createdAt:datetime",
                    "1:createdBy:AuditedCreatedBy",
                    "2:traceId:guid",
                    "3:title:string",
                    "4:tags:Set<string>",
                    "5:index:Map<string, Array<Document>>",
                    "6:basis:Array<f4, 16>",
                    "7:price:decimal",
                    "8:shipping:DocumentShipping"
                }));
        });
    }

    /// <summary>The named interactions, one per pairing, asserted on the lowered type.</summary>
    [TestCase("Map<string, Data~>", "Map<string, Partial<Data>>", TestName = "Interaction_MapOfPartial")]
    [TestCase("Set<i4>[3]", "Array<Set<i4>, 3>", TestName = "Interaction_FixedArrayOfSet")]
    [TestCase("Maybe<f4[16]>", "Maybe<Array<f4, 16>>", TestName = "Interaction_MaybeOfFixedArray")]
    [TestCase("f4[16]?", "Maybe<Array<f4, 16>>", TestName = "Interaction_FixedArrayOptional")]
    [TestCase("Map<string, decimal[8]>", "Map<string, Array<decimal, 8>>", TestName = "Interaction_MapOfFixedDecimal")]
    [TestCase("Set<Map<guid, Data>>", "Set<Map<guid, Data>>", TestName = "Interaction_SetOfMap")]
    [TestCase("Map<guid, Set<Data>>[2]", "Array<Map<guid, Set<Data>>, 2>", TestName = "Interaction_FixedArrayOfMapOfSet")]
    public void Interaction_LowersAsWritten(string written, string canonical)
    {
        var compiled = LanguageFeature.Compile($"msg Data {{ z: i4; }}\nmsg M {{ a: {written}; }}");

        compiled.AssertAccepted();
        Assert.That(compiled.FieldType("M", "a"), Is.EqualTo(canonical));
    }

    /// <summary>A mixin field whose type is an inline anonymous message, then fixed-size.</summary>
    [Test]
    public void Interaction_MixinFieldOfAFixedArrayOfAnInlineType()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin Audit { stamps: msg { at: datetime; }[3]; }
                                               msg M with Audit { own: i4; }
                                               service Api() { Get(): M; }
                                               """);

        compiled.AssertAccepted();

        Assert.Multiple(() =>
        {
            Assert.That(compiled.FieldType("M", "stamps"), Is.EqualTo("Array<AuditStamps, 3>"));
            Assert.That(compiled.Lock().Definitions["M"].Fields![0].Type,
                Is.EqualTo("Array<AuditStamps, 3>"));
        });
    }

    /// <summary>A hoisted type used as a Map value and inside a Set, at depth.</summary>
    [Test]
    public void Interaction_HoistedTypeInsideCollections()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg Order { line: msg { sku: string; }; }
                                               msg Basket { byId: Map<guid, Set<OrderLine>>; }
                                               service Api() { Get(): Basket; Also(): Order; }
                                               """);

        compiled.AssertAccepted();
        Assert.That(compiled.FieldType("Basket", "byId"), Is.EqualTo("Map<guid, Set<OrderLine>>"));
    }

    /// <summary>
    /// A mixin in type position inside a collection is still a mixin in type position, and is still
    /// exactly one diagnostic.
    /// </summary>
    [TestCase("Map<string, A>", TestName = "Interaction_MixinAsMapValue")]
    [TestCase("Set<A>", TestName = "Interaction_MixinAsSetElement")]
    [TestCase("A[4]", TestName = "Interaction_MixinAsFixedArrayElement")]
    [TestCase("Map<string, Array<Set<A>>>", TestName = "Interaction_MixinDeepInside")]
    public void Interaction_AMixinInsideACollection_IsOneDiagnostic(string written)
    {
        var compiled = LanguageFeature.Compile($"mixin A {{ x: i4; }}\nmsg M {{ f: {written}; }}");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.MixinInTypePosition }),
            compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ONE MISTAKE, ONE DIAGNOSTIC
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every single-mistake source that touches the new features, asserted to produce exactly the one
    /// code it is about. A cascade here is a real defect: the second message always names something
    /// the author did not write.
    /// </summary>
    [TestCase("msg M with Nope { b: i4; }", LanguageFeature.WithClause,
        TestName = "Single_UnknownMixin")]
    [TestCase("msg D { a: i4; }\nmsg M with D { b: i4; }", LanguageFeature.WithClause,
        TestName = "Single_WithClauseNamesAMsg")]
    [TestCase("mixin A { x: i4; }\nmsg M with A, A { y: i4; }", LanguageFeature.WithClause,
        TestName = "Single_DuplicateInClause")]
    [TestCase("mixin A with A { x: i4; }\nmsg M with A { y: i4; }", LanguageFeature.CyclicMixin,
        TestName = "Single_SelfCycle")]
    [TestCase("mixin A { x: i4; }\nmsg M with A { f: A; }", LanguageFeature.MixinInTypePosition,
        TestName = "Single_MixinInTypePosition")]
    [TestCase("msg M { a: Map<f4, i4>; }", LanguageFeature.MapKey,
        TestName = "Single_BadMapKey")]
    [TestCase("msg M { a: Map<string>; }", LanguageFeature.Arity,
        TestName = "Single_BadArity")]
    [TestCase("msg M { a: f4[0]; }", LanguageFeature.FixedArraySize,
        TestName = "Single_BadFixedSize")]
    [TestCase("msg OrderS { z: i4; }\nmsg Order { s: msg { a: i4; }; }", LanguageFeature.InlineNameCollision,
        TestName = "Single_InlineNameCollision")]
    [TestCase("msg M { a: Array<msg { z: i4; }>; }", LanguageFeature.InlineNotAllowed,
        TestName = "Single_InlineNotAllowed")]
    public void OneMistake_ProducesOneDiagnostic(string source, string code)
    {
        var compiled = LanguageFeature.Compile(source);

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { code }), compiled.Describe);
    }

    /// <summary>
    /// Independent mistakes are still all reported — the rule above is "no cascades", not "one error
    /// per file".
    /// </summary>
    [Test]
    public void IndependentMistakes_AreAllReported()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg M {
                                                   a: Map<f4, i4>;
                                                   b: Map<string>;
                                                   c: i4[0];
                                               }
                                               """);

        Assert.That(compiled.ErrorCodes, Is.EquivalentTo(new[]
        {
            LanguageFeature.MapKey, LanguageFeature.Arity, LanguageFeature.FixedArraySize
        }), compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // MULTI-FILE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The type namespace is flat and global, so a mixin declared in one file is includable from
    /// another and a derived name claimed in one file collides in another.
    /// </summary>
    [Test]
    public void MultiFile_AMixinCrossesFileBoundaries()
    {
        var compiled = LanguageFeature.CompileMany([
            "mixin Audited { createdAt: datetime; }",
            "msg Doc with Audited { title: string; }\nservice Api() { Get(): Doc; }"
        ]);

        compiled.AssertAccepted();
        Assert.That(compiled.FieldNames("Doc"), Is.EqualTo(new[] { "createdAt", "title" }));
    }

    [Test]
    public void MultiFile_ADerivedNameCollidesAcrossFiles()
    {
        var compiled = LanguageFeature.CompileMany([
            "msg OrderShipping { z: i4; }",
            "msg Order { shipping: msg { a: i4; }; }"
        ]);

        Assert.That(compiled.WithCode(LanguageFeature.InlineNameCollision), Has.Count.EqualTo(1),
            compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CURRENT BEHAVIOUR — pinned, and flagged in the review
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// QUESTIONABLE, pinned so a change is deliberate. Two fields of a message with the same name are
    /// silently accepted when the message has no <c>with</c> clause, and reported as ION0065 when it
    /// does — with a message that reads "contributed by msg 'M' and is also declared by msg 'M'".
    /// The rule is right (a duplicate must not reach the generators) but it is enforced only on the
    /// half of messages that happen to use a mixin, under a code that is about mixins.
    /// </summary>
    [Test]
    public void Questionable_DuplicateOwnFieldsDependOnWhetherAWithClauseExists()
    {
        var withoutClause = LanguageFeature.Compile("msg M { x: i4; x: i8; }");
        var withClause = LanguageFeature.Compile("mixin A { q: i4; }\nmsg M with A { x: i4; x: i8; }");

        Assert.Multiple(() =>
        {
            Assert.That(withoutClause.Errors, Is.Empty, "no clause: the duplicate is not noticed at all");
            Assert.That(withoutClause.FieldNames("M"), Is.EqualTo(new[] { "x", "x" }));

            Assert.That(withClause.WithCode(LanguageFeature.FieldCollision), Has.Count.EqualTo(1));
            Assert.That(withClause.Only(LanguageFeature.FieldCollision).Message,
                Does.Contain("contributed by msg 'M' and is also declared by msg 'M'"));
        });
    }

    /// <summary>
    /// QUESTIONABLE, pinned. <c>IonTypeSites.AsWritten</c> renders a fixed size as a bare <c>[]</c>,
    /// so ION0061 quotes <c>'string[]'</c> for a key the author wrote as <c>string[4]</c>. Harmless
    /// today — the type is rejected either way — but it is the same "echo what was written" contract
    /// ION0062 goes out of its way to honour.
    /// </summary>
    [Test]
    public void Questionable_ION0061DropsAFixedSizeFromTheEcho()
    {
        var compiled = LanguageFeature.Compile("msg M { m: Map<string[4], i4>; }");

        Assert.That(compiled.Only(LanguageFeature.MapKey).Message, Does.StartWith("'string[]' cannot be"));
    }

    /// <summary>
    /// QUESTIONABLE, pinned. <c>Array&lt;f4, 16&gt;</c> is the spelling <c>ion.lock.json</c> teaches,
    /// and it is not a valid type reference — a size is a suffix, not an argument. It fails to parse,
    /// error recovery turns the whole declaration into an <see cref="InvalidIonBlock"/>, and
    /// <c>CompilationPipeline</c> never looks at those: the compile reports nothing and succeeds with
    /// no definitions at all.
    /// <para>
    /// The hole is pre-existing and not this feature's (<c>msg M { a: i4 }</c> behaves identically),
    /// but the lock's rendering makes it newly easy to hit. Surfacing an unparsed declaration belongs
    /// in the pipeline rather than only in <c>ionc</c> and the LSP.
    /// </para>
    /// </summary>
    [Test]
    public void Questionable_AnUnparsedDeclarationCompilesSilently()
    {
        var lockSpelling = LanguageFeature.Compile("msg M { a: Array<f4, 16>; }");
        var plainTypo = LanguageFeature.Compile("msg M { a: i4 }");

        Assert.Multiple(() =>
        {
            Assert.That(lockSpelling.HasParseErrors, Is.True);
            Assert.That(lockSpelling.Diagnostics, Is.Empty);
            Assert.That(lockSpelling.Success, Is.True);
            Assert.That(lockSpelling.DefinitionNames, Is.Empty);

            // The same shape for a mistake that has nothing to do with the new features.
            Assert.That(plainTypo.HasParseErrors, Is.True);
            Assert.That(plainTypo.Diagnostics, Is.Empty);
            Assert.That(plainTypo.Success, Is.True);
        });
    }

    /// <summary>
    /// QUESTIONABLE, pinned. A mixin written as an <c>enum</c> base draws ION0066 and then ION0004 on
    /// top of it. ION0066 is documented as replacing ION0009 rather than stacking; it does not do the
    /// same for the base-type rule, so this one written token yields two errors.
    /// </summary>
    [Test]
    public void Questionable_AMixinAsAnEnumBase_ReportsTwice()
    {
        var compiled = LanguageFeature.Compile("mixin A { x: i4; }\nenum E : A { X }");

        Assert.That(compiled.ErrorCodes,
            Is.EquivalentTo(new[] { LanguageFeature.MixinInTypePosition, "ION0003" }), compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // PATHOLOGICAL INPUT
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deep generic nesting fails fast with an ordinary parse error rather than overflowing the
    /// stack. <c>[Timeout]</c> is inert on this target framework, so the budget is enforced on a pool
    /// thread — see <see cref="ParseBudget"/>.
    /// </summary>
    [Test]
    public void Pathological_DeepGenericNesting_FailsFast()
    {
        var source = "msg M { a: " + string.Concat(Enumerable.Repeat("Array<", 50_000)) + "i4" +
                     string.Concat(Enumerable.Repeat(">", 50_000)) + "; }";

        var file = ParseBudget.Within(() => IonParser.Parse("deep", source));

        Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Not.Empty);
    }

    /// <summary>An unterminated run of <c>&lt;</c> is the cheaper version of the same hazard.</summary>
    [Test]
    public void Pathological_UnterminatedGenericOpeners_FailFast()
    {
        var source = "msg M { a: " + string.Concat(Enumerable.Repeat("Array<", 100_000)) + "; }";

        var file = ParseBudget.Within(() => IonParser.Parse("deep", source));

        Assert.That(file.messageSyntaxes, Is.Empty);
    }

    /// <summary>Legal nesting just under the budget still compiles, so the guard is not overreaching.</summary>
    [Test]
    public void Pathological_NestingUnderTheBudget_StillCompiles()
    {
        const int depth = 30;

        var source = "msg M { a: " + string.Concat(Enumerable.Repeat("Array<", depth)) + "i4" +
                     string.Concat(Enumerable.Repeat(">", depth)) + "; }";

        var compiled = ParseBudget.Within(() => LanguageFeature.Compile(source));

        Assert.That(compiled.HasParseErrors, Is.False);
        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
    }

    /// <summary>
    /// A long mixin chain is a recursive splice in one stage and a recursive cycle walk in another;
    /// neither may hang or overflow.
    /// </summary>
    [Test]
    public void Pathological_ALongMixinChain_Terminates()
    {
        const int depth = 1000;

        var source = string.Join("\n", Enumerable.Range(0, depth).Select(i =>
                         i == 0 ? "mixin M0 { f0: i4; }" : $"mixin M{i} with M{i - 1} {{ f{i}: i4; }}"))
                     + $"\nmsg Big with M{depth - 1} {{ own: i4; }}";

        var compiled = ParseBudget.Within(() => LanguageFeature.Compile(source));

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        Assert.That(compiled.FieldNames("Big"), Has.Count.EqualTo(depth + 1));
    }

    /// <summary>A mixin clause listing hundreds of names resolves without quadratic blowup.</summary>
    [Test]
    public void Pathological_AWideWithClause_Terminates()
    {
        const int width = 300;

        var source = string.Join("\n", Enumerable.Range(0, width).Select(i => $"mixin W{i} {{ f{i}: i4; }}"))
                     + "\nmsg Big with " + string.Join(", ", Enumerable.Range(0, width).Select(i => $"W{i}"))
                     + " { own: i4; }";

        var compiled = ParseBudget.Within(() => LanguageFeature.Compile(source));

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        Assert.That(compiled.FieldNames("Big"), Has.Count.EqualTo(width + 1));
    }
}
