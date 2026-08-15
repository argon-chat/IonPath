namespace ion.syntax.test;

using ion.compiler;
using ion.runtime;

/// <summary>
/// End-to-end coverage for the partial modifier <c>~</c> (<c>T~</c>, lowered to
/// <c>Partial&lt;T&gt;</c>).
/// <para>
/// <c>Partial&lt;T&gt;</c> is a sparse patch over <c>T</c> — per field: untouched, modified, or
/// cleared — and is the only Ion type encoded as a CBOR <em>map</em> keyed by field name rather
/// than a positional array (<c>tests/golden/partial.golden.json</c>). That is only meaningful over
/// a type that has a field set, so the modifier is legal over a user-defined <c>msg</c> and over
/// nothing else. Before <c>PartialTypeValidationStage</c> the compiler accepted <c>~</c> on
/// literally anything and emitted an empty map forever.
/// </para>
/// </summary>
public class PartialValidationTests
{
    // ═══════════════════════════════════════════════════════════════════
    // HARNESS
    // ═══════════════════════════════════════════════════════════════════

    private const string PartialCode = "ION0018";

    private sealed record Compiled(CompilationContext Context, bool Success)
    {
        public IReadOnlyList<IonDiagnostic> Diagnostics => Context.Diagnostics;

        public IReadOnlyList<IonDiagnostic> Errors => Diagnostics
            .Where(d => d.Severity == IonDiagnosticSeverity.Error)
            .ToList();

        public IReadOnlyList<string> ErrorCodes => Errors.Select(d => d.Code).ToList();

        /// <summary>Every ION0018, at any severity.</summary>
        public IReadOnlyList<IonDiagnostic> Partials => Diagnostics
            .Where(d => d.Code == PartialCode)
            .ToList();

        public string Describe() => Diagnostics.Count == 0
            ? "(no diagnostics)"
            : string.Join("; ", Diagnostics.Select(d => $"{d.Code}@{d.StartPosition}: {d.Message}"));

        // RestoreUnresolvedTypeStage re-adds every module to ProcessedModules, so each definition
        // appears twice. See TypedefTests, which does the same.
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
    }

    private static Compiled Compile(string source, IonSchemaLock? existingLock = null)
        => CompileMany([source], existingLock);

    private static Compiled CompileMany(IReadOnlyList<string> sources, IonSchemaLock? existingLock = null)
    {
        var files = sources
            .Select((s, i) => IonParser.Parse($"partialtest{i}", s))
            .ToList();

        var ctx = CompilationContext.Create(["std"], files);
        var success = new CompilationPipeline(ctx, null, existingLock).Execute();
        return new Compiled(ctx, success);
    }

    /// <summary>A message whose single field is <paramref name="type"/>, plus a target <c>msg</c>.</summary>
    private static Compiled CompileField(string type) =>
        Compile($"msg Data {{ a: i4; b: string; }}\nmsg M {{ p: {type}; }}\n");

