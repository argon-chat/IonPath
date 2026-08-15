namespace ion.syntax.test;

using ion.runtime;

/// <summary>
/// End-to-end coverage for the two new builtin generics — <c>Map&lt;K, V&gt;</c> and
/// <c>Set&lt;T&gt;</c> — and for the two diagnostics that arrived with them: ION0060 (generic arity,
/// which no generic had before) and ION0061 (a <c>Map</c> key that cannot be one).
/// </summary>
/// <remarks>
/// The nesting cases are not decoration. Before this round a generic argument was parsed as a bare
/// identifier and rebuilt as a bare identifier by <c>CompilationContext.ResolveTypeFor</c>, so
/// <c>Map&lt;string, Array&lt;User&gt;&gt;</c> was a parse error and <c>Map&lt;string, User?&gt;</c>
/// lowered to <c>Map&lt;string, User&gt;</c>. Both halves of that had to be fixed, and a fix on only
/// one of them looks identical from the outside at depth 1.
/// </remarks>
public class MapSetSemanticsTests
{
    // ═══════════════════════════════════════════════════════════════════
    // THE KEY SET ITSELF
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The allowed-key list is data, and the tests below are driven off it, so an empty or truncated
    /// derivation would silently reduce half this file to nothing. Pin the contents — and pin the
    /// exclusions the design argued for hardest: the three floats, which <em>are</em> scalar
    /// builtins and are deliberately not keys.
    /// </summary>
    [Test]
    public void MapKeyBuiltins_AreTheExpectedSet()
        => Assert.Multiple(() =>
        {
            Assert.That(IonModule.MapKeyBuiltins, Is.EquivalentTo(new[]
            {
                "i1", "i2", "i4", "i8", "i16",
                "u1", "u2", "u4", "u8", "u16",
                "bool", "duration", "string", "guid"
            }));

            Assert.That(IonModule.MapKeyBuiltins, Has.No.Member("f2"));
            Assert.That(IonModule.MapKeyBuiltins, Has.No.Member("f4"));
            Assert.That(IonModule.MapKeyBuiltins, Has.No.Member("f8"));
            Assert.That(IonModule.MapKeyBuiltins, Has.No.Member("decimal"));
        });

    // ═══════════════════════════════════════════════════════════════════
    // MAP KEYS — ACCEPTED
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every legal key, driven off <see cref="IonModule.MapKeyBuiltins"/> rather than a copy: a key
    /// added to the runtime list and missed by the compiler must fail here.
    /// </summary>
    [TestCaseSource(nameof(AllowedKeyBuiltins))]
    public void MapKey_AllowedBuiltin_IsAccepted(string key)
        => LanguageFeature.Compile($"msg M {{ m: Map<{key}, i4>; }}").AssertAccepted();

    private static IEnumerable<TestCaseData> AllowedKeyBuiltins() => IonModule.MapKeyBuiltins
        .Select(name => new TestCaseData(name).SetName($"MapKey_Allows_{name}"));

    /// <summary>An enum is the one non-builtin key: integral base, closed named value set.</summary>
    [Test]
    public void MapKey_Enum_IsAccepted()
    {
        var compiled = LanguageFeature.Compile("enum Status { Active, Closed }\nmsg M { m: Map<Status, i4>; }");

        compiled.AssertAccepted();
        Assert.That(compiled.FieldType("M", "m"), Is.EqualTo("Map<Status, i4>"));
    }

    /// <summary>
    /// A typedef is transparent on the wire, so a key is judged on what it erases to — and an alias
    /// for a legal key stays legal however long the chain.
    /// </summary>
    [TestCase("typedef UserId = string;", "UserId")]
    [TestCase("typedef Inner = u8;\ntypedef UserId = Inner;", "UserId")]
    public void MapKey_AliasForALegalKey_IsAccepted(string typedefs, string key)
        => LanguageFeature.Compile($"{typedefs}\nmsg M {{ m: Map<{key}, i4>; }}").AssertAccepted();

