namespace ion.syntax.test;

using ion.compiler;
using ion.runtime;

/// <summary>
/// Coverage for declarations that collide with a name the compiler already owns —
/// <c>ion.compiler.DuplicateSymbolValidationStage</c>.
/// <para>
/// Two holes. <c>CompilationContext.ResolveTypeFor</c> calls <c>ResolveBuiltinType</c> before it
/// looks at anything the project declared, so <c>typedef u4 = i8;</c> and <c>msg u4 { q: i8; }</c>
/// both reported "Check passed" while producing a declaration no reference could ever reach
/// (ION0031). And the duplicate map did not register every declaration form, so two declarations
/// could share a name and <c>ResolveTypeFor</c> would silently pick whichever was registered first
/// (ION0002).
/// </para>
/// </summary>
public class BuiltinShadowingTests
{
    // ═══════════════════════════════════════════════════════════════════
    // HARNESS
    // ═══════════════════════════════════════════════════════════════════

    private const string ShadowCode = "ION0031";
    private const string DuplicateCode = "ION0002";

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
    }

    private static Compiled Compile(string source, params string[] features)
    {
        var files = new[] { IonParser.Parse("shadowtest0", source) };
        var ctx = CompilationContext.Create(features.Length == 0 ? ["std"] : features, files);
        var success = new CompilationPipeline(ctx).Execute();
        return new Compiled(ctx, success);
    }

    // ── Declaration forms ──────────────────────────────────────────────

    /// <summary>
    /// The six declaration forms ION0031 applies to, as (kind, source template) pairs. The template
    /// takes the declared name. Every form is exercised against every builtin below, because the
    /// check lives in one place and a form left out of the switch is exactly how this bug happened.
    /// </summary>
    private static readonly (string Kind, string Keyword, Func<string, string> Source)[] Forms =
    [
        ("Message", "msg", n => $"msg {n} {{ q: i8; }}"),
        ("Enum", "enum", n => $"enum {n} {{ A, B }}"),
        ("Flags", "flags", n => $"flags {n} : u4 {{ A = 1, B = 2 }}"),
        ("Union", "union", n => $"union {n} {{ A(x: i4), B(x: i4) }}"),
        ("Typedef", "typedef", n => $"typedef {n} = i8;"),
        ("Attribute", "attribute", n => $"attribute @{n}(v: i4);")
    ];

    /// <summary>
    /// Every builtin type name the <c>std</c> module declares, read from
    /// <c>IonModule.GetStdModule</c> itself rather than copied. If a builtin is added, these cases
    /// extend automatically instead of quietly under-testing.
    /// </summary>
    private static IEnumerable<string> StdBuiltinNames => IonModule.GetStdModule.Value.Definitions
        .Where(d => d.IsBuiltin)
        .Select(d => d.name.Identifier);

    private static IEnumerable<TestCaseData> ShadowCases() =>
        from name in StdBuiltinNames
        from form in Forms
        select new TestCaseData(form.Source(name), form.Kind, name)
            .SetName($"Shadow_{form.Keyword}_{name}");

    // ═══════════════════════════════════════════════════════════════════
    // GUARD — the derived list is real
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="ShadowCases"/> is data-driven off the std module, so an empty or truncated
    /// derivation would silently reduce the suite to nothing. Pin the contents: every scalar, every
    /// non-scalar builtin, <c>void</c>, and the three builtin generics.
    /// </summary>
    [Test]
    public void StdBuiltins_AreTheExpectedSet()
        => Assert.That(StdBuiltinNames, Is.EquivalentTo(new[]
        {
            "void", "bool",
            "i1", "i2", "i4", "i8", "i16",
            "u1", "u2", "u4", "u8", "u16",
            "f2", "f4", "f8",
            "bigint", "guid", "string", "datetime", "dateonly", "timeonly", "uri", "duration", "bytes",
            "decimal",
            "Maybe", "Array", "Partial", "Map", "Set"
        }));

    // ═══════════════════════════════════════════════════════════════════
    // SHADOWING — every builtin × every declaration form
    // ═══════════════════════════════════════════════════════════════════

    [TestCaseSource(nameof(ShadowCases))]
    public void Declaration_ShadowingABuiltin_IsReported(string source, string kind, string name)
    {
        var compiled = Compile(source);

        Assert.That(compiled.WithCode(ShadowCode), Has.Count.EqualTo(1), compiled.Describe);

        var diagnostic = compiled.WithCode(ShadowCode)[0];

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Severity, Is.EqualTo(IonDiagnosticSeverity.Error));
            Assert.That(compiled.Success, Is.False);
            // Names the offending declaration and its kind...
            Assert.That(diagnostic.Message, Does.StartWith($"{kind} '{name}'"));
            // ...says where the name is already taken...
            Assert.That(diagnostic.Message, Does.Contain("builtin type"));
            Assert.That(diagnostic.Message, Does.Contain("from module 'std'"));
            // ...and states the fix.
            Assert.That(diagnostic.Message, Does.Contain("rename it"));
        });
    }

    /// <summary>
    /// The three builtin generics, called out separately because they are <c>IonGenericType</c>s
    /// rather than plain <c>IonType</c>s and would be missed by a predicate that pattern-matched on
    /// the concrete type instead of the <c>builtin</c> attribute.
    /// </summary>
    [TestCase("Maybe")]
    [TestCase("Array")]
    [TestCase("Partial")]
    public void Declaration_ShadowingABuiltinGeneric_IsReported(string name)
        => Assert.That(Compile($"msg {name} {{ q: i8; }}").ErrorCodes,
            Is.EqualTo(new[] { ShadowCode }));

    /// <summary>The exact CLI reproductions from the bug report, which both printed "Check passed".</summary>
    [TestCase("typedef u4 = i8;")]
    [TestCase("msg u4 { q: i8; }")]
    public void Declaration_TheReportedRepros_AreNowRejected(string source)
    {
        var compiled = Compile(source);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Success, Is.False, compiled.Describe);
            Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { ShadowCode }), compiled.Describe);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // SHADOWING — feature-gated builtins
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The builtin list is derived from <c>CompilationContext.GlobalModules</c>, not from a
    /// hardcoded copy of std, so a name is only taken when the feature that declares it is in
    /// scope. That mattered most for the nine <c>vec2f</c>…<c>vec4h</c> types the <c>vector</c>
    /// feature used to contribute; those were deleted as unimplemented — they resolved in the
    /// compiler and the editor but no generator mapped them and no runtime defined them — so
    /// <c>orleans</c> is now the only feature contributing builtin surface, and it contributes
    /// attributes rather than types.
    /// </summary>
    [TestCase("@grainId", TestName = "Orleans_GrainId")]
    [TestCase("@oneWay", TestName = "Orleans_OneWay")]
    public void Declaration_ShadowingAFeatureGatedBuiltin_TracksTheFeature(string attribute)
    {
        var name = attribute[1..];
        var source = $"attribute @{name}(v: i4);";

        var withFeature = Compile(source, "std", "orleans");
        var withoutFeature = Compile(source, "std");

        Assert.Multiple(() =>
        {
            Assert.That(withFeature.WithCode(ShadowCode), Has.Count.EqualTo(1), withFeature.Describe);
            Assert.That(withFeature.WithCode(ShadowCode)[0].Message,
                Does.Contain("from module 'orleans'"));

            Assert.That(withoutFeature.WithCode(ShadowCode), Is.Empty, withoutFeature.Describe);
            Assert.That(withoutFeature.Errors, Is.Empty, withoutFeature.Describe);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // ACCEPTED
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>A name that collides with nothing is fine, in every declaration form.</summary>
    [TestCaseSource(nameof(NonCollidingCases))]
    public void Declaration_WithANonCollidingName_IsAccepted(string source, string kind)
    {
        var compiled = Compile(source);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(ShadowCode), Is.Empty, $"{kind}: {compiled.Describe()}");
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Success, Is.True, compiled.Describe);
        });
    }

    private static IEnumerable<TestCaseData> NonCollidingCases() =>
        from form in Forms
        select new TestCaseData(form.Source("Widget"), form.Kind).SetName($"Ok_{form.Keyword}");

    /// <summary>
    /// Case matters. <c>ResolveBuiltinType</c> compares with <c>string.Equals</c>, so <c>U4</c> is
    /// a perfectly reachable declaration and must not be reported as an unreachable one — even
    /// though the ION0002 duplicate map next to it is case-insensitive.
    /// </summary>
    [TestCase("U4")]
    [TestCase("I4")]
    [TestCase("String")]
    [TestCase("MAYBE")]
    public void Declaration_DifferingOnlyInCase_IsAccepted(string name)
    {
        var compiled = Compile($"msg {name} {{ q: i8; }}");

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        Assert.That(compiled.Success, Is.True, compiled.Describe);
    }

    /// <summary>
    /// A <c>service</c> is exempt: <c>TransformStage</c> files services under
    /// <c>IonModule.Services</c> and never <c>IonModule.Definitions</c>, so <c>service u4()</c>
    /// shadows nothing — every <c>u4</c> reference still resolves to the builtin, and the service is
    /// still reachable by its own name. Pinned so the exemption stays a decision.
    /// </summary>
    [Test]
    public void Service_NamedAfterABuiltin_IsNotAShadow()
    {
        var compiled = Compile("service u4() { Do(): void; }");

        Assert.That(compiled.WithCode(ShadowCode), Is.Empty, compiled.Describe);
        Assert.That(compiled.Success, Is.True, compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SOURCE POSITION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>ION0031 squiggles the offending name, not the whole declaration.</summary>
    [Test]
    public void Position_PointsAtTheDeclaredName()
    {
        //             1234567890
        // line 2:     msg u4 {
        var compiled = Compile("""
                               msg Fine { a: i4; }
                               msg u4 {
                                   q: i8;
                               }
                               """);

        Assert.That(compiled.WithCode(ShadowCode), Has.Count.EqualTo(1), compiled.Describe);

        var diagnostic = compiled.WithCode(ShadowCode)[0];

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.StartPosition.Line, Is.EqualTo(2));
            Assert.That(diagnostic.StartPosition.Col, Is.EqualTo(5));
            Assert.That(diagnostic.EndPosition, Is.Not.Null);
            Assert.That(diagnostic.EndPosition!.Value.Col, Is.EqualTo(7));
        });
    }

    /// <summary>A typedef reports against its name side, not its underlying type.</summary>
    [Test]
    public void Position_TypedefPointsAtTheAliasName()
    {
        var compiled = Compile("typedef u4 = i8;");

        Assert.That(compiled.WithCode(ShadowCode), Has.Count.EqualTo(1), compiled.Describe);
        Assert.That(compiled.WithCode(ShadowCode)[0].StartPosition.Col, Is.EqualTo(9));
    }

    /// <summary>
    /// Every colliding declaration in a file is reported, not just the first.
    /// <para>
    /// Asserted as a set: the stage walks <c>IonFileSyntax.Definitions</c>, which concatenates by
    /// declaration <em>kind</em> (attributes, flags, enums, messages, services, unions, typedefs)
    /// rather than by source position, so the enum below is visited before the message above it.
    /// That grouping also fixes the order definitions land in the schema lock, so it is not
    /// something to "fix" for prettier diagnostics.
    /// </para>
    /// </summary>
    [Test]
    public void EveryCollidingDeclaration_IsReported()
    {
        var compiled = Compile("""
                               msg u4 { q: i8; }
                               enum i2 { A }
                               typedef guid = i8;
                               """);

        Assert.That(compiled.WithCode(ShadowCode).Select(d => d.StartPosition.Line),
            Is.EquivalentTo(new[] { 1, 2, 3 }), compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DUPLICATES ACROSS DECLARATION KINDS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every declaration form shares one type namespace, so a name may be claimed once. These pairs
    /// were all silently accepted while the duplicate map was missing a form: <c>ResolveTypeFor</c>
    /// then returned whichever definition happened to be registered first.
    /// </summary>
    [TestCase("enum Foo { A }\nunion Foo { A(x: i4) }", TestName = "Duplicate_enum_vs_union")]
    [TestCase("flags Foo : u4 { A = 1 }\nmsg Foo { q: i8; }", TestName = "Duplicate_flags_vs_msg")]
    [TestCase("union Foo { A(x: i4) }\ntypedef Foo = i8;", TestName = "Duplicate_union_vs_typedef")]
    [TestCase("attribute @Foo(v: i4);\nmsg Foo { q: i8; }", TestName = "Duplicate_attribute_vs_msg")]
    [TestCase("typedef Foo = i8;\nenum Foo { A }", TestName = "Duplicate_typedef_vs_enum")]
    [TestCase("msg Foo { q: i8; }\nservice Foo() { Do(): void; }", TestName = "Duplicate_msg_vs_service")]
    [TestCase("enum Foo { A }\nflags Foo : u4 { B = 1 }", TestName = "Duplicate_enum_vs_flags")]
    [TestCase("union Foo { A(x: i4) }\nattribute @Foo(v: i4);", TestName = "Duplicate_union_vs_attribute")]
    public void CollidingDeclarations_AreReported(string source)
    {
        var compiled = Compile(source);

        Assert.That(compiled.WithCode(DuplicateCode), Has.Count.EqualTo(1), compiled.Describe);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Success, Is.False);
            Assert.That(compiled.WithCode(DuplicateCode)[0].Message, Does.Contain("'Foo'"));
        });
    }

    /// <summary>Distinct names across those same forms stay clean.</summary>
    [Test]
    public void DistinctDeclarations_AcrossEveryForm_AreAccepted()
    {
        var compiled = Compile("""
                               attribute @Marker(v: i4);
                               msg Alpha { q: i8; }
                               enum Beta { A, B }
                               flags Gamma : u4 { A = 1, B = 2 }
                               union Delta { One(x: i4), Two(x: i4) }
                               typedef Epsilon = i8;
                               service Zeta() { Do(): void; }
                               """);

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        Assert.That(compiled.Success, Is.True, compiled.Describe);
    }

    /// <summary>
    /// A declaration can be both unreachable and duplicated, and the two facts are independent:
    /// ION0031 is about the builtin, ION0002 about the sibling.
    /// </summary>
    [Test]
    public void ShadowAndDuplicate_AreBothReported()
    {
        var compiled = Compile("msg u4 { q: i8; }\nenum u4 { A }");

        Assert.That(compiled.ErrorCodes,
            Is.EqualTo(new[] { ShadowCode, ShadowCode, DuplicateCode }), compiled.Describe);
    }
}