    private static void AssertRejected(Compiled compiled, string writtenName, string reasonFragment)
    {
        Assert.That(compiled.Partials, Has.Count.EqualTo(1), compiled.Describe);

        var diagnostic = compiled.Partials[0];

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Severity, Is.EqualTo(IonDiagnosticSeverity.Error));
            Assert.That(compiled.Success, Is.False);
            // The message names the offending type as it was written...
            Assert.That(diagnostic.Message, Does.Contain($"'{writtenName}'"));
            // ...says why...
            Assert.That(diagnostic.Message, Does.Contain(reasonFragment));
            // ...and says what is allowed.
            Assert.That(diagnostic.Message, Does.Contain("user-defined 'msg'"));
        });
    }

    private static void AssertAccepted(Compiled compiled)
    {
        Assert.Multiple(() =>
        {
            Assert.That(compiled.Partials, Is.Empty, compiled.Describe);
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Success, Is.True, compiled.Describe);
        });
    }

    private static IonSchemaLock LockOf(string source)
    {
        var compiled = Compile(source);
        Assert.That(compiled.Success, Is.True, compiled.Describe);
        return SchemaLockGenerator.Generate("partialtests", compiled.Context.ProcessedModules);
    }

    private static IReadOnlyList<IonDiagnostic> LockDiagnostics(IonSchemaLock existing, string source)
        => Compile(source, existing).Diagnostics
            .Where(d => d.Code.StartsWith("ION002", StringComparison.Ordinal))
            .ToList();

    // ═══════════════════════════════════════════════════════════════════
    // REJECTED — scalars and other builtins
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The headline bug: the old runtime formatter enumerated <c>typeof(int).GetProperties()</c>,
    /// found nothing, and wrote an empty map forever.
    /// </summary>
    [TestCase("i1")]
    [TestCase("i2")]
    [TestCase("i4")]
    [TestCase("i8")]
    [TestCase("i16")]
    [TestCase("u1")]
    [TestCase("u2")]
    [TestCase("u4")]
    [TestCase("u8")]
    [TestCase("u16")]
    [TestCase("f2")]
    [TestCase("f4")]
    [TestCase("f8")]
    [TestCase("bool")]
    [TestCase("duration")]
    public void Rejected_BuiltinScalar(string scalar)
        => AssertRejected(CompileField($"{scalar}~"), scalar, "a builtin scalar type");

    [TestCase("string")]
    [TestCase("guid")]
    [TestCase("datetime")]
    [TestCase("dateonly")]
    [TestCase("timeonly")]
    [TestCase("uri")]
    [TestCase("bytes")]
    [TestCase("bigint")]
    public void Rejected_BuiltinNonScalar(string builtin)
        => AssertRejected(CompileField($"{builtin}~"), builtin, "a builtin type");

    [Test]
    public void Rejected_Void()
        => AssertRejected(CompileField("void~"), "void", "the void type");

    // ═══════════════════════════════════════════════════════════════════
    // REJECTED — declarations that are not a msg
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Rejected_Enum()
    {
        var compiled = Compile("""
                               enum Colour { Red, Green }
                               msg M { e: Colour~; }
                               """);

        AssertRejected(compiled, "Colour", "an enum and has no fields to patch");
    }

    [Test]
    public void Rejected_Flags()
    {
        var compiled = Compile("""
                               flags Perm : u4 { Read = 1, Write = 2 }
                               msg M { f: Perm~; }
                               """);

        AssertRejected(compiled, "Perm", "a flags type and has no fields to patch");
    }

    /// <summary>A union's shape is a discriminated case, not a field set.</summary>
    [Test]
    public void Rejected_Union()
    {
        var compiled = Compile("""
                               union Event { Joined(who: i4), Left(who: i4) }
                               msg M { u: Event~; }
                               """);

        AssertRejected(compiled, "Event", "a union");
    }

    // ═══════════════════════════════════════════════════════════════════
    // REJECTED — through a typedef
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A typedef is erased before validation runs, so this has to be caught as a patch over a
    /// scalar — and reported under the name the author actually wrote.
    /// </summary>
    [Test]
    public void Rejected_TypedefOverAScalar()
    {
        var compiled = Compile("""
                               typedef UserId = u4;
                               msg M { x: UserId~; }
                               """);

        AssertRejected(compiled, "UserId", "an alias for 'u4', which is a builtin scalar type");
    }

    [Test]
    public void Rejected_TypedefChainOverAScalar()
    {
        var compiled = Compile("""
                               typedef A = B;
                               typedef B = string;
                               msg M { x: A~; }
                               """);

        AssertRejected(compiled, "A", "an alias for 'string'");
    }

    [Test]
    public void Rejected_TypedefOverAnEnum()
    {
        var compiled = Compile("""
                               enum Colour { Red, Green }
                               typedef Shade = Colour;
                               msg M { x: Shade~; }
                               """);

        AssertRejected(compiled, "Shade", "an alias for 'Colour', which is an enum");
    }

    /// <summary>An alias of a <c>msg</c> erases to that <c>msg</c>, so the patch is legal.</summary>
    [Test]
    public void Accepted_TypedefOverAMessage()
    {
        var compiled = Compile("""
                               msg Data { a: i4; }
                               typedef Payload = Data;
                               msg M { p: Payload~; }
                               """);

        AssertAccepted(compiled);

        var partial = (IonGenericType)compiled.FieldType("M", "p");

        Assert.Multiple(() =>
        {
            Assert.That(partial.IsPartial, Is.True);
            Assert.That(partial.TypeArguments[0].name.Identifier, Is.EqualTo("Data"));
        });
    }

    /// <summary>
    /// The one route that can actually reach <c>Partial&lt;Partial&lt;T&gt;&gt;</c>: the grammar
    /// collapses a literal <c>Data~~</c>, but an alias of a partial can be patched again.
    /// </summary>
    [Test]
    public void Rejected_PartialOverAnAliasOfAPartial()
    {
        var compiled = Compile("""
                               msg Data { a: i4; }
                               typedef Patch = Data~;
                               msg M { p: Patch~; }
                               """);

        AssertRejected(compiled, "Patch", "an alias for 'Partial<Data>', which is already a partial");
    }

    // ═══════════════════════════════════════════════════════════════════
    // REJECTED — wrapper generics
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Rejected_PartialOverAnExplicitMaybe()
    {
        var compiled = Compile("""
                               msg Data { a: i4; }
                               msg M { p: Maybe<Data>~; }
                               """);

        AssertRejected(compiled, "Maybe<Data>", "an optional type");
        Assert.That(compiled.Partials[0].Message, Does.Contain("'T~?'"));
    }

    [Test]
    public void Rejected_PartialOverAnExplicitArray()
    {
        var compiled = Compile("""
                               msg Data { a: i4; }
                               msg M { p: Array<Data>~; }
                               """);

        AssertRejected(compiled, "Array<Data>", "an array type");
        Assert.That(compiled.Partials[0].Message, Does.Contain("'T~[]'"));
    }

    [Test]
    public void Rejected_PartialOverAnExplicitPartial()
    {
        var compiled = Compile("""
                               msg Data { a: i4; }
                               msg M { p: Partial<Data>~; }
                               """);

        AssertRejected(compiled, "Partial<Data>", "already a partial");
    }

    // ═══════════════════════════════════════════════════════════════════
    // SOURCE POSITION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The diagnostic must land on the offending type, not on the enclosing declaration.</summary>
    [Test]
    public void Position_PointsAtTheTypeName()
    {
        //         1234567890
        // line 3:     bad: i4~;
        var compiled = Compile("""
                               msg Data { a: i4; }
                               msg M {
                                   bad: i4~;
                               }
                               """);

        Assert.That(compiled.Partials, Has.Count.EqualTo(1), compiled.Describe);

        var pos = compiled.Partials[0].StartPosition;

        Assert.Multiple(() =>
        {
            Assert.That(pos.Line, Is.EqualTo(3));
            Assert.That(pos.Col, Is.EqualTo(10));
            // The end position covers the identifier, so an editor can squiggle it.
            Assert.That(compiled.Partials[0].EndPosition, Is.Not.Null);
            Assert.That(compiled.Partials[0].EndPosition!.Value.Col, Is.EqualTo(12));
        });
    }

    /// <summary>Each bad site is reported separately, at its own position.</summary>
    [Test]
    public void Position_EveryOffendingSiteIsReportedOnce()
    {
        var compiled = Compile("""
                               msg M {
                                   a: i4~;
                                   b: string;
                                   c: bool~;
                               }
                               """);

        Assert.That(compiled.Partials, Has.Count.EqualTo(2), compiled.Describe);
        Assert.That(compiled.Partials.Select(d => d.StartPosition.Line), Is.EqualTo(new[] { 2, 4 }));
    }

    // ═══════════════════════════════════════════════════════════════════
    // EVERY TYPE POSITION — rejected
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Position_MessageField()
        => AssertRejected(Compile("msg M { p: i4~; }"), "i4", "a builtin scalar type");

    [Test]
    public void Position_MethodArgument()
        => AssertRejected(Compile("service S() { Do(p: i4~): void; }"), "i4", "a builtin scalar type");

    [Test]
    public void Position_MethodReturnType()
        => AssertRejected(Compile("service S() { Do(): i4~; }"), "i4", "a builtin scalar type");

    /// <summary>
    /// Service base arguments are prepended to <em>every</em> method by
    /// <c>TransformStage.PrependMethods</c>. Walking the IR would report this once per method; the
    /// declaration is written once, so it must be reported once.
    /// </summary>
    [Test]
    public void Position_ServiceBaseArgument_IsReportedExactlyOnce()
    {
        var compiled = Compile("""
                               service S(owner: i4~) {
                                   A(): void;
                                   B(): void;
                                   C(): void;
                               }
                               """);

        AssertRejected(compiled, "i4", "a builtin scalar type");
    }

    [Test]
    public void Position_UnionSharedField()
        => AssertRejected(Compile("union U(shared: i4~) { Joined(who: i4), Left(who: i4) }"),
            "i4", "a builtin scalar type");

    [Test]
    public void Position_UnionCaseField()
        => AssertRejected(Compile("union U { Joined(who: i4~), Left(who: i4) }"),
            "i4", "a builtin scalar type");

    [Test]
    public void Position_UnionReferencedCase()
    {
        var compiled = Compile("""
                               enum Colour { Red, Green }
                               union U { Colour~ }
                               """);

        AssertRejected(compiled, "Colour", "an enum");
    }

    [Test]
    public void Position_TypedefUnderlyingType()
        => AssertRejected(Compile("typedef Bad = i4~;\nmsg M { a: Bad; }\n"), "i4", "a builtin scalar type");

    /// <summary>
    /// <c>enum E : i4~</c> parses (the base type uses the full <c>Type</c> parser) and the modifier
    /// was then dropped on the floor by <c>TransformStage</c>.
    /// </summary>
    [Test]
    public void Position_EnumBaseType()
        => AssertRejected(Compile("enum E : i4~ { A }\nmsg M { e: E; }\n"), "i4", "a builtin scalar type");

    [Test]
    public void Position_FlagsBaseType()
        => AssertRejected(Compile("flags F : u4~ { A = 1 }\nmsg M { f: F; }\n"), "u4", "a builtin scalar type");

    [Test]
    public void Position_AttributeDefinitionArgument()
        => AssertRejected(Compile("attribute @mark(v: i4~);\nmsg M { a: i4; }\n"), "i4", "a builtin scalar type");

    // ═══════════════════════════════════════════════════════════════════
    // EVERY TYPE POSITION — nested in Array / Maybe
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>WrapModifiers</c> applies <c>Partial</c> innermost, so <c>i4~[]</c> is
    /// <c>Array&lt;Partial&lt;i4&gt;&gt;</c> — still a patch over a scalar, and still wrong.
    /// </summary>
    [TestCase("i4~?")]
    [TestCase("i4~[]")]
    [TestCase("i4~[]?")]
    public void Position_NestedInsideAWrapper(string type)
        => AssertRejected(CompileField(type), "i4", "a builtin scalar type");

    // ═══════════════════════════════════════════════════════════════════
    // ACCEPTED — a real msg, in every type position
    // ═══════════════════════════════════════════════════════════════════

    private const string Target = "msg Data { a: i4; b: string; }\n";

    [Test]
    public void Accepted_MessageField()
        => AssertAccepted(Compile(Target + "msg M { p: Data~; }\n"));

    [Test]
    public void Accepted_MethodArgument()
        => AssertAccepted(Compile(Target + "service S() { Do(p: Data~): void; }\n"));

    [Test]
    public void Accepted_MethodReturnType()
        => AssertAccepted(Compile(Target + "service S() { Do(): Data~; }\n"));

    [Test]
    public void Accepted_ServiceBaseArgument()
        => AssertAccepted(Compile(Target + "service S(owner: Data~) { Do(): void; }\n"));

    [Test]
    public void Accepted_UnionSharedField()
        => AssertAccepted(Compile(Target + "union U(shared: Data~) { Joined(who: i4), Left(who: i4) }\n"));

    [Test]
    public void Accepted_UnionCaseField()
        => AssertAccepted(Compile(Target + "union U { Joined(who: Data~), Left(who: i4) }\n"));

    [Test]
    public void Accepted_UnionReferencedCase()
        => AssertAccepted(Compile(Target + "union U { Data~ }\n"));

    [Test]
    public void Accepted_TypedefUnderlyingType()
        => AssertAccepted(Compile(Target + "typedef Patch = Data~;\nmsg M { p: Patch; }\n"));

    /// <summary>All seven positions at once, in one file: nothing may be reported.</summary>
    [Test]
    public void Accepted_AllPositionsTogether()
    {
        var compiled = Compile("""
                               msg Data { a: i4; b: string; }
                               msg M { p: Data~; q: Data~?; r: Data~[]; s: Data~[]?; }
                               union U(shared: Data~) { Joined(who: Data~), Left(who: i4) }
                               service S(owner: Data~) {
                                   Get(p: Data~): Data~;
                                   Put(p: Data~[]): Data~?;
                               }
                               """);

        AssertAccepted(compiled);
    }

    /// <summary>A message may hold a patch over itself; that is a normal recursive schema.</summary>
    [Test]
    public void Accepted_PartialOverTheEnclosingMessage()
        => AssertAccepted(Compile("msg A { id: i4; self: A~; }"));

    /// <summary>
    /// The stage runs after type resolution, so declaration order must not matter — a forward
    /// reference is still an <c>IonUnresolvedType</c> when <c>TransformStage</c> hands it over.
    /// </summary>
    [Test]
    public void Accepted_ForwardReferenceToTheTargetMessage()
        => AssertAccepted(Compile("msg M { p: Data~; }\nmsg Data { a: i4; }\n"));

    [Test]
    public void Accepted_TargetMessageInAnotherFile()
        => AssertAccepted(CompileMany(["msg Data { a: i4; }", "msg M { p: Data~; }"]));

    [Test]
    public void Rejected_TargetInAnotherFileIsStillClassified()
        => AssertRejected(CompileMany(["enum Colour { Red, Green }", "msg M { p: Colour~; }"]),
            "Colour", "an enum");

    /// <summary>
    /// A patch over a fieldless <c>msg</c> is legal but degenerate: the empty map is its only
    /// value. Pinned as accepted — it is a well-formed (if pointless) contract, not an error.
    /// </summary>
    [Test]
    public void Accepted_PartialOverAFieldlessMessage()
        => AssertAccepted(Compile("msg Empty { }\nmsg M { p: Empty~; }\n"));

    // ═══════════════════════════════════════════════════════════════════
    // MODIFIER STACKING — the lowered IR shape
    // ═══════════════════════════════════════════════════════════════════

    private static IonType StackedFieldType(string type)
    {
        var compiled = Compile($"{Target}msg M {{ p: {type}; }}\n");
        Assert.That(compiled.Success, Is.True, compiled.Describe);
        return compiled.FieldType("M", "p");
    }

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

    [Test]
    public void Stacking_Bare_IsPartialOfData()
        => AssertShape(StackedFieldType("Data~"), "Partial", "Data");

    [Test]
    public void Stacking_Optional_IsMaybeOfPartialOfData()
        => AssertShape(StackedFieldType("Data~?"), "Maybe", "Partial", "Data");

    [Test]
    public void Stacking_Array_IsArrayOfPartialOfData()
        => AssertShape(StackedFieldType("Data~[]"), "Array", "Partial", "Data");

    [Test]
    public void Stacking_ArrayOptional_IsMaybeOfArrayOfPartialOfData()
        => AssertShape(StackedFieldType("Data~[]?"), "Maybe", "Array", "Partial", "Data");

    /// <summary>
    /// Modifier order in source does not change the lowering: <c>~</c> is always innermost. That is
    /// precisely why a non-canonical spelling is now an error (ION0010) rather than a synonym — see
    /// <c>TypeModifierValidationTests</c>. The lowering is pinned here so that the error is known to
    /// be about the <em>spelling</em> and nothing about <c>WrapModifiers</c> has quietly moved.
    /// </summary>
    [TestCase("Data?~", "Maybe", "Partial", "Data")]
    [TestCase("Data[]~", "Array", "Partial", "Data")]
    public void Stacking_ModifierOrderInSourceIsIrrelevantToTheLowering(string type, params string[] shape)
    {
        var compiled = Compile($"{Target}msg M {{ p: {type}; }}\n");

        Assert.Multiple(() =>
        {
            // Rejected for how it is written...
            Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { "ION0010" }), compiled.Describe);
            // ...but the type it produced is the canonical one all the same.
            AssertShape(compiled.FieldType("M", "p"), shape);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // `Data~~` — the collapse is real, and is now reported
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>Data~~</c> still lowers to a single <c>Partial&lt;Data&gt;</c> — <c>WrapModifiers</c> reads
    /// a <see cref="bool"/>, so a second tilde cannot change what is built — but it is no longer
    /// silent: <c>TypeModifierValidationStage</c> raises ION0019 off the raw modifier tokens the
    /// parser now keeps. <c>Partial&lt;Partial&lt;T&gt;&gt;</c> remains unrepresentable; the author
    /// is told so instead of being handed the collapsed type.
    /// </summary>
    [Test]
    public void DoubleTilde_StillCollapses_ButIsNowReported()
    {
        var compiled = Compile(Target + "msg M { p: Data~~; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { "ION0019" }), compiled.Describe);
            // ION0018 has nothing to say: Data is a msg, so the *target* of the patch is fine.
            Assert.That(compiled.Partials, Is.Empty, compiled.Describe);
            AssertShape(compiled.FieldType("M", "p"), "Partial", "Data");
        });
    }

    /// <summary>
    /// The evidence the diagnostic runs on. The three bools cannot tell the two apart — that is the
    /// whole reason <c>IonUnderlyingTypeSyntax.ModifierTokens</c> exists — so the raw token list is
    /// what must differ.
    /// </summary>
    [Test]
    public void DoubleTilde_IsVisibleInTheSyntaxTree()
    {
        var once = IonParser.Parse("a", "msg M { p: Data~; }").messageSyntaxes[0].Fields[0].Type;
        var twice = IonParser.Parse("b", "msg M { p: Data~~; }").messageSyntaxes[0].Fields[0].Type;

        Assert.Multiple(() =>
        {
            // The lossy reduction is unchanged: both are "partial".
            Assert.That(once.IsPartial, Is.True);
            Assert.That(twice.IsPartial, Is.True);
            // The evidence is not.
            Assert.That(once.ModifierTokens, Is.EqualTo(new[] { "~" }));
            Assert.That(twice.ModifierTokens, Is.EqualTo(new[] { "~", "~" }));
        });
    }

    /// <summary>A double tilde over a scalar is still caught — the collapse does not hide it.</summary>
    [Test]
    public void Pinned_DoubleTildeOverAScalar_IsStillRejected()
        => AssertRejected(CompileField("i4~~"), "i4", "a builtin scalar type");

    // ═══════════════════════════════════════════════════════════════════
    // PINNED BEHAVIOUR — a msg literally named `Partial`
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>IonType.IsPartial</c> is a pure name check (<c>name.Identifier == "Partial"</c>), so a
    /// user-defined <c>msg Partial</c> would false-positive against it. This stage never consults
    /// it: it keys off the syntax-level <c>~</c> token, so the declaration produces no ION0018.
    /// <para>
    /// It is not clean, though — <c>Partial</c> is a builtin generic, so the declaration is
    /// unreachable and <c>DuplicateSymbolValidationStage</c> now says so with ION0031. That is the
    /// <em>only</em> thing reported: the point of this test is that the unrelated <c>Data~</c> in
    /// the same file is unaffected.
    /// </para>
    /// </summary>
    [Test]
    public void MsgNamedPartial_ProducesNoPartialDiagnostic()
    {
        var compiled = Compile("""
                               msg Partial { a: i4; }
                               msg Data { b: i4; }
                               msg M { p: Data~; }
                               """);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Partials, Is.Empty, compiled.Describe);
            Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { "ION0031" }), compiled.Describe);
            AssertShape(compiled.FieldType("M", "p"), "Partial", "Data");
        });
    }

    /// <summary>
    /// The shadowing itself, pinned here because ION0018's wording depends on it: builtins win
    /// name resolution (<c>CompilationContext.ResolveTypeFor</c> tries <c>ResolveBuiltinType</c>
    /// first), so a bare <c>Partial</c> reference means the builtin wrapper — the user's message is
    /// unreachable. <c>PartialTypeValidationStage.Lookup</c> mirrors that precedence exactly, so
    /// validation and lowering can never disagree about what the name means.
    /// <para>
    /// ION0031 now rejects the declaration outright, which is the actual fix. The resolution
    /// behaviour below is unchanged and stays pinned: ION0031 is a diagnostic, not a rename, so
    /// everything downstream of it still sees the builtin win.
    /// </para>
    /// </summary>
    [Test]
    public void Pinned_MsgNamedPartial_IsShadowedByTheBuiltinWrapper()
    {
        var compiled = Compile("""
                               msg Partial { a: i4; }
                               msg M { p: Partial; }
                               """);

        var fieldType = compiled.FieldType("M", "p");

        Assert.Multiple(() =>
        {
            // The builtin open generic, not the user's msg.
            Assert.That(fieldType, Is.InstanceOf<IonGenericType>());
            Assert.That(fieldType.IsBuiltin, Is.True);
            Assert.That(fieldType.fields, Is.Empty);
            // The user's declaration still exists; it just cannot be referenced...
            Assert.That(compiled.Definition("Partial").fields.Select(f => f.name.Identifier),
                Is.EqualTo(new[] { "a" }));
            // ...which is exactly what ION0031 reports. ION0060 rides along because a bare
            // `Partial` is the builtin open generic written with no type argument, which is
            // precisely what the generic arity rule exists to catch — and it only appears here
            // *because* the builtin won resolution, so it corroborates the shadowing.
            Assert.That(compiled.ErrorCodes, Is.EquivalentTo(new[] { "ION0031", "ION0060" }),
                compiled.Describe);
        });
    }

    /// <summary>...and applying <c>~</c> to that shadowed name reports against the builtin.</summary>
    [Test]
    public void Pinned_MsgNamedPartial_TildeOnItReportsAgainstTheBuiltin()
    {
        var compiled = Compile("""
                               msg Partial { a: i4; }
                               msg M { p: Partial~; }
                               """);

        AssertRejected(compiled, "Partial", "already a partial");
    }

    // ═══════════════════════════════════════════════════════════════════
    // NO CASCADES
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>An unknown name is ION0009's business; ION0018 must not pile on.</summary>
    [Test]
    public void NoCascade_UnknownTypeReportsOnlyION0009()
    {
        var compiled = Compile("msg M { p: Nope~; }");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.ErrorCodes, Does.Contain("ION0009"));
            Assert.That(compiled.Partials, Is.Empty, compiled.Describe);
        });
    }

    /// <summary>A typedef cycle is ION0017's business, and must not hang or double-report here.</summary>
    [Test]
    public void NoCascade_CyclicTypedefUnderATilde()
    {
        Compiled? compiled = null;
        var worker = new Thread(() => compiled = Compile("typedef A = A;\nmsg M { p: A~; }\n"),
            maxStackSize: 4 * 1024 * 1024) { IsBackground = true };

        worker.Start();
        Assert.That(worker.Join(TimeSpan.FromSeconds(20)), Is.True, "compilation did not terminate");

        Assert.Multiple(() =>
        {
            Assert.That(compiled!.ErrorCodes, Does.Contain("ION0017"));
            Assert.That(compiled.Partials, Is.Empty, compiled.Describe);
        });
    }

    /// <summary>A service name is not a type; the unresolved-type diagnostic owns that too.</summary>
    [Test]
    public void NoCascade_ServiceNameUnderATilde()
    {
        var compiled = Compile("""
                               service S() { Do(): void; }
                               msg M { p: S~; }
                               """);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.ErrorCodes, Does.Contain("ION0009"));
            Assert.That(compiled.Partials, Is.Empty, compiled.Describe);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // SCHEMA LOCK
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The lock renders partials through <c>GetCanonicalTypeName</c>, which is fully generic, so no
    /// partial-specific code was needed there. Confirmed rather than assumed.
    /// </summary>
    [Test]
    public void Lock_CanonicalNamesForEveryStacking()
    {
        var snapshot = LockOf(Target + "msg M { p: Data~; q: Data~?; r: Data~[]; s: Data~[]?; }\n");

        var fields = snapshot.Definitions["M"].Fields!;

        Assert.Multiple(() =>
        {
            Assert.That(fields[0].Type, Is.EqualTo("Partial<Data>"));
            Assert.That(fields[1].Type, Is.EqualTo("Maybe<Partial<Data>>"));
            Assert.That(fields[2].Type, Is.EqualTo("Array<Partial<Data>>"));
            Assert.That(fields[3].Type, Is.EqualTo("Maybe<Array<Partial<Data>>>"));
        });
    }

    [Test]
    public void Lock_CanonicalNamesOnServiceSignatures()
    {
        var snapshot = LockOf(Target + "service S(owner: Data~) { Get(p: Data~[]): Data~?; }\n");

        var method = snapshot.Definitions["S"].Methods!["Get"];

        Assert.Multiple(() =>
        {
            Assert.That(method.Args[0].Type, Is.EqualTo("Partial<Data>"), "service base argument");
            Assert.That(method.Args[1].Type, Is.EqualTo("Array<Partial<Data>>"));
            Assert.That(method.Returns, Is.EqualTo("Maybe<Partial<Data>>"));
        });
    }

    [Test]
    public void Lock_RecompilingTheSameSchema_ProducesNoDiagnostic()
    {
        const string source = Target + "msg M { p: Data~; q: Data~[]; }\n";
        var diagnostics = LockDiagnostics(LockOf(source), source);

        Assert.That(diagnostics, Is.Empty,
            () => string.Join("; ", diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    /// <summary>Dropping the <c>~</c> changes the wire type and must be reported.</summary>
    [Test]
    public void Lock_RemovingTheTilde_ReportsION0022()
    {
        var snapshot = LockOf(Target + "msg M { p: Data~; }\n");
        var diagnostics = LockDiagnostics(snapshot, Target + "msg M { p: Data; }\n");

        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo(new[] { "ION0022" }));
        Assert.That(diagnostics[0].Message,
            Is.EqualTo("Breaking change: field 'p' in 'M' changed type from 'Partial<Data>' to 'Data'."));
    }

    [Test]
    public void Lock_RemovingAPartialField_ReportsION0020()
    {
        var snapshot = LockOf(Target + "msg M { p: Data~; keep: i4; }\n");
        var diagnostics = LockDiagnostics(snapshot, Target + "msg M { keep: i4; }\n");

        Assert.That(diagnostics.Select(d => d.Code), Does.Contain("ION0020"));
        Assert.That(diagnostics.First(d => d.Code == "ION0020").Message, Does.Contain("'p'"));
    }

    /// <summary>Changing the patched message's own fields is invisible at the use site.</summary>
    [Test]
    public void Lock_ChangingThePatchedMessage_IsReportedOnThatMessageOnly()
    {
        var snapshot = LockOf("msg Data { a: i4; }\nmsg M { p: Data~; }\n");
        var diagnostics = LockDiagnostics(snapshot, "msg Data { a: i8; }\nmsg M { p: Data~; }\n");

        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo(new[] { "ION0022" }));
        Assert.That(diagnostics[0].Message, Does.Contain("field 'a' in 'Data'"));
    }

    /// <summary>
    /// PINNED, deliberately unchanged: <c>SchemaLockValidationStage</c> exempts only
    /// <c>IsMaybe</c> from ION0029, so a newly added <c>T~</c> field still warns.
    /// <para>
    /// A patch <em>value</em> is optional-ish — an empty map (<c>a0</c>) is a valid "no changes"
    /// patch — but ION0029 is about the <em>parent</em> message's framing, which is a positional
    /// CBOR array. The new element occupies a slot an older reader does not read, exactly as any
    /// other added field does; the patch's own map encoding cannot help with that. The suggested
    /// fix (<c>p: Data~?</c>) also remains available and correct. Suppressing the warning would be
    /// a silent compatibility claim that no runtime path or golden vector backs.
    /// </para>
    /// </summary>
    [Test]
    public void Lock_AddingAPartialField_StillWarnsION0029()
    {
        var snapshot = LockOf(Target + "msg M { keep: i4; }\n");
        var diagnostics = LockDiagnostics(snapshot, Target + "msg M { keep: i4; p: Data~; }\n");

        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo(new[] { "ION0029" }));

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics[0].Severity, Is.EqualTo(IonDiagnosticSeverity.Warning));
            Assert.That(diagnostics[0].Message, Does.Contain("'p'"));
            Assert.That(diagnostics[0].Message, Does.Contain("Partial<Data>"));
        });
    }

    /// <summary>The documented escape hatch: <c>Data~?</c> is a Maybe, so it is exempt.</summary>
    [Test]
    public void Lock_AddingAnOptionalPartialField_DoesNotWarn()
    {
        var snapshot = LockOf(Target + "msg M { keep: i4; }\n");
        var diagnostics = LockDiagnostics(snapshot, Target + "msg M { keep: i4; p: Data~?; }\n");

        Assert.That(diagnostics, Is.Empty,
            () => string.Join("; ", diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    [Test]
    public void Lock_VersionIsUnchanged()
        => Assert.That(IonSchemaLock.CurrentVersion, Is.EqualTo(1),
            "partial validation must not change the lock format");

    // ═══════════════════════════════════════════════════════════════════
    // CIRCULAR REFERENCES — pinned findings
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A patch is cycle-breaking: an empty map terminates, so a field of type <c>A~</c> inside
    /// <c>A</c> is finite on the wire. A <em>bare</em> self-reference is not, and is now ION0030 —
    /// it used to be invisible because <c>CollectDirectReferences</c> skipped
    /// <c>inner.name.Identifier == ownerName</c> outright, so a genuinely infinite type compiled.
    /// </summary>
    [Test]
    public void Cycle_DirectSelfReference_IsBrokenByAPartialButNotByOwnership()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Compile("msg A { id: i4; self: A~; }").ErrorCodes, Does.Not.Contain("ION0030"));
            Assert.That(Compile("msg A { id: i4; self: A; }").ErrorCodes, Does.Contain("ION0030"),
                "a bare owned self-reference can never be finite");
        });
    }

    /// <summary>
    /// Cycle detection follows only unconditionally owned edges, so an indirect loop closed through
    /// a partial — or through any other cycle-breaking wrapper — is legal. This test previously
    /// pinned the opposite, with a note that the <c>Maybe</c> half had to move in step because
    /// <c>UnwrapType</c> stripped both before testing; the rule changed and both halves moved.
    /// </summary>
    [TestCase("B~", TestName = "Cycle_IndirectThroughAPartial")]
    [TestCase("B?", TestName = "Cycle_IndirectThroughAMaybe")]
    [TestCase("B[]", TestName = "Cycle_IndirectThroughAnArray")]
    public void Cycle_IndirectThroughACycleBreakingWrapper_IsLegal(string wrapped)
    {
        Assert.That(Compile($"msg A {{ b: {wrapped}; }}\nmsg B {{ a: A; }}\n").ErrorCodes,
            Does.Not.Contain("ION0030"));
    }

    /// <summary>The counterpart: every hop owned outright is still rejected.</summary>
    [Test]
    public void Cycle_IndirectThroughOwnedFieldsOnly_IsStillReported()
    {
        Assert.That(Compile("msg A { b: B; }\nmsg B { a: A; }\n").ErrorCodes, Does.Contain("ION0030"));
    }
}
