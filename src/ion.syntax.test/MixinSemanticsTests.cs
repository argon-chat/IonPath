namespace ion.syntax.test;

using ion.runtime;

/// <summary>
/// End-to-end coverage for <c>mixin</c> and the <c>with</c> clause: the expansion order the wire
/// depends on, diamond deduplication, and ION0063–ION0066 plus the ION1001 hint.
/// </summary>
/// <remarks>
/// Field <em>order</em> is the whole contract here — Ion messages are positional, so the order
/// <c>MixinExpansionStage</c> produces is the field numbering — and it is asserted both in the IR and
/// in <c>ion.lock.json</c>, because those are two different code paths and only the second one is
/// what a downstream reader sees.
/// </remarks>
public class MixinSemanticsTests
{
    // ═══════════════════════════════════════════════════════════════════
    // EXPANSION ORDER
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The rule, spelled out: mixins in <c>with</c> order, bases first, own fields last.</summary>
    [Test]
    public void Order_MixinFieldsThenOwn()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A { a1: i4; a2: i4; }
                                               mixin B with A { b1: i4; }
                                               mixin C { c1: i4; }
                                               msg M with B, C { m1: i4; }
                                               """);

        compiled.AssertAccepted();
        Assert.That(compiled.FieldNames("M"), Is.EqualTo(new[] { "a1", "a2", "b1", "c1", "m1" }));
    }

    /// <summary>
    /// Three mixins and a two-deep transitive base. Reading the <c>with</c> clause left to right and
    /// each mixin top to bottom has to give the wire order, or an author cannot tell what index their
    /// own field has.
    /// </summary>
    [Test]
    public void Order_ThreeMixinsWithATransitiveBase()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin Base  { b1: i4; b2: i4; }
                                               mixin Mid   with Base { m1: i4; }
                                               mixin Third with Mid { t1: i4; }
                                               mixin Other { o1: i4; }
                                               msg M with Third, Other { own: i4; }
                                               """);

        compiled.AssertAccepted();
        Assert.That(compiled.FieldNames("M"),
            Is.EqualTo(new[] { "b1", "b2", "m1", "t1", "o1", "own" }));
    }

    /// <summary>
    /// The same order, read back out of the lock, with the indices the wire actually uses. This is
    /// the assertion that would catch an expansion computed correctly and then written into the IR
    /// in a different order.
    /// </summary>
    [Test]
    public void Order_IsWhatLandsInTheSchemaLock()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin Base  { b1: i4; b2: i4; }
                                               mixin Mid   with Base { m1: i4; }
                                               mixin Third with Mid { t1: i4; }
                                               mixin Other { o1: i4; }
                                               msg M with Third, Other { own: i4; }
                                               service Api() { Get(): M; }
                                               """);

        compiled.AssertAccepted();

        var locked = compiled.Lock().Definitions["M"];

        Assert.Multiple(() =>
        {
            Assert.That(locked.Fields!.Select(f => $"{f.Index}:{f.Name}"),
                Is.EqualTo(new[] { "0:b1", "1:b2", "2:m1", "3:t1", "4:o1", "5:own" }));
            Assert.That(locked.NextIndex, Is.EqualTo(6));
        });
    }

    /// <summary>
    /// Reordering a mixin's own fields renumbers every including message — which is exactly why the
    /// order rule has to be predictable, and is a breaking change the lock catches.
    /// </summary>
    [Test]
    public void Order_ReorderingAMixinsFields_IsABreakingChange()
    {
        var before = LanguageFeature.Compile("""
                                             mixin A { a1: i4; a2: i4; }
                                             msg M with A { own: i4; }
                                             service Api() { Get(): M; }
                                             """);
        before.AssertAccepted();

        var after = LanguageFeature.Compile("""
                                            mixin A { a2: i4; a1: i4; }
                                            msg M with A { own: i4; }
                                            service Api() { Get(): M; }
                                            """, before.Lock());

        Assert.That(after.WithCode("ION0021").Select(d => d.Message),
            Has.Exactly(2).Items, after.Describe);
    }

    /// <summary>
    /// A mixin field's doc comment and attributes travel with it into every includer — the expansion
    /// reuses the author's own <c>IonFieldSyntax</c> nodes, which is the point.
    /// </summary>
    [Test]
    public void Expansion_CarriesDocsAndAttributes()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin Audited {
                                                   /// When the row was created.
                                                   @internal
                                                   createdAt: datetime;
                                               }
                                               msg M with Audited { own: i4; }
                                               msg N with Audited { other: i4; }
                                               """);

        compiled.AssertAccepted();

        var m = compiled.Definition("M").fields[0];
        var n = compiled.Definition("N").fields[0];

        Assert.Multiple(() =>
        {
            Assert.That(m.Doc, Is.EqualTo("When the row was created."));
            Assert.That(m.attributes.Select(a => a.name.Identifier), Does.Contain("internal"));
            Assert.That(n.Doc, Is.EqualTo("When the row was created."));
            // Distinct IonField instances per message: Doc is mutable and must not be shared.
            Assert.That(m, Is.Not.SameAs(n));
        });
    }

    /// <summary>A message with no <c>with</c> clause is untouched by any of this.</summary>
    [Test]
    public void Expansion_AMessageWithNoClause_KeepsItsOwnFields()
    {
        var compiled = LanguageFeature.Compile("mixin A { a1: i4; }\nmsg M { own: i4; }\nmsg N with A { z: i4; }");

        Assert.That(compiled.FieldNames("M"), Is.EqualTo(new[] { "own" }));
    }

    /// <summary>An empty mixin contributes nothing and is not an error.</summary>
    [Test]
    public void Expansion_AnEmptyMixin_ContributesNothing()
    {
        var compiled = LanguageFeature.Compile("mixin Marker { }\nmsg M with Marker { own: i4; }");

        compiled.AssertAccepted();
        Assert.That(compiled.FieldNames("M"), Is.EqualTo(new[] { "own" }));
    }

    // ═══════════════════════════════════════════════════════════════════
    // DIAMOND DEDUPLICATION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The diamond. <c>Traced</c> already carries <c>Audited</c>, so listing both must splice
    /// <c>Audited</c> once — and the resulting order must not depend on which arm was written first,
    /// or the author would have to compute a linearisation to know their own field numbering.
    /// </summary>
    [TestCase("Audited, Traced", TestName = "Diamond_BaseFirst")]
    [TestCase("Traced, Audited", TestName = "Diamond_DerivedFirst")]
    public void Diamond_ContributesTheBaseOnce(string clause)
    {
        var compiled = LanguageFeature.Compile($$"""
                                                 mixin Audited { createdAt: datetime; createdBy: guid; }
                                                 mixin Traced with Audited { traceId: guid; }
                                                 msg Doc with {{clause}} { title: string; }
                                                 """);

        compiled.AssertAccepted();
        Assert.That(compiled.FieldNames("Doc"),
            Is.EqualTo(new[] { "createdAt", "createdBy", "traceId", "title" }));
    }

    /// <summary>Two arms reaching the same base is still one contribution.</summary>
    [Test]
    public void Diamond_TwoArmsOverOneBase_ContributeItOnce()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin Base { b: i4; }
                                               mixin Left  with Base { l: i4; }
                                               mixin Right with Base { r: i4; }
                                               msg M with Left, Right { own: i4; }
                                               """);

        compiled.AssertAccepted();
        Assert.That(compiled.FieldNames("M"), Is.EqualTo(new[] { "b", "l", "r", "own" }));
    }

    /// <summary>
    /// The line between deduplication and a real collision: the same mixin reaching a message twice
    /// is deduplicated, two <em>different</em> mixins declaring the same field name is not.
    /// </summary>
    [Test]
    public void Diamond_AndCollision_AreDistinguished()
    {
        var deduplicated = LanguageFeature.Compile("""
                                                   mixin Base { stamp: i4; }
                                                   mixin Left  with Base { l: i4; }
                                                   msg M with Base, Left { own: i4; }
                                                   """);

        var collision = LanguageFeature.Compile("""
                                                mixin One { stamp: i4; }
                                                mixin Two { stamp: i8; }
                                                msg M with One, Two { own: i4; }
                                                """);

        Assert.Multiple(() =>
        {
            deduplicated.AssertAccepted();
            Assert.That(deduplicated.FieldNames("M"), Is.EqualTo(new[] { "stamp", "l", "own" }));

            Assert.That(collision.WithCode(LanguageFeature.FieldCollision), Has.Count.EqualTo(1),
                collision.Describe);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // ION0063 — the with clause
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>A name nothing declares.</summary>
    [Test]
    public void WithClause_UnknownName_IsReported()
    {
        var compiled = LanguageFeature.Compile("msg M with Nope { b: i4; }");
        var diagnostic = compiled.Only(LanguageFeature.WithClause);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message,
                Is.EqualTo("'Nope' in the 'with' clause of msg 'M' does not name a mixin. " +
                           "Declare 'mixin Nope { … }', or remove it from the clause."));
            //             12345678901234567890
            //             msg M with Nope { … }
            LanguageFeature.AssertSpan(diagnostic, 1, 12, 16);
        });
    }

    /// <summary>
    /// An unknown mixin name is one mistake. It must not also become an unresolved type reference or
    /// a mixin-in-type-position complaint — the name is in a <c>with</c> clause, not a type position.
    /// </summary>
    [Test]
    public void WithClause_UnknownName_ProducesNoCascade()
    {
        var compiled = LanguageFeature.Compile("msg M with Nope { b: i4; }");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.WithClause }), compiled.Describe);
    }

    /// <summary>The name resolves, just not to a mixin — a different mistake with a different fix.</summary>
    [TestCase("msg D { a: i4; }", "D", "a msg", TestName = "WithClause_NamesAMsg")]
    [TestCase("enum D { A }", "D", "an enum", TestName = "WithClause_NamesAnEnum")]
    [TestCase("flags D : u4 { A = 1 }", "D", "a flags declaration", TestName = "WithClause_NamesFlags")]
    [TestCase("union D { Ok(a: i4) }", "D", "a union", TestName = "WithClause_NamesAUnion")]
    [TestCase("typedef D = i4;", "D", "a typedef", TestName = "WithClause_NamesATypedef")]
    [TestCase("service D() { Go(): i4; }", "D", "a service", TestName = "WithClause_NamesAService")]
    [TestCase("attribute @D(v: i4);", "D", "an attribute declaration", TestName = "WithClause_NamesAnAttribute")]
    public void WithClause_NamesANonMixin_IsReported(string declaration, string name, string kind)
    {
        var compiled = LanguageFeature.Compile($"{declaration}\nmsg M with {name} {{ b: i4; }}");

        Assert.That(compiled.Only(LanguageFeature.WithClause).Message,
            Is.EqualTo($"'{name}' in the 'with' clause of msg 'M' is {kind}, not a mixin. " +
                       "Only a 'mixin' can be included with 'with'."));
    }

    /// <summary>A builtin is named as what it is, not reported as missing.</summary>
    [Test]
    public void WithClause_NamesABuiltin_IsReported()
        => Assert.That(LanguageFeature.Compile("msg M with i4 { b: i4; }").Only(LanguageFeature.WithClause).Message,
            Does.Contain("is the builtin type 'i4', not a mixin"));

    /// <summary>
    /// Listing the same mixin twice is rejected rather than collapsed: contributing its fields twice
    /// would be caught downstream as a field colliding with itself, which names the wrong mistake.
    /// </summary>
    [Test]
    public void WithClause_DuplicateEntry_IsReportedAtTheSecondOne()
    {
        //             1         2
        //    123456789012345678901234567
        //    msg M with A, A { y: i4; }
        var compiled = LanguageFeature.Compile("mixin A { x: i4; }\nmsg M with A, A { y: i4; }");
        var diagnostic = compiled.Only(LanguageFeature.WithClause);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message,
                Is.EqualTo("Mixin 'A' is listed more than once in the 'with' clause of msg 'M'. Write it once."));
            LanguageFeature.AssertSpan(diagnostic, 2, 15, 16);
            // …and the fields are still contributed exactly once, so nothing downstream sees a
            // message with two fields called 'x'.
            Assert.That(compiled.FieldNames("M"), Is.EqualTo(new[] { "x", "y" }));
        });
    }

    /// <summary>
    /// A clause on a mixin is resolved once, however many messages then include that mixin: a single
    /// misspelling in a widely used mixin is one error, not one per consumer.
    /// </summary>
    [Test]
    public void WithClause_OnAMixin_IsReportedOncePerWrittenClause()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin Shared with Nope { s: i4; }
                                               msg A with Shared { a: i4; }
                                               msg B with Shared { b: i4; }
                                               msg C with Shared { c: i4; }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.WithClause), Has.Count.EqualTo(1), compiled.Describe);
        Assert.That(compiled.Only(LanguageFeature.WithClause).Message, Does.Contain("of mixin 'Shared'"));
    }

    /// <summary>A bad entry does not swallow the good ones beside it.</summary>
    [Test]
    public void WithClause_ABadEntry_DoesNotSkipItsNeighbours()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A { a: i4; }
                                               mixin C { c: i4; }
                                               msg M with A, Nope, C { own: i4; }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.WithClause), Has.Count.EqualTo(1), compiled.Describe);
        Assert.That(compiled.FieldNames("M"), Is.EqualTo(new[] { "a", "c", "own" }));
    }

    // ═══════════════════════════════════════════════════════════════════
    // ION0064 — cycles
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Cycle_DirectSelfInclusion_IsReported()
    {
        var compiled = LanguageFeature.Compile("mixin A with A { a: i4; }\nmsg M with A { z: i4; }");
        var diagnostic = compiled.Only(LanguageFeature.CyclicMixin);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.StartWith("Circular mixin inclusion: A → A."));
            Assert.That(diagnostic.Message, Does.Contain("the chain must terminate"));
            //    1234567890
            //    mixin A with A { … }
            LanguageFeature.AssertSpan(diagnostic, 1, 7, 8);
        });
    }

    [Test]
    public void Cycle_TwoMixins_IsReportedOnce()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A with B { a: i4; }
                                               mixin B with A { b: i4; }
                                               msg M with A { z: i4; }
                                               msg N with B { z: i4; }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.CyclicMixin), Has.Count.EqualTo(1), compiled.Describe);
        Assert.That(compiled.Only(LanguageFeature.CyclicMixin).Message, Does.Contain("A → B → A"));
    }

    /// <summary>A cyclic mixin contributes nothing, so the including message keeps only its own fields.</summary>
    [Test]
    public void Cycle_ContributesNoFields()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A with B { a: i4; }
                                               mixin B with A { b: i4; }
                                               msg M with A { z: i4; }
                                               """);

        Assert.That(compiled.FieldNames("M"), Is.EqualTo(new[] { "z" }));
    }

    /// <summary>
    /// The cycle is the only thing wrong worth saying: a field collision inside it is a consequence,
    /// not a second mistake.
    /// </summary>
    [Test]
    public void Cycle_DoesNotAlsoReportAFieldCollision()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A with B { x: i4; }
                                               mixin B with A { x: i8; }
                                               msg M with A { z: i4; }
                                               """);

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.CyclicMixin }), compiled.Describe);
    }

    /// <summary>A long ring terminates and is reported once, rather than hanging or overflowing.</summary>
    [Test]
    public void Cycle_ALongRing_IsReportedOnceAndTerminates()
    {
        var ring = string.Join("\n", Enumerable.Range(0, 200)
            .Select(i => $"mixin R{i} with R{(i + 1) % 200} {{ g{i}: i4; }}"));

        var compiled = ParseBudget.Within(() => LanguageFeature.Compile(ring));

        Assert.That(compiled.WithCode(LanguageFeature.CyclicMixin), Has.Count.EqualTo(1), compiled.Describe);
        Assert.That(compiled.Success, Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ION0065 — field collisions
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>A mixin field colliding with the includer's own field. Both sources are named.</summary>
    [Test]
    public void Collision_WithTheDeclarationsOwnField_IsReported()
    {
        var compiled = LanguageFeature.Compile("mixin A { x: i4; }\nmsg M with A { x: i8; }");
        var diagnostic = compiled.Only(LanguageFeature.FieldCollision);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message,
                Is.EqualTo("Field 'x' is contributed by mixin 'A' and is also declared by msg 'M', " +
                           "which would give msg 'M' two fields called 'x'. Rename one of them."));
            //             123456789012345678
            //             msg M with A { x: i8; }
            LanguageFeature.AssertSpan(diagnostic, 2, 16, 17);
        });
    }

    /// <summary>
    /// The own field is dropped rather than the mixin's, so the expansion still has unique field
    /// names while the compile fails — a duplicate would reach the generators, which emit one
    /// property per field.
    /// </summary>
    [Test]
    public void Collision_LeavesTheExpansionWithUniqueNames()
    {
        var compiled = LanguageFeature.Compile("mixin A { x: i4; }\nmsg M with A { x: i8; y: i4; }");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.FieldNames("M"), Is.EqualTo(new[] { "x", "y" }));
            Assert.That(compiled.FieldType("M", "x"), Is.EqualTo("i4"), "the mixin's field survives");
        });
    }

    /// <summary>Two different mixins declaring the same field name.</summary>
    [Test]
    public void Collision_BetweenTwoMixins_NamesBothSources()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A { x: i4; }
                                               mixin B { x: i8; }
                                               msg M with A, B { y: i4; }
                                               """);

        Assert.That(compiled.Only(LanguageFeature.FieldCollision).Message,
            Is.EqualTo("Field 'x' is contributed by both mixin 'A' and mixin 'B', which would give " +
                       "msg 'M' two fields called 'x'. Rename one of them, or include only the mixin " +
                       "that already carries the other."));
    }

    /// <summary>
    /// A field arriving through a chain says which mixin in the clause brought it, or the reader is
    /// pointed at a mixin they cannot see in the clause they wrote.
    /// </summary>
    [Test]
    public void Collision_ThroughAChain_NamesTheListedMixin()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A { x: i4; }
                                               mixin B { x: i8; }
                                               mixin C with A { c: i4; }
                                               msg M with C, B { y: i4; }
                                               """);

        Assert.That(compiled.Only(LanguageFeature.FieldCollision).Message,
            Does.Contain("mixin 'A' (included by 'C')"));
    }

    /// <summary>
    /// BUG — the ION0065 dedupe key is <c>field | first source | second source</c>, which is
    /// order-sensitive, so one conflict between one pair of mixins is reported once per <c>with</c>
    /// ordering that appears anywhere in the project. <c>MixinExpansionStage._reportedCollisions</c>
    /// documents the opposite: "keyed by the field and the two origins alone and therefore reported
    /// at the first declaration that hits it". Two reports, at two different declarations, for a
    /// conflict that exists between <c>A</c> and <c>B</c> once.
    /// </summary>
    [Test]
    public void Collision_BetweenTheSamePair_IsReportedOnceRegardlessOfClauseOrder()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A { x: i4; }
                                               mixin B { x: i8; }
                                               msg M with A, B { }
                                               msg N with B, A { }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.FieldCollision), Has.Count.EqualTo(1),
            compiled.Describe);
    }

    /// <summary>
    /// BUG — the second manifestation of the same unstable key, and the worse one: both diagnostics
    /// land on the <em>identical</em> span (mixin <c>B</c>'s own <c>x</c>). <c>Source()</c> renders
    /// <c>A</c> as <c>mixin 'A'</c> while expanding <c>B</c> itself and as
    /// <c>mixin 'A' (included by 'B')</c> while expanding <c>M</c>, so the two keys differ and the
    /// same mistake is stated twice in the same place.
    /// </summary>
    [Test]
    public void Collision_InAMixinThatRedeclaresItsBasesField_IsReportedOnce()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A { x: i4; }
                                               mixin B with A { x: i8; }
                                               msg M with B { y: i4; }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.FieldCollision), Has.Count.EqualTo(1),
            compiled.Describe);
    }

    /// <summary>
    /// A mixin is validated on its own, whether or not anything includes it: an internal collision is
    /// still a mistake in the file.
    /// </summary>
    [Test]
    public void Collision_InsideAnUnusedMixin_IsStillReported()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A { x: i4; }
                                               mixin B with A { x: i8; }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.FieldCollision), Has.Count.EqualTo(1),
            compiled.Describe);
    }

    /// <summary>
    /// A conflict two mixins have with each other is reported at the mixin that pairs them, not at
    /// each of the messages downstream of it.
    /// </summary>
    [Test]
    public void Collision_IsNotRepeatedPerIncludingMessage()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A { x: i4; }
                                               mixin B { x: i8; }
                                               msg M with A, B { }
                                               msg N with A, B { }
                                               msg O with A, B { }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.FieldCollision), Has.Count.EqualTo(1),
            compiled.Describe);
    }

    /// <summary>
    /// A collision with a declaration's own field <em>is</em> keyed by that declaration: each one is a
    /// separate mistake in a separate place.
    /// </summary>
    [Test]
    public void Collision_WithOwnFields_IsReportedPerDeclaration()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A { x: i4; }
                                               msg M with A { x: i8; }
                                               msg N with A { x: i8; }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.FieldCollision).Select(d => d.StartPosition.Line),
            Is.EqualTo(new[] { 2, 3 }), compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ION0066 — a mixin in type position
    // ═══════════════════════════════════════════════════════════════════

    /// <remarks>
    /// The usage always lands on line 2, under <c>mixin A { x: i4; }</c>; the columns below are the
    /// span of the single <c>A</c> in it.
    /// </remarks>
    [TestCase("msg M { f: A; }", 12, 13, TestName = "TypePosition_Field")]
    [TestCase("msg M { f: A[]; }", 12, 13, TestName = "TypePosition_Array")]
    [TestCase("msg M { f: A?; }", 12, 13, TestName = "TypePosition_Optional")]
    [TestCase("msg M { f: Set<A>; }", 16, 17, TestName = "TypePosition_SetElement")]
    [TestCase("msg M { f: Map<string, A>; }", 24, 25, TestName = "TypePosition_MapValue")]
    [TestCase("typedef T = A;", 13, 14, TestName = "TypePosition_TypedefUnderlying")]
    [TestCase("union U { Ok(v: A) }", 17, 18, TestName = "TypePosition_UnionCaseArgument")]
    [TestCase("service S() { Go(): A; }", 21, 22, TestName = "TypePosition_MethodReturn")]
    [TestCase("service S() { Go(v: A): i4; }", 21, 22, TestName = "TypePosition_MethodArgument")]
    [TestCase("mixin Other { f: A; }", 18, 19, TestName = "TypePosition_MixinField")]
    public void TypePosition_IsRejected(string usage, int startCol, int endCol)
    {
        var compiled = LanguageFeature.Compile($"mixin A {{ x: i4; }}\n{usage}");
        var diagnostic = compiled.Only(LanguageFeature.MixinInTypePosition);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message,
                Is.EqualTo("'A' is a mixin, which is a field-set template rather than a type, so it " +
                           "cannot be used here. Include it with 'with A', or declare a 'msg' if you " +
                           "need a type."));
            LanguageFeature.AssertSpan(diagnostic, 2, startCol, endCol);
        });
    }

    /// <summary>
    /// ION0066 replaces ION0009 rather than stacking on it: the name resolves perfectly well, it is
    /// the position that rejects it.
    /// </summary>
    [Test]
    public void TypePosition_DoesNotAlsoReportAnUnresolvedType()
    {
        var compiled = LanguageFeature.Compile("mixin A { x: i4; }\nmsg M with A { f: A; }");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.MixinInTypePosition }),
            compiled.Describe);
    }

    /// <summary>
    /// A service base argument appears once per method in the IR and exactly once in the syntax, so
    /// one written mistake is one diagnostic.
    /// </summary>
    [Test]
    public void TypePosition_InAServiceBaseArgument_IsReportedOnce()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A { x: i4; }
                                               service Api(ctx: A) { P(): i4; Q(): i4; R(): i4; }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.MixinInTypePosition), Has.Count.EqualTo(1),
            compiled.Describe);
    }

    /// <summary>Every written position is reported, so a name used twice is two diagnostics.</summary>
    [Test]
    public void TypePosition_EverySite_IsReported()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin A { x: i4; }
                                               msg M { f: A; g: A[]; h: i4; }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.MixinInTypePosition), Has.Count.EqualTo(2),
            compiled.Describe);
    }

    /// <summary>
    /// CURRENT BEHAVIOUR, flagged: a mixin used only in type position is reported both as
    /// ION0066 and as ION1001 "no message or mixin includes it with 'with'". The hint is technically
    /// true and the two codes have different severities, but the pair reads as contradictory advice
    /// about the same declaration.
    /// </summary>
    [Test]
    public void TypePosition_AlsoCountsAsUnused()
    {
        var compiled = LanguageFeature.Compile("mixin A { x: i4; }\nmsg M { f: A; }");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(LanguageFeature.MixinInTypePosition), Has.Count.EqualTo(1));
            Assert.That(compiled.WithCode(LanguageFeature.Advisory)
                .Select(d => d.Message), Has.One.Contains("Mixin 'A' is defined but"));
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // ION1001 — an unused mixin
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Unused_AMixinNobodyIncludes_IsAHint()
    {
        var compiled = LanguageFeature.Compile("mixin A { x: i4; }\nmsg M { y: i4; }\nservice S() { Go(): M; }");

        var hint = compiled.WithCode(LanguageFeature.Advisory)
            .Single(d => d.Message.StartsWith("Mixin", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(hint.Severity, Is.EqualTo(IonDiagnosticSeverity.Info));
            Assert.That(hint.Message,
                Is.EqualTo("Mixin 'A' is defined but no message or mixin includes it with 'with'."));
            Assert.That(compiled.Success, Is.True, "a hint must not fail the compile");
        });
    }

    [TestCase("msg M with A { y: i4; }", TestName = "Unused_IncludedByAMessage")]
    [TestCase("mixin B with A { y: i4; }\nmsg M with B { z: i4; }", TestName = "Unused_IncludedByAMixin")]
    public void Unused_AnIncludedMixin_IsNotAHint(string usage)
    {
        var compiled = LanguageFeature.Compile($"mixin A {{ x: i4; }}\n{usage}");

        Assert.That(compiled.WithCode(LanguageFeature.Advisory)
            .Where(d => d.Message.StartsWith("Mixin", StringComparison.Ordinal)), Is.Empty, compiled.Describe);
    }

    /// <summary>
    /// QUESTIONABLE, pinned. The registry is <c>Ordinal</c> and the unused-mixin pass is
    /// <c>OrdinalIgnoreCase</c>, so a misspelled include is rejected as naming no mixin (ION0063) and
    /// simultaneously counts as a use, silencing the hint on a mixin that really is dead. Two
    /// comparers for one relationship.
    /// </summary>
    [Test]
    public void Questionable_AMisspelledIncludeSilencesTheUnusedHint()
    {
        var compiled = LanguageFeature.Compile("mixin Audited { x: i4; }\nmsg M with audited { y: i4; }");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(LanguageFeature.WithClause), Has.Count.EqualTo(1),
                compiled.Describe);
            Assert.That(compiled.WithCode(LanguageFeature.Advisory)
                .Where(d => d.Message.StartsWith("Mixin", StringComparison.Ordinal)), Is.Empty);
        });
    }

    /// <summary>
    /// QUESTIONABLE, pinned. <c>@deprecated</c> on a mixin binds, validates and then does nothing:
    /// a mixin produces no <c>IonType</c> for the instance to attach to, and a <c>with</c> clause is
    /// not a type site, so <c>DeprecatedUsageStage</c> never sees it. The same attribute on a
    /// <c>msg</c> warns at every reference. An annotation that looks like it says something and does
    /// not is the failure mode this codebase deletes features over.
    /// </summary>
    [Test]
    public void Questionable_DeprecatingAMixinIsSilentlyANoOp()
    {
        var mixin = LanguageFeature.Compile("""
                                            @deprecated("2.0") mixin A { x: i4; }
                                            msg M with A { y: i4; }
                                            service S() { Go(): M; }
                                            """);

        var message = LanguageFeature.Compile("""
                                              @deprecated("2.0") msg D { x: i4; }
                                              msg M { d: D; }
                                              service S() { Go(): M; }
                                              """);

        Assert.Multiple(() =>
        {
            Assert.That(mixin.WithCode("ION1004"), Is.Empty, "no warning for the mixin");
            Assert.That(message.WithCode("ION1004"), Has.Count.EqualTo(1), "but one for the msg");
        });
    }

    /// <summary>
    /// A mixin field is a real owned edge once spliced, so a mixin that gives its includer a field of
    /// the includer's own type is a genuine cycle and is caught.
    /// </summary>
    [Test]
    public void Cycle_ThroughASplicedMixinField_IsDetected()
    {
        var compiled = LanguageFeature.Compile("mixin A { m: M; }\nmsg M with A { }");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.CircularType }),
            compiled.Describe);
        Assert.That(compiled.Only(LanguageFeature.CircularType).Message, Does.Contain("M → M"));
    }

    /// <summary>And the same field under a cycle breaker is fine, spliced or not.</summary>
    [TestCase("M?", TestName = "Cycle_SplicedOptional")]
    [TestCase("M~", TestName = "Cycle_SplicedPartial")]
    [TestCase("M[]", TestName = "Cycle_SplicedArray")]
    [TestCase("Set<M>", TestName = "Cycle_SplicedSet")]
    public void Cycle_ThroughASplicedMixinField_UnderABreaker_IsFine(string type)
        => LanguageFeature.Compile($"mixin A {{ m: {type}; }}\nmsg M with A {{ }}").AssertAccepted();

    /// <summary>
    /// A msg used only by a mixin's field is used. Reporting it as dead code would tell the author to
    /// delete something the wire depends on.
    /// </summary>
    [Test]
    public void Unused_ATypeReferencedOnlyByAMixinField_IsNotAHint()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg Stamp { at: datetime; }
                                               mixin A { s: Stamp; }
                                               msg M with A { y: i4; }
                                               service Api() { Get(): M; }
                                               """);

        compiled.AssertAccepted();
        Assert.That(compiled.WithCode(LanguageFeature.Advisory), Is.Empty, compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // A MIXIN IS NOT A TYPE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// No lock entry, no IR definition. That single omission is what makes every downstream
    /// "enumerate the definitions" loop correct for free.
    /// </summary>
    [Test]
    public void AMixin_HasNoDefinitionAndNoLockEntry()
    {
        var compiled = LanguageFeature.Compile("""
                                               mixin Audited { createdAt: datetime; }
                                               msg Doc with Audited { title: string; }
                                               service Api() { Get(): Doc; }
                                               """);

        compiled.AssertAccepted();

        Assert.Multiple(() =>
        {
            Assert.That(compiled.DefinitionNames, Is.EquivalentTo(new[] { "Doc" }));
            Assert.That(compiled.Lock().Definitions.Keys, Is.EquivalentTo(new[] { "Doc", "Api" }));
        });
    }

    /// <summary>
    /// A mixin shares the one flat declaration namespace with every type, so a colliding name is
    /// ION0002 — and a name a builtin owns is ION0031, the same as any other declaration.
    /// </summary>
    [Test]
    public void AMixin_SharesTheDeclarationNamespace()
    {
        var duplicate = LanguageFeature.Compile("mixin A { x: i4; }\nmsg A { y: i4; }");
        var shadow = LanguageFeature.Compile("mixin u4 { x: i4; }\nmsg M with u4 { z: i4; }");

        Assert.Multiple(() =>
        {
            Assert.That(duplicate.WithCode(LanguageFeature.Duplicate), Has.Count.EqualTo(1),
                duplicate.Describe);
            Assert.That(shadow.WithCode("ION0031"), Has.Count.EqualTo(1), shadow.Describe);
            Assert.That(shadow.Only("ION0031").Message, Does.StartWith("Mixin 'u4'"));
        });
    }

    /// <summary>Two mixins with the same name are ION0002 like anything else.</summary>
    [Test]
    public void AMixin_DeclaredTwice_IsADuplicate()
        => Assert.That(LanguageFeature.Compile("mixin A { x: i4; }\nmixin A { y: i4; }")
            .WithCode(LanguageFeature.Duplicate), Has.Count.EqualTo(1));

    // ═══════════════════════════════════════════════════════════════════
    // SCALE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A long inclusion chain is a recursive splice; it must not overflow, and the order must hold
    /// all the way down.
    /// </summary>
    [Test]
    public void Scale_ALongChain_ExpandsInOrder()
    {
        const int depth = 400;

        var source = string.Join("\n", Enumerable.Range(0, depth).Select(i =>
                         i == 0 ? "mixin M0 { f0: i4; }" : $"mixin M{i} with M{i - 1} {{ f{i}: i4; }}"))
                     + $"\nmsg Big with M{depth - 1} {{ own: i4; }}";

        var compiled = ParseBudget.Within(() => LanguageFeature.Compile(source));

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        Assert.That(compiled.FieldNames("Big"),
            Is.EqualTo(Enumerable.Range(0, depth).Select(i => $"f{i}").Append("own")));
    }

    /// <summary>One wide mixin included by many messages, expanded once per message.</summary>
    [Test]
    public void Scale_AWideMixinAcrossManyMessages()
    {
        const int width = 100;

        var source = "mixin W { " + string.Join(" ", Enumerable.Range(0, width).Select(i => $"f{i}: i4;")) + " }\n"
                     + string.Join("\n", Enumerable.Range(0, 100).Select(i => $"msg M{i} with W {{ z: i4; }}"));

        var compiled = ParseBudget.Within(() => LanguageFeature.Compile(source));

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        Assert.That(compiled.FieldNames("M99"), Has.Count.EqualTo(width + 1));
    }
}
