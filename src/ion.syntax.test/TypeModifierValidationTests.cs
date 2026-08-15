namespace ion.syntax.test;

using ion.compiler;
using ion.runtime;

/// <summary>
/// Coverage for the <em>spelling</em> of the type modifier suffixes <c>~</c>, <c>[]</c> and <c>?</c>
/// — <c>ion.compiler.TypeModifierValidationStage</c>.
/// <para>
/// Two holes, both silent before this stage existed. <c>CompilationContext.WrapModifiers</c> reads
/// three <see cref="bool"/>s produced by <c>modifiers.Contains(..)</c>, so (a) a repeated suffix was
/// swallowed — <c>Data~~</c>, <c>Data??</c> and <c>Data[][]</c> all collapsed to the single-modifier
/// form, handing the author a type they did not write — and (b) written order was discarded, so
/// <c>Data?~</c> and <c>Data~?</c> were synonyms even though only one of the two spellings describes
/// what the compiler builds.
/// </para>
/// <para>
/// Canonical order is <c>~</c>, then <c>[]</c>, then <c>?</c>: left to right in source is
/// inside-out in the IR, which is exactly the order <c>WrapModifiers</c> applies its wrappers in.
/// </para>
/// </summary>
public class TypeModifierValidationTests
{
    // ═══════════════════════════════════════════════════════════════════
    // HARNESS
    // ═══════════════════════════════════════════════════════════════════

    private const string OutOfOrderCode = "ION0010";
    private const string DuplicateCode = "ION0019";

    /// <summary>A real <c>msg</c> to hang modifiers off, so ION0018 never joins in.</summary>
    private const string Target = "msg Data { a: i4; b: string; }\n";

    private sealed record Compiled(CompilationContext Context, bool Success)
    {
        public IReadOnlyList<IonDiagnostic> Diagnostics => Context.Diagnostics;

        public IReadOnlyList<IonDiagnostic> Errors => Diagnostics
            .Where(d => d.Severity == IonDiagnosticSeverity.Error)
            .ToList();

        public IReadOnlyList<string> ErrorCodes => Errors.Select(d => d.Code).ToList();

        public IReadOnlyList<IonDiagnostic> WithCode(string code) => Diagnostics
            .Where(d => d.Code == code)
            .ToList();

        public string Describe() => Diagnostics.Count == 0
            ? "(no diagnostics)"
            : string.Join("; ", Diagnostics.Select(d => $"{d.Code}@{d.StartPosition}: {d.Message}"));

        // RestoreUnresolvedTypeStage re-adds every module to ProcessedModules, so each definition
        // appears twice. See PartialValidationTests / TypedefTests, which do the same.
        public IonType FieldType(string typeName, string fieldName) =>
            Context.ProcessedModules
                .SelectMany(m => m.Definitions)
                .DistinctBy(d => d.name.Identifier)
                .FirstOrDefault(d => d.name.Identifier == typeName)
                ?.fields.FirstOrDefault(f => f.name.Identifier == fieldName)?.type
            ?? throw new AssertionException($"no field '{fieldName}' on '{typeName}'");
    }

    private static Compiled Compile(string source)
    {
        var files = new[] { IonParser.Parse("modtest0", source) };
        var ctx = CompilationContext.Create(["std"], files);
        var success = new CompilationPipeline(ctx).Execute();
        return new Compiled(ctx, success);
    }

    /// <summary>A single message field of the given written type.</summary>
    private static Compiled CompileField(string type) => Compile($"{Target}msg M {{ p: {type}; }}\n");

    /// <summary>The nesting of a lowered type, outermost first.</summary>
    private static void AssertShape(IonType type, params string[] expectedNesting)
    {
        var names = new List<string>();
        var current = type;

        while (true)
        {
            names.Add(current.name.Identifier);
            if (current is IonGenericType { TypeArguments.Count: > 0 } g)
                current = g.TypeArguments[0];
            else
                break;
        }

        Assert.That(names, Is.EqualTo(expectedNesting),
            () => $"expected {string.Join("<", expectedNesting)}, got {string.Join("<", names)}");
    }