    /// <summary>
    /// The value side is unrestricted. Whatever cannot be a key can still be one of these.
    /// </summary>
    [TestCase("f8", TestName = "MapValue_Float")]
    [TestCase("decimal", TestName = "MapValue_Decimal")]
    [TestCase("bytes", TestName = "MapValue_Bytes")]
    [TestCase("datetime", TestName = "MapValue_DateTime")]
    [TestCase("Data", TestName = "MapValue_Message")]
    [TestCase("Data?", TestName = "MapValue_Optional")]
    [TestCase("Data~", TestName = "MapValue_Partial")]
    [TestCase("Data[]", TestName = "MapValue_Array")]
    [TestCase("Data[4]", TestName = "MapValue_FixedArray")]
    [TestCase("Set<Data>", TestName = "MapValue_Set")]
    [TestCase("Map<i4, Data>", TestName = "MapValue_Map")]
    public void MapValue_IsUnrestricted(string value)
        => LanguageFeature.Compile($"msg Data {{ z: i4; }}\nmsg M {{ m: Map<string, {value}>; }}")
            .AssertAccepted();

    // ═══════════════════════════════════════════════════════════════════
    // MAP KEYS — REJECTED
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The whole excluded boundary, one case per reason. Each asserts the code, that it is the
    /// <em>only</em> error, and the fragment of the explanation that names the actual defect — the
    /// message is the entire value of ION0061, since nothing at runtime re-checks a key.
    /// </summary>
    [TestCase("f2", "floating point", TestName = "MapKey_Rejects_f2")]
    [TestCase("f4", "floating point", TestName = "MapKey_Rejects_f4")]
    [TestCase("f8", "does not compare equal to itself", TestName = "MapKey_Rejects_f8")]
    [TestCase("decimal", "arbitrary precision", TestName = "MapKey_Rejects_decimal")]
    [TestCase("bigint", "arbitrary precision", TestName = "MapKey_Rejects_bigint")]
    [TestCase("bytes", "byte string", TestName = "MapKey_Rejects_bytes")]
    [TestCase("datetime", "reference equality", TestName = "MapKey_Rejects_datetime")]
    [TestCase("dateonly", "reference equality", TestName = "MapKey_Rejects_dateonly")]
    [TestCase("timeonly", "reference equality", TestName = "MapKey_Rejects_timeonly")]
    [TestCase("uri", "reference equality", TestName = "MapKey_Rejects_uri")]
    [TestCase("void", "no values", TestName = "MapKey_Rejects_void")]
    public void MapKey_ExcludedBuiltin_IsRejected(string key, string reason)
    {
        var compiled = LanguageFeature.Compile($"msg M {{ m: Map<{key}, i4>; }}");
        var diagnostic = compiled.Only(LanguageFeature.MapKey);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.MapKey }), compiled.Describe);
            Assert.That(diagnostic.Message, Does.StartWith($"'{key}' cannot be a Map key"));
            Assert.That(diagnostic.Message, Does.Contain(reason));
            // ...and states the whole legal set, because the reader is deciding what to write instead.
            Assert.That(diagnostic.Message, Does.Contain("integral scalar builtin"));
        });
    }

    /// <summary>The non-builtin exclusions, each with its own reason.</summary>
    [TestCase("msg Data { z: i4; }", "Data", "a message", TestName = "MapKey_Rejects_Message")]
    [TestCase("union U { Ok(a: i4), No(b: i4) }", "U", "a union", TestName = "MapKey_Rejects_Union")]
    [TestCase("flags F : u4 { A = 1, B = 2 }", "F", "a flags type", TestName = "MapKey_Rejects_Flags")]
    public void MapKey_ExcludedDeclaration_IsRejected(string declaration, string key, string reason)
    {
        var compiled = LanguageFeature.Compile($"{declaration}\nmsg M {{ m: Map<{key}, i4>; }}");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.MapKey }), compiled.Describe);
        Assert.That(compiled.Only(LanguageFeature.MapKey).Message, Does.Contain(reason));
    }

    /// <summary>
    /// A modifier is judged before the name is resolved, because <c>WrapModifiers</c> is what turns
    /// <c>string?</c> into a <c>Maybe&lt;string&gt;</c> and the wrapper is what would go on the wire.
    /// </summary>
    [TestCase("string?", "an absent key is not a key", TestName = "MapKey_Rejects_Optional")]
    [TestCase("Data~", "sparse patch", TestName = "MapKey_Rejects_Partial")]
    [TestCase("string[]", "no canonical byte order", TestName = "MapKey_Rejects_Array")]
    [TestCase("Set<i4>", "a Set", TestName = "MapKey_Rejects_Set")]
    [TestCase("Map<i4, i4>", "a Map", TestName = "MapKey_Rejects_Map")]
    [TestCase("Maybe<string>", "optional", TestName = "MapKey_Rejects_SpelledOutMaybe")]
    [TestCase("Array<string>", "no canonical byte order", TestName = "MapKey_Rejects_SpelledOutArray")]
    [TestCase("Partial<Data>", "sparse patch", TestName = "MapKey_Rejects_SpelledOutPartial")]
    public void MapKey_Wrapped_IsRejected(string key, string reason)
    {
        var compiled = LanguageFeature.Compile($"msg Data {{ z: i4; }}\nmsg M {{ m: Map<{key}, i4>; }}");

        Assert.That(compiled.WithCode(LanguageFeature.MapKey), Has.Count.EqualTo(1), compiled.Describe);
        Assert.That(compiled.Only(LanguageFeature.MapKey).Message, Does.Contain(reason));
    }

    /// <summary>
    /// An alias is transparent, so a key is judged on the underlying type — and the message says both,
    /// because the author wrote the alias and has to be told what it stands for.
    /// </summary>
    [Test]
    public void MapKey_AliasForAnIllegalKey_IsRejectedAndNamesBoth()
    {
        var compiled = LanguageFeature.Compile("typedef Weight = f4;\nmsg M { m: Map<Weight, i4>; }");
        var diagnostic = compiled.Only(LanguageFeature.MapKey);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.StartWith("'Weight' cannot be a Map key"));
            Assert.That(diagnostic.Message, Does.Contain("an alias for 'f4'"));
            Assert.That(diagnostic.Message, Does.Contain("floating point"));
        });
    }

    /// <summary>
    /// ION0061 squiggles the key, not the whole <c>Map&lt;…&gt;</c> — the value half is fine.
    /// </summary>
    [Test]
    public void MapKey_Position_PointsAtTheKeyArgument()
    {
        //             1         2
        //    1234567890123456789012345
        //    msg M { m: Map<f4, i4>; }
        var compiled = LanguageFeature.Compile("msg M { m: Map<f4, i4>; }");

        LanguageFeature.AssertSpan(compiled.Only(LanguageFeature.MapKey), 1, 16, 18);
    }

    /// <summary>And at depth: the key of an inner Map is where the squiggle goes, not the outer one.</summary>
    [Test]
    public void MapKey_Position_PointsAtTheNestedKey()
    {
        //             1         2         3         4
        //    1234567890123456789012345678901234567890123456
        //    msg M { m: Map<string, Array<Map<f4, i4>>>; }
        var compiled = LanguageFeature.Compile("msg M { m: Map<string, Array<Map<f4, i4>>>; }");

        Assert.That(compiled.WithCode(LanguageFeature.MapKey), Has.Count.EqualTo(1), compiled.Describe);
        LanguageFeature.AssertSpan(compiled.Only(LanguageFeature.MapKey), 1, 34, 36);
    }

    /// <summary>
    /// The key check resolves the name rather than matching on the spelling, and it resolves it with
    /// the type checker's own precedence — builtins first. So a <c>msg Map</c> shadowing the builtin
    /// is unreachable (ION0031) and every <c>Map&lt;…&gt;</c> in the file still means the builtin,
    /// key rule included. The two diagnostics agree with each other, which is the property worth
    /// pinning: a key check that name-matched instead would have gone quiet here and let an
    /// unencodable key through.
    /// </summary>
    [Test]
    public void MapKey_IsStillCheckedWhenAMessageShadowsMap()
    {
        var compiled = LanguageFeature.Compile("msg Map { z: i4; }\nmsg M { m: Map<f4, i4>; }");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode("ION0031"), Has.Count.EqualTo(1), compiled.Describe);
            Assert.That(compiled.WithCode(LanguageFeature.MapKey), Has.Count.EqualTo(1), compiled.Describe);
            // …and no arity complaint: the site resolved to the two-parameter builtin, not to the msg.
            Assert.That(compiled.WithCode(LanguageFeature.Arity), Is.Empty, compiled.Describe);
        });
    }

    /// <summary>An unknown key name is ION0009's; ION0061 must not stack a second story on it.</summary>
    [Test]
    public void MapKey_UnknownName_IsOnlyUnresolved()
    {
        var compiled = LanguageFeature.Compile("msg M { m: Map<Nope, i4>; }");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.Unresolved }), compiled.Describe);
    }

    /// <summary>Every bad key in a file is reported, not just the first.</summary>
    [Test]
    public void MapKey_EveryOffendingSite_IsReported()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg M {
                                                   a: Map<f4, i4>;
                                                   b: Map<bytes, i4>;
                                                   c: Map<string, i4>;
                                                   d: Map<datetime, i4>;
                                               }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.MapKey).Select(d => d.StartPosition.Line),
            Is.EqualTo(new[] { 2, 3, 5 }), compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ARITY — ION0060
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The mismatch half, over all five builtin generics — the rule reads
    /// <c>TypeParameters.Count</c> off the definition, so the three wrappers that predate
    /// <c>Map</c>/<c>Set</c> are checked by the very same code and must behave identically.
    /// </summary>
    [TestCase("Maybe<i4, i8>", "Maybe", 1, 2, "Maybe<T>")]
    [TestCase("Array<i4, i8>", "Array", 1, 2, "Array<T>")]
    [TestCase("Partial<i4, i8>", "Partial", 1, 2, "Partial<T>")]
    [TestCase("Set<i4, i8>", "Set", 1, 2, "Set<T>")]
    [TestCase("Map<string>", "Map", 2, 1, "Map<K, V>")]
    [TestCase("Map<string, i4, i8>", "Map", 2, 3, "Map<K, V>")]
    public void Arity_Mismatch_IsReported(string written, string name, int expected, int actual, string fix)
    {
        var compiled = LanguageFeature.Compile($"msg M {{ a: {written}; }}");
        var diagnostic = compiled.Only(LanguageFeature.Arity);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Severity, Is.EqualTo(IonDiagnosticSeverity.Error));
            Assert.That(compiled.Success, Is.False);
            Assert.That(diagnostic.Message,
                Is.EqualTo($"Generic type '{name}' takes {expected} type argument(s), " +
                           $"but was given {actual}. Write '{fix}'."));
        });
    }

    /// <summary>
    /// A generic written bare. <c>Array</c> used to lower to the open generic with no element type,
    /// which every generator then had to guess at.
    /// </summary>
    [TestCase("Maybe", "Maybe", 1)]
    [TestCase("Array", "Array", 1)]
    [TestCase("Partial", "Partial", 1)]
    [TestCase("Set", "Set", 1)]
    [TestCase("Map", "Map", 2)]
    public void Arity_NoArgumentsAtAll_IsReported(string written, string name, int expected)
    {
        var compiled = LanguageFeature.Compile($"msg M {{ a: {written}; }}");

        Assert.That(compiled.Only(LanguageFeature.Arity).Message,
            Is.EqualTo($"Generic type '{name}' takes {expected} type argument(s), but was given 0. " +
                       $"Write '{name}{(expected == 1 ? "<T>" : "<K, V>")}'."));
    }

    /// <summary>
    /// The zero-arity half, split out because the fix is the opposite one — there is no correct
    /// argument list, the brackets have to go.
    /// </summary>
    [TestCase("i4<string>", "i4", 1, TestName = "Arity_NotGeneric_Builtin")]
    [TestCase("string<i4>", "string", 1, TestName = "Arity_NotGeneric_String")]
    [TestCase("decimal<i4>", "decimal", 1, TestName = "Arity_NotGeneric_Decimal")]
    [TestCase("Data<i4>", "Data", 1, TestName = "Arity_NotGeneric_Message")]
    [TestCase("Data<i4, i8>", "Data", 2, TestName = "Arity_NotGeneric_MessageTwoArgs")]
    public void Arity_TypeIsNotGeneric_IsReported(string written, string name, int given)
    {
        var compiled = LanguageFeature.Compile($"msg Data {{ z: i4; }}\nmsg M {{ a: {written}; }}");

        Assert.That(compiled.Only(LanguageFeature.Arity).Message,
            Is.EqualTo($"Type '{name}' is not generic, but was given {given} type argument(s). " +
                       "Remove the '<...>'."));
    }

    /// <summary>
    /// <c>Map&lt;&gt;</c> parses to an empty argument list, which is the same mistake as a bare
    /// <c>Map</c> and must not be a second, different one.
    /// </summary>
    [Test]
    public void Arity_EmptyArgumentList_ReadsAsZeroArguments()
    {
        var compiled = LanguageFeature.Compile("msg M { a: Map<>; }");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.Arity }), compiled.Describe);
        Assert.That(compiled.Only(LanguageFeature.Arity).Message, Does.Contain("but was given 0"));
    }

    /// <summary>ION0060 squiggles the type name, which is the token the argument list belongs to.</summary>
    [Test]
    public void Arity_Position_PointsAtTheGenericName()
    {
        //    123456789012345678901234567
        //    msg M { a: Map<string>; }
        var compiled = LanguageFeature.Compile("msg M { a: Map<string>; }");

        LanguageFeature.AssertSpan(compiled.Only(LanguageFeature.Arity), 1, 12, 15);
    }

    /// <summary>
    /// A nested argument list is its own site: <c>IonTypeSites</c> yields sub-arguments, so the bad
    /// <c>Set</c> inside four levels of wrapping is reached and reported where it is written.
    /// </summary>
    [Test]
    public void Arity_Position_PointsAtTheNestedGenericName()
    {
        //             1         2         3         4         5
        //    12345678901234567890123456789012345678901234567890123456
        //    msg M { m: Map<string, Array<Map<i4, Set<i4, i8>>>>; }
        var compiled = LanguageFeature.Compile("msg M { m: Map<string, Array<Map<i4, Set<i4, i8>>>>; }");

        Assert.That(compiled.WithCode(LanguageFeature.Arity), Has.Count.EqualTo(1), compiled.Describe);
        LanguageFeature.AssertSpan(compiled.Only(LanguageFeature.Arity), 1, 38, 41);
    }

    /// <summary>An unknown name is ION0009's alone; arity has nothing to say about it.</summary>
    [Test]
    public void Arity_UnknownName_IsOnlyUnresolved()
    {
        var compiled = LanguageFeature.Compile("msg M { a: Nope<i4>; }");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.Unresolved }), compiled.Describe);
    }

    /// <summary>
    /// A service's base arguments are copied into every method by <c>TransformStage</c>. The check
    /// walks the syntax, so one written mistake is one diagnostic however many methods there are.
    /// </summary>
    [Test]
    public void Arity_InAServiceBaseArgument_IsReportedOnce()
    {
        var compiled = LanguageFeature.Compile("""
                                               service Api(ctx: Map<string>) {
                                                   A(): i4;
                                                   B(): i4;
                                                   C(): i4;
                                               }
                                               """);

        Assert.That(compiled.WithCode(LanguageFeature.Arity), Has.Count.EqualTo(1), compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // NESTING — the half that was completely broken
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The two headline shapes from the bug report, asserted on the lowered IR rather than on the
    /// parse: <c>ResolveTypeFor</c> rebuilt every argument as a bare name, so both of these lowered
    /// to something the author did not write even once the grammar accepted them.
    /// </summary>
    [TestCase("Map<string, Array<User>>", "Map<string, Array<User>>")]
    [TestCase("Map<string, User?>", "Map<string, Maybe<User>>")]
    [TestCase("Map<string, User~>", "Map<string, Partial<User>>")]
    [TestCase("Map<string, User[]>", "Map<string, Array<User>>")]
    [TestCase("Map<string, User[3]>", "Map<string, Array<User, 3>>")]
    [TestCase("Set<Array<User>>", "Set<Array<User>>")]
    [TestCase("Array<Map<i4, User>>", "Array<Map<i4, User>>")]
    [TestCase("Maybe<Set<User>>", "Maybe<Set<User>>")]
    [TestCase("Map<i4, Map<i4, Map<i4, User>>>", "Map<i4, Map<i4, Map<i4, User>>>")]
    [TestCase("Map<string, Array<Map<i4, Set<Maybe<User>>>>>", "Map<string, Array<Map<i4, Set<Maybe<User>>>>>")]
    public void Nesting_SurvivesLowering(string written, string canonical)
    {
        var compiled = LanguageFeature.Compile($"msg User {{ z: i4; }}\nmsg M {{ a: {written}; }}");

        compiled.AssertAccepted();
        Assert.That(compiled.FieldType("M", "a"), Is.EqualTo(canonical));
    }

    /// <summary>
    /// The same nesting in every written position, not only a field. Each of these reaches
    /// <c>ResolveTypeFor</c> by a different route.
    /// </summary>
    [Test]
    public void Nesting_SurvivesInEveryPosition()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg User { z: i4; }
                                               typedef Index = Map<string, Array<User>>;
                                               msg Holder { a: Index; }
                                               union U(shared: Map<i4, Set<User>>) { Ok(v: Map<string, User?>) }
                                               service Api(ctx: Map<string, Array<User>>) {
                                                   Get(q: Set<Map<i4, User>>): Map<string, Array<User>>;
                                               }
                                               """);

        compiled.AssertAccepted();

        var service = compiled.Context.ProcessedModules.SelectMany(m => m.Services)
            .First(s => s.name.Identifier == "Api");
        var get = service.methods.First(m => m.name.Identifier == "Get");

        Assert.Multiple(() =>
        {
            // The typedef erases, so the field carries the whole nested shape.
            Assert.That(compiled.FieldType("Holder", "a"), Is.EqualTo("Map<string, Array<User>>"));
            Assert.That(LanguageFeature.Canonical(get.returnType), Is.EqualTo("Map<string, Array<User>>"));
            Assert.That(LanguageFeature.Canonical(get.arguments[0].type),
                Is.EqualTo("Map<string, Array<User>>"), "the base argument");
            Assert.That(LanguageFeature.Canonical(get.arguments[1].type), Is.EqualTo("Set<Map<i4, User>>"));
        });
    }

    /// <summary>
    /// A nested argument is what the lock records, so a change buried three levels down is still a
    /// breaking change — which it could not have been while the argument was rebuilt as a bare name.
    /// </summary>
    [Test]
    public void Nesting_IsPartOfTheLockedWireIdentity()
    {
        var before = LanguageFeature.Compile("msg User { z: i4; }\nmsg M { a: Map<string, Array<User>>; }");
        before.AssertAccepted();

        Assert.That(before.Lock().Definitions["M"].Fields![0].Type,
            Is.EqualTo("Map<string, Array<User>>"));

        var after = LanguageFeature.Compile("msg User { z: i4; }\nmsg M { a: Map<string, Set<User>>; }",
            before.Lock());

        Assert.That(after.WithCode("ION0022"), Has.Count.EqualTo(1), after.Describe);
        Assert.That(after.WithCode("ION0022")[0].Message,
            Does.Contain("from 'Map<string, Array<User>>' to 'Map<string, Set<User>>'"));
    }

    /// <summary>
    /// A type used only as a nested generic argument is used. <c>CollectReferencedTypeNames</c> read
    /// the argument's <em>head</em> name, so <c>User</c> here was reported as dead code (ION1001)
    /// while being a live part of the wire format.
    /// </summary>
    [Test]
    public void Nesting_ANestedArgumentCountsAsAReference()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg User { z: i4; }
                                               msg Holder { a: Map<string, Array<User>>; }
                                               service Api() { Get(): Holder; }
                                               """);

        compiled.AssertAccepted();
        Assert.That(compiled.WithCode(LanguageFeature.Advisory), Is.Empty, compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // CYCLES — Map and Set are cycle-breaking on both sides
    // ═══════════════════════════════════════════════════════════════════

    [TestCase("Map<string, Tree>", TestName = "Cycle_MapValue_Terminates")]
    [TestCase("Set<Tree>", TestName = "Cycle_Set_Terminates")]
    [TestCase("Map<string, Map<i4, Tree>>", TestName = "Cycle_NestedMap_Terminates")]
    [TestCase("Map<string, Tree[]>", TestName = "Cycle_MapOfArray_Terminates")]
    public void Cycle_ThroughACollection_IsNotACycle(string type)
    {
        var compiled = LanguageFeature.Compile($"msg Tree {{ children: {type}; }}");

        Assert.That(compiled.WithCode(LanguageFeature.CircularType), Is.Empty, compiled.Describe);
        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SET
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>Set</c> has no key rule of its own. That is a deliberate asymmetry with <c>Map</c> and
    /// worth pinning: a set of floats is representable (it is a list on the wire), a map keyed by
    /// one is not.
    /// </summary>
    [TestCase("f4")]
    [TestCase("decimal")]
    [TestCase("bytes")]
    [TestCase("datetime")]
    [TestCase("Data")]
    public void Set_ElementType_IsUnrestricted(string element)
        => LanguageFeature.Compile($"msg Data {{ z: i4; }}\nmsg M {{ s: Set<{element}>; }}").AssertAccepted();

    /// <summary>Both collections lock under their own name with their arguments spelled out.</summary>
    [Test]
    public void MapAndSet_LockUnderTheirCanonicalName()
    {
        var compiled = LanguageFeature.Compile("""
                                               msg M { a: Map<string, i4>; b: Set<guid>; }
                                               service Api() { Get(): M; }
                                               """);

        compiled.AssertAccepted();

        var fields = compiled.Lock().Definitions["M"].Fields!;

        Assert.Multiple(() =>
        {
            Assert.That(fields[0].Type, Is.EqualTo("Map<string, i4>"));
            Assert.That(fields[1].Type, Is.EqualTo("Set<guid>"));
        });
    }
}