    // ═══════════════════════════════════════════════════════════════════
    // DUPLICATES — every repeated form
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The headline bug. Each of these used to compile clean as the single-modifier form.
    /// <c>{0}</c> written, <c>{1}</c> repeated token, <c>{2}</c> the type actually produced,
    /// <c>{3}</c> the spelling that produces it.
    /// </summary>
    [TestCase("Data~~", "~", "Partial<Data>", "Data~")]
    [TestCase("Data??", "?", "Maybe<Data>", "Data?")]
    [TestCase("Data[][]", "[]", "Array<Data>", "Data[]")]
    [TestCase("Data~~~", "~", "Partial<Data>", "Data~")]
    [TestCase("Data~~?", "~", "Maybe<Partial<Data>>", "Data~?")]
    [TestCase("Data~??", "?", "Maybe<Partial<Data>>", "Data~?")]
    [TestCase("Data~[][]", "[]", "Array<Partial<Data>>", "Data~[]")]
    [TestCase("Data~~[]?", "~", "Maybe<Array<Partial<Data>>>", "Data~[]?")]
    public void Duplicate_IsReported(string written, string token, string produced, string canonical)
    {
        var compiled = CompileField(written);

        Assert.That(compiled.WithCode(DuplicateCode), Has.Count.EqualTo(1), compiled.Describe);

        var diagnostic = compiled.WithCode(DuplicateCode)[0];

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Severity, Is.EqualTo(IonDiagnosticSeverity.Error));
            Assert.That(compiled.Success, Is.False);
            // Names the offending type exactly as written...
            Assert.That(diagnostic.Message, Does.Contain($"'{written}'"));
            // ...says the repeated form is unrepresentable...
            Assert.That(diagnostic.Message, Does.Contain("cannot represent"));
            Assert.That(diagnostic.Message, Does.Contain($"repeats the '{token}' modifier"));
            // ...names the type it actually produced...
            Assert.That(diagnostic.Message, Does.Contain($"'{produced}'"));
            // ...and gives the correct spelling.
            Assert.That(diagnostic.Message, Does.Contain($"'{canonical}'"));
        });
    }

    /// <summary>A repeat is the only complaint: the fix is stated once, not twice.</summary>
    [TestCase("Data~~")]
    [TestCase("Data??")]
    [TestCase("Data[][]")]
    [TestCase("Data~~?")]
    public void Duplicate_IsTheOnlyDiagnostic(string written)
    {
        var compiled = CompileField(written);
        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { DuplicateCode }), compiled.Describe);
    }

    /// <summary>
    /// Two different repeats are two different mistakes, each reported on its own, in canonical
    /// order. The ordering check stays quiet: the canonical spelling offered by each ION0019 is
    /// already de-duplicated <em>and</em> reordered, so ION0010 would only restate the same fix.
    /// </summary>
    [Test]
    public void Duplicate_EachRepeatedTokenIsReportedOnce()
    {
        var compiled = CompileField("Data??~~");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { DuplicateCode, DuplicateCode }),
            compiled.Describe);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(DuplicateCode)[0].Message, Does.Contain("repeats the '~' modifier"));
            Assert.That(compiled.WithCode(DuplicateCode)[1].Message, Does.Contain("repeats the '?' modifier"));
            // Both point at the one canonical spelling.
            Assert.That(compiled.WithCode(DuplicateCode).Select(d => d.Message),
                Has.All.Contains("'Data~?'"));
        });
    }

    /// <summary>The collapse is real — the diagnostic describes a type the author genuinely got.</summary>
    [TestCase("Data~~", "Partial", "Data")]
    [TestCase("Data??", "Maybe", "Data")]
    [TestCase("Data[][]", "Array", "Data")]
    [TestCase("Data~~?", "Maybe", "Partial", "Data")]
    public void Duplicate_StillLowersToTheCollapsedForm(string written, params string[] shape)
        => AssertShape(CompileField(written).FieldType("M", "p"), shape);

    // ═══════════════════════════════════════════════════════════════════
    // ORDER — every permutation
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every non-canonical permutation of the three modifiers: the two- and three-suffix cases that
    /// are not <c>~</c> → <c>[]</c> → <c>?</c>.
    /// </summary>
    [TestCase("Data[]~", "Data~[]", "Array<Partial<Data>>")]
    [TestCase("Data?~", "Data~?", "Maybe<Partial<Data>>")]
    [TestCase("Data?[]", "Data[]?", "Maybe<Array<Data>>")]
    [TestCase("Data~?[]", "Data~[]?", "Maybe<Array<Partial<Data>>>")]
    [TestCase("Data[]~?", "Data~[]?", "Maybe<Array<Partial<Data>>>")]
    [TestCase("Data[]?~", "Data~[]?", "Maybe<Array<Partial<Data>>>")]
    [TestCase("Data?~[]", "Data~[]?", "Maybe<Array<Partial<Data>>>")]
    [TestCase("Data?[]~", "Data~[]?", "Maybe<Array<Partial<Data>>>")]
    public void OutOfOrder_IsReported(string written, string canonical, string produced)
    {
        var compiled = CompileField(written);

        Assert.That(compiled.WithCode(OutOfOrderCode), Has.Count.EqualTo(1), compiled.Describe);

        var diagnostic = compiled.WithCode(OutOfOrderCode)[0];

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Severity, Is.EqualTo(IonDiagnosticSeverity.Error));
            Assert.That(compiled.Success, Is.False);
            // It is the only complaint.
            Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { OutOfOrderCode }), compiled.Describe);
            // Names the offending type as written...
            Assert.That(diagnostic.Message, Does.Contain($"'{written}'"));
            // ...names what it silently produced...
            Assert.That(diagnostic.Message, Does.Contain($"'{produced}'"));
            // ...and shows the canonical spelling.
            Assert.That(diagnostic.Message, Does.Contain($"write '{canonical}'"));
            Assert.That(diagnostic.Message, Does.Contain("'~', then '[]', then '?'"));
        });
    }

    /// <summary>Reordering does not change the lowering; that is the whole reason it is an error.</summary>
    [TestCase("Data[]~", "Array", "Partial", "Data")]
    [TestCase("Data?~", "Maybe", "Partial", "Data")]
    [TestCase("Data?[]~", "Maybe", "Array", "Partial", "Data")]
    public void OutOfOrder_StillLowersToTheCanonicalType(string written, params string[] shape)
        => AssertShape(CompileField(written).FieldType("M", "p"), shape);

    /// <summary>
    /// Every canonically-ordered spelling, including all three singletons and the empty case.
    /// Nothing may be reported for any of them.
    /// </summary>
    [TestCase("Data")]
    [TestCase("Data~")]
    [TestCase("Data[]")]
    [TestCase("Data?")]
    [TestCase("Data~[]")]
    [TestCase("Data~?")]
    [TestCase("Data[]?")]
    [TestCase("Data~[]?")]
    public void CanonicalOrder_IsAccepted(string written)
    {
        var compiled = CompileField(written);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(OutOfOrderCode), Is.Empty, compiled.Describe);
            Assert.That(compiled.WithCode(DuplicateCode), Is.Empty, compiled.Describe);
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Success, Is.True, compiled.Describe);
        });
    }

    /// <summary>
    /// The four documented partial stackings, end to end: accepted, and lowered to the IR the
    /// generators and the golden vectors expect.
    /// </summary>
    [TestCase("Data~", "Partial", "Data")]
    [TestCase("Data~?", "Maybe", "Partial", "Data")]
    [TestCase("Data~[]", "Array", "Partial", "Data")]
    [TestCase("Data~[]?", "Maybe", "Array", "Partial", "Data")]
    public void ValidStacking_ResolvesToTheDocumentedIR(string written, params string[] shape)
    {
        var compiled = CompileField(written);

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        AssertShape(compiled.FieldType("M", "p"), shape);
    }

    /// <summary>The non-partial stackings too, so the rule is not a partial-only rule.</summary>
    [TestCase("i4[]", "Array", "i4")]
    [TestCase("i4?", "Maybe", "i4")]
    [TestCase("i4[]?", "Maybe", "Array", "i4")]
    [TestCase("string?", "Maybe", "string")]
    public void ValidStacking_OverABuiltinIsUnaffected(string written, params string[] shape)
    {
        var compiled = CompileField(written);

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        AssertShape(compiled.FieldType("M", "p"), shape);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SOURCE POSITION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The diagnostic lands on the type name, not on the field or the declaration.</summary>
    [TestCase("Data~~", DuplicateCode)]
    [TestCase("Data?~", OutOfOrderCode)]
    public void Position_PointsAtTheTypeName(string written, string code)
    {
        //             1234567890123
        // line 3:     p: Data~~;
        var compiled = Compile($$"""
                                 msg Data { a: i4; }
                                 msg M {
                                     p: {{written}};
                                 }
                                 """);

        Assert.That(compiled.WithCode(code), Has.Count.EqualTo(1), compiled.Describe);

        var diagnostic = compiled.WithCode(code)[0];

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.StartPosition.Line, Is.EqualTo(3));
            Assert.That(diagnostic.StartPosition.Col, Is.EqualTo(8));
            // The end position covers the identifier, so an editor can squiggle it.
            Assert.That(diagnostic.EndPosition, Is.Not.Null);
            Assert.That(diagnostic.EndPosition!.Value.Col, Is.EqualTo(12));
        });
    }

    /// <summary>Each bad site is reported separately, at its own position.</summary>
    [Test]
    public void Position_EveryOffendingSiteIsReportedOnce()
    {
        var compiled = Compile("""
                               msg Data { a: i4; }
                               msg M {
                                   a: Data~~;
                                   b: Data~;
                                   c: Data?~;
                                   d: i4[][];
                               }
                               """);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.ErrorCodes,
                Is.EqualTo(new[] { DuplicateCode, OutOfOrderCode, DuplicateCode }), compiled.Describe);
            Assert.That(compiled.Errors.Select(d => d.StartPosition.Line),
                Is.EqualTo(new[] { 3, 5, 6 }));
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // EVERY TYPE POSITION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The stage walks <c>IonTypeSites.Of</c>, the traversal it shares with
    /// <c>PartialTypeValidationStage</c>. Every position that walk visits must be diagnosed.
    /// </summary>
    [TestCase("msg Data { a: i4; }\nmsg M { p: Data?~; }")]
    [TestCase("msg Data { a: i4; }\nservice S() { Do(p: Data?~): void; }")]
    [TestCase("msg Data { a: i4; }\nservice S() { Do(): Data?~; }")]
    [TestCase("msg Data { a: i4; }\nunion U(shared: Data?~) { A(x: i4), B(x: i4) }")]
    [TestCase("msg Data { a: i4; }\nunion U { A(x: Data?~), B(x: i4) }")]
    [TestCase("msg Data { a: i4; }\nunion U { Data?~ }")]
    [TestCase("msg Data { a: i4; }\ntypedef Alias = Data?~;\nmsg M { p: Alias; }")]
    [TestCase("enum E : i4?~ { A }\nmsg M { e: E; }")]
    [TestCase("flags F : u4?~ { A = 1 }\nmsg M { f: F; }")]
    [TestCase("attribute @mark(v: i4?~);\nmsg M { a: i4; }")]
    public void EveryTypePosition_IsChecked(string source)
    {
        var compiled = Compile(source);
        Assert.That(compiled.WithCode(OutOfOrderCode), Has.Count.EqualTo(1), compiled.Describe);
    }

    /// <summary>
    /// Service base arguments are prepended to <em>every</em> method by
    /// <c>TransformStage.PrependMethods</c>. Walking the IR would report this once per method; the
    /// declaration is written once, so it must be reported once. This is the property the shared
    /// <c>IonTypeSites</c> traversal exists to preserve, so it is asserted for both of its consumers
    /// (see <c>PartialValidationTests</c> for the ION0018 half).
    /// </summary>
    [Test]
    public void ServiceBaseArgument_IsReportedExactlyOnce()
    {
        var compiled = Compile("""
                               msg Data { a: i4; }
                               service S(owner: Data?~) {
                                   One(): void;
                                   Two(): void;
                                   Three(): void;
                               }
                               """);

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { OutOfOrderCode }), compiled.Describe);
        Assert.That(compiled.Errors[0].StartPosition.Line, Is.EqualTo(2));
    }

    /// <summary>
    /// An <c>enum</c> without an explicit base type gets a synthesized <c>u4</c> node whose
    /// <c>ModifierTokens</c> is null. "No evidence recorded" must read as "nothing written", never
    /// as a crash or a phantom diagnostic.
    /// </summary>
    [Test]
    public void SynthesizedBaseType_IsNotDiagnosed()
    {
        var compiled = Compile("enum E { A, B }\nmsg M { e: E; }");

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        Assert.That(compiled.Success, Is.True, compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // NO CASCADES / INTERACTION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The check is purely syntactic and runs before type resolution, so a misspelled name and a
    /// doubled modifier are independent facts and both are reported on one run.
    /// </summary>
    [Test]
    public void UnknownTypeName_StillGetsTheModifierDiagnostic()
    {
        var compiled = Compile("msg M { p: Nope~~; }");

        Assert.That(compiled.ErrorCodes, Does.Contain(DuplicateCode), compiled.Describe);
        Assert.That(compiled.Diagnostics.Select(d => d.Code), Does.Contain("ION0009"), compiled.Describe);
    }

    /// <summary>
    /// ION0018 is about the <em>target</em> of a patch and ION0019 about how the patch was spelled.
    /// A doubled tilde over a scalar is both, and both are said.
    /// </summary>
    [Test]
    public void DoubleTildeOverAScalar_ReportsBoth()
    {
        var compiled = Compile("msg M { p: i4~~; }");

        Assert.That(compiled.ErrorCodes, Is.EquivalentTo(new[] { DuplicateCode, "ION0018" }),
            compiled.Describe);
    }

    /// <summary>Generic arguments are carried into the message, so the author sees what they wrote.</summary>
    [Test]
    public void GenericTypeReference_IsNamedWithItsArguments()
    {
        var compiled = Compile("msg Data { a: i4; }\nmsg M { p: Array<Data>?~; }");

        Assert.That(compiled.WithCode(OutOfOrderCode), Has.Count.EqualTo(1), compiled.Describe);
        Assert.That(compiled.WithCode(OutOfOrderCode)[0].Message,
            Does.Contain("write 'Array<Data>~?'"));
    }

    /// <summary>
    /// A typedef's <em>name</em> side is a declaration, not a reference: a modifier there is
    /// meaningless rather than misordered, and ION0015 already owns it. The shared traversal must
    /// not drag it in and produce a second, contradictory diagnostic.
    /// </summary>
    [Test]
    public void TypedefNameSide_StaysION0015sTerritory()
    {
        var compiled = Compile("typedef Bad~ = i4;\nmsg M { a: i4; }");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { "ION0015" }), compiled.Describe);
    }

    /// <summary>
    /// Even a <em>repeated</em> modifier on a typedef's name side stays ION0015's: the suffix is
    /// meaningless there whatever its shape, and "you wrote '~' twice" would be advice on how to
    /// better spell something that should not be written at all.
    /// </summary>
    [Test]
    public void TypedefNameSide_RepeatedModifierIsStillOnlyION0015()
    {
        var compiled = Compile("typedef Bad~~ = i4;\nmsg M { a: i4; }");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { "ION0015" }), compiled.Describe);
    }

    /// <summary>
    /// KNOWN GAP, pinned rather than fixed. An <em>inline</em> union case name declares a type; it
    /// is not a reference, so <c>IonTypeSites.Of</c> skips it and no modifier written there is
    /// diagnosed — by anything. Unlike the typedef name side there is no ION0015 equivalent to catch
    /// it, so <c>Ok~~(x: i4)</c> is silently ignored today.
    /// <para>
    /// Out of scope here (this change is about references), but pinned so that a future
    /// "declaration-name modifier" diagnostic is added deliberately, and so that nobody widens the
    /// shared traversal to cover it without noticing that it would also start reporting the typedef
    /// name side twice.
    /// </para>
    /// </summary>
    [Test]
    public void InlineUnionCaseName_IsAKnownGap()
    {
        var compiled = Compile("union U { Ok~~(x: i4), No(y: i4) }");

        Assert.That(compiled.WithCode(DuplicateCode), Is.Empty, compiled.Describe);
        Assert.That(compiled.WithCode(OutOfOrderCode), Is.Empty, compiled.Describe);
    }
}
