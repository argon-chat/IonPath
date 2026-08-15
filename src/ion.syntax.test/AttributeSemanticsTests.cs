namespace ion.syntax.test;

using ion.compiler;
using ion.runtime;
using System.Numerics;

/// <summary>
/// Semantic coverage for attributes: arity (ION0032), argument types (ION0033) and their declared
/// ranges (ION0034), named arguments (ION0035–ION0037), targets (ION0038), optional parameters
/// (ION0039), the declaration-side parameter type rules (ION0003 / ION0004) and deprecation
/// (ION1004).
/// <para>
/// Everything here drives the real <see cref="CompilationPipeline"/>, exactly like
/// <c>PartialValidationTests</c> / <c>TypeModifierValidationTests</c> / <c>BuiltinShadowingTests</c>:
/// the unit under test is "what does the compiler say about this source", not any single class.
/// The grammar-level companion — what an attribute argument list <em>parses</em> to — lives in
/// <c>AttributeArgumentTests</c> and <c>LiteralTests</c>; nothing is duplicated between them.
/// </para>
/// <para>
/// Diagnostics are asserted by code <em>and</em> position. A rule that fires with the right code on
/// the wrong span is only half implemented: the argument that is wrong is the thing the author has
/// to edit, so ION0033 / ION0034 must land on the literal, ION0035 on the name token, and ION0032 /
/// ION0038 — which are about the use as a whole — on the attribute.
/// </para>
/// </summary>
public class AttributeSemanticsTests
{
    // ═══════════════════════════════════════════════════════════════════
    // HARNESS
    // ═══════════════════════════════════════════════════════════════════

    private const string TooFewOrManyCode = "ION0032";
    private const string TypeMismatchCode = "ION0033";
    private const string OutOfRangeCode = "ION0034";
    private const string UnknownArgumentCode = "ION0035";
    private const string DuplicateArgumentCode = "ION0036";
    private const string PositionalAfterNamedCode = "ION0037";
    private const string TargetCode = "ION0038";
    private const string RequiredAfterOptionalCode = "ION0039";
    private const string UnresolvedTypeCode = "ION0003";
    private const string BadParameterTypeCode = "ION0004";
    private const string UndeclaredAttributeCode = "ION0005";
    private const string DeprecatedCode = "ION1004";

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

        public IonDiagnostic Single(string code)
        {
            var matches = WithCode(code);

            Assert.That(matches, Has.Count.EqualTo(1),
                () => $"expected exactly one {code}, got: {Describe()}");

            return matches[0];
        }

        public string Describe() => Diagnostics.Count == 0
            ? "(no diagnostics)"
            : string.Join("; ", Diagnostics.Select(d =>
                $"{d.Code}@{d.StartPosition.Line}:{d.StartPosition.Col}: {d.Message}"));

        // RestoreUnresolvedTypeStage re-adds every module to ProcessedModules, so each definition
        // appears twice — the same DistinctBy every other suite here uses.
        public IonType Definition(string name) =>
            Context.ProcessedModules
                .SelectMany(m => m.Definitions)
                .DistinctBy(d => d.name.Identifier)
                .FirstOrDefault(d => d.name.Identifier == name)
            ?? throw new AssertionException($"no definition '{name}'. {Describe()}");

        /// <summary>The single instance of <paramref name="attribute"/> lowered onto a definition.</summary>
        public IonAttributeInstance Attribute(string definition, string attribute)
        {
            var found = Definition(definition).attributes.Where(a => a.Is(attribute)).ToList();

            Assert.That(found, Has.Count.EqualTo(1),
                () => $"expected one @{attribute} on '{definition}', found {found.Count}. {Describe()}");

            return found[0];
        }

        /// <summary>The bound argument values of a use, in declaration order.</summary>
        public IReadOnlyList<object?> Values(string definition, string attribute) =>
            Attribute(definition, attribute).arguments;
    }

    private static Compiled Compile(string source, params string[] features)
    {
        var files = new[] { IonParser.Parse("attrsem0", source) };
        var ctx = CompilationContext.Create(features.Length == 0 ? ["std"] : features, files);
        var success = new CompilationPipeline(ctx).Execute();
        return new Compiled(ctx, success);
    }

    /// <summary>
    /// The two-line shape every argument test uses: the declaration on line 1, the use on line 2,
    /// a message to hang it on for line 3.
    /// <para>
    /// Column arithmetic on line 2 is fixed by this layout and asserted throughout:
    /// <c>@</c> is column 1, the attribute name column 2, <c>(</c> column 3, so the first argument
    /// always starts at column 4.
    /// </para>
    /// </summary>
    private static Compiled Use(string parameters, string arguments) =>
        Compile($"attribute @A({parameters});\n@A({arguments})\nmsg M {{ a: i4; }}\n");

    private const int FirstArgumentColumn = 4;

    private static void AssertAt(IonDiagnostic diagnostic, int line, int column, int? endColumn = null)
    {
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.StartPosition.Line, Is.EqualTo(line),
                () => $"line of {diagnostic.Code}: {diagnostic.Message}");
            Assert.That(diagnostic.StartPosition.Col, Is.EqualTo(column),
                () => $"column of {diagnostic.Code}: {diagnostic.Message}");

            if (endColumn is not null)
            {
                Assert.That(diagnostic.EndPosition, Is.Not.Null, "diagnostic carries no end position");
                Assert.That(diagnostic.EndPosition!.Value.Col, Is.EqualTo(endColumn.Value),
                    () => $"end column of {diagnostic.Code}: {diagnostic.Message}");
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // ARITY — ION0032
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Arity_Exact_IsClean()
    {
        var compiled = Use("x: i4", "1");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Success, Is.True);
        });
    }

    /// <summary>Naming the parameter that is missing is the whole point; a bare count is not a fix.</summary>
    [Test]
    public void Arity_TooFew_NamesTheMissingParameterAndTheSignature()
    {
        var compiled = Use("x: i4, y: i4", "1");
        var diagnostic = compiled.Single(TooFewOrManyCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Severity, Is.EqualTo(IonDiagnosticSeverity.Error));
            Assert.That(compiled.Success, Is.False);
            Assert.That(diagnostic.Message, Does.Contain("missing required argument 'y'"));
            Assert.That(diagnostic.Message, Does.Contain("'@A(x: i4, y: i4)'"));
            // Nothing is wrong with the argument that *was* written.
            Assert.That(compiled.WithCode(TypeMismatchCode), Is.Empty, compiled.Describe);
        });

        // The whole use is the offender, not any one argument.
        AssertAt(diagnostic, 2, 1);
    }

    [Test]
    public void Arity_TwoMissing_ListsBothAndPluralises()
    {
        var diagnostic = Use("x: i4, y: i4, z: i4", "1").Single(TooFewOrManyCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain("missing required arguments 'y', 'z'"));
            Assert.That(diagnostic.Message, Does.Not.Contain("argument's"));
        });
    }

    [Test]
    public void Arity_TooMany_ReportsCountsOnce()
    {
        var compiled = Use("x: i4", "1, 2, 3");
        var diagnostic = compiled.Single(TooFewOrManyCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain("takes 1 argument(s) but 3 were given"));
            Assert.That(diagnostic.Message, Does.Contain("'@A(x: i4)'"));
            // One mistake, one squiggle — not one per surplus argument.
            Assert.That(compiled.Errors, Has.Count.EqualTo(1), compiled.Describe);
        });

        AssertAt(diagnostic, 2, 1);
    }

    [Test]
    public void Arity_ZeroParameters_NoParens_IsClean() =>
        Assert.That(Compile("attribute @A();\n@A\nmsg M { a: i4; }\n").Errors, Is.Empty);

    [Test]
    public void Arity_ZeroParameters_EmptyParens_IsClean() =>
        Assert.That(Compile("attribute @A();\n@A()\nmsg M { a: i4; }\n").Errors, Is.Empty);

    /// <summary><c>@A()</c> and <c>@A</c> are the same use; neither invents an argument.</summary>
    [Test]
    public void Arity_ZeroParameters_ParensAndNoParensAgree()
    {
        var withParens = Compile("attribute @A();\n@A()\nmsg M { a: i4; }\n");
        var without = Compile("attribute @A();\n@A\nmsg M { a: i4; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(withParens.Values("M", "A"), Is.Empty);
            Assert.That(without.Values("M", "A"), Is.Empty);
        });
    }

    [Test]
    public void Arity_ZeroParameters_OneGiven_IsTooMany()
    {
        var diagnostic = Use("", "1").Single(TooFewOrManyCode);

        Assert.That(diagnostic.Message, Does.Contain("takes 0 argument(s) but 1 were given"));
    }

    /// <summary>Empty parens do not satisfy a required parameter.</summary>
    [Test]
    public void Arity_EmptyParens_WithRequiredParameter_IsTooFew()
    {
        var compiled = Compile("attribute @A(x: i4);\n@A()\nmsg M { a: i4; }\n");

        Assert.That(compiled.Single(TooFewOrManyCode).Message,
            Does.Contain("missing required argument 'x'"));
    }

    /// <summary>A named argument counts towards arity; only the unfilled slots are missing.</summary>
    [Test]
    public void Arity_NamedOnly_StillReportsTheUnfilledSlot()
    {
        var compiled = Use("x: i4, y: i4", "y: 2");
        var diagnostic = compiled.Single(TooFewOrManyCode);

        Assert.Multiple(() =>
        {
            // 'y' was supplied by name, so only 'x' is missing.
            Assert.That(diagnostic.Message, Does.Contain("missing required argument 'x'"));
            Assert.That(diagnostic.Message, Does.Contain("'@A(x: i4, y: i4)'"));
            Assert.That(diagnostic.Message, Does.Not.Contain("'x', 'y'"));
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // TYPE MATRIX — ION0033
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every literal form the grammar can produce, in the order they are tried in
    /// <c>Ion.Literals.cs</c>. Each parameter type below declares which of these it accepts; every
    /// other combination must be ION0033.
    /// </summary>
    private static readonly (string Name, string Text)[] Literals =
    [
        ("integer", "7"),
        ("float", "1.5"),
        ("string", "\"txt\""),
        ("bool", "true"),
        ("null", "null"),
        ("array", "[1]"),
        ("enumRef", "Status.Active")
    ];

    /// <summary>
    /// Parameter type → the literal kinds it accepts.
    /// <para>
    /// <c>uri</c> accepts <c>"txt"</c> because a bare word is a legal <em>relative</em> URI, which
    /// is what <c>Uri.TryCreate(.., RelativeOrAbsolute, ..)</c> answers; <c>guid</c>,
    /// <c>datetime</c>, <c>dateonly</c>, <c>timeonly</c> and <c>duration</c> take a string but not
    /// that one, so they land on ION0033 through the "not a valid X" arm. The float widths accept an
    /// integer literal (<c>@ratio(1)</c>), which is why "float" is not the only entry for them.
    /// </para>
    /// </summary>
    private static readonly (string Type, string[] Accepts)[] ParameterTypes =
    [
        ("bool", ["bool"]),
        ("i1", ["integer"]), ("i2", ["integer"]), ("i4", ["integer"]),
        ("i8", ["integer"]), ("i16", ["integer"]),
        ("u1", ["integer"]), ("u2", ["integer"]), ("u4", ["integer"]),
        ("u8", ["integer"]), ("u16", ["integer"]),
        ("f2", ["integer", "float"]), ("f4", ["integer", "float"]), ("f8", ["integer", "float"]),
        ("bigint", ["integer"]),
        ("string", ["string"]),
        ("uri", ["string"]),
        ("guid", []), ("datetime", []), ("dateonly", []), ("timeonly", []), ("duration", []),
        ("i4[]", ["array"])
    ];

    public static IEnumerable<TestCaseData> TypeMatrix()
    {
        foreach (var (type, accepts) in ParameterTypes)
        foreach (var (kind, text) in Literals)
            yield return new TestCaseData(type, text, accepts.Contains(kind))
                .SetName($"Type_{type.Replace("[]", "Array")}_Given_{kind}");
    }

    /// <summary>
    /// The headline matrix: every builtin parameter type against every literal form. This is where a
    /// missing arm in <c>IonAttributeBinder.Convert</c> shows up as either a silent accept or a
    /// diagnostic on the wrong thing.
    /// </summary>
    [TestCaseSource(nameof(TypeMatrix))]
    public void TypeMatrix_AcceptsOnlyItsOwnLiteralForm(string type, string literal, bool accepted)
    {
        var compiled = Use($"x: {type}", literal);
        var mismatches = compiled.WithCode(TypeMismatchCode);

        if (accepted)
        {
            Assert.Multiple(() =>
            {
                Assert.That(mismatches, Is.Empty, compiled.Describe);
                Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            });
            return;
        }

        Assert.That(mismatches, Has.Count.EqualTo(1), compiled.Describe);

        // Always on the literal, never on the whole attribute.
        AssertAt(mismatches[0], 2, FirstArgumentColumn, FirstArgumentColumn + literal.Length);

        Assert.That(mismatches[0].Message, Does.Contain($"argument 'x'"));
    }

    /// <summary>
    /// <c>null</c> is not a type mismatch, it is a nullability mistake, and the message has to say so
    /// — including the <c>T?</c> spelling that would make it legal.
    /// </summary>
    [Test]
    public void Type_NullForNonOptional_SaysHowToMakeItOptional()
    {
        var compiled = Use("x: i4", "null");
        var diagnostic = compiled.Single(TypeMismatchCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain("is not optional"));
            Assert.That(diagnostic.Message, Does.Contain("Declare it as 'i4?'"));
        });

        AssertAt(diagnostic, 2, FirstArgumentColumn, FirstArgumentColumn + 4);
    }

    /// <summary>The three stringly-typed builtins parse their text, and say which one failed.</summary>
    [TestCase("guid", "\"6F9619FF-8B86-D011-B42D-00CF4FC964FF\"")]
    [TestCase("datetime", "\"2020-01-02T03:04:05\"")]
    [TestCase("dateonly", "\"2020-01-02\"")]
    [TestCase("timeonly", "\"03:04:05\"")]
    [TestCase("duration", "\"01:02:03\"")]
    [TestCase("uri", "\"https://example.com/x\"")]
    public void Type_TextBackedBuiltin_AcceptsAValidSpelling(string type, string literal)
    {
        var compiled = Use($"x: {type}", literal);

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
    }

    [TestCase("guid", "\"not-a-guid\"")]
    [TestCase("datetime", "\"not-a-date\"")]
    [TestCase("dateonly", "\"2020-13-45\"")]
    [TestCase("timeonly", "\"99:99\"")]
    [TestCase("duration", "\"forever\"")]
    public void Type_TextBackedBuiltin_RejectsABadSpellingAsThatType(string type, string literal)
    {
        var compiled = Use($"x: {type}", literal);
        var diagnostic = compiled.Single(TypeMismatchCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain($"not a valid {type}"));
            Assert.That(diagnostic.Message, Does.Contain($"expects '{type}'"));
        });

        AssertAt(diagnostic, 2, FirstArgumentColumn, FirstArgumentColumn + literal.Length);
    }

    /// <summary>
    /// <c>void</c> and <c>bytes</c> are builtins with no literal form, so they are rejected in the
    /// <em>declaration</em> — no use site could ever satisfy them.
    /// </summary>
    [TestCase("void")]
    [TestCase("bytes")]
    public void Type_BuiltinWithNoLiteralForm_IsRejectedInTheDeclaration(string type)
    {
        var compiled = Compile($"attribute @A(x: {type});\nmsg M {{ a: i4; }}\n");
        var diagnostic = compiled.Single(BadParameterTypeCode);

        Assert.That(diagnostic.Message, Does.Contain($"Type '{type}' is not allowed in attribute arguments"));
        // On the parameter, which is what has to change.
        AssertAt(diagnostic, 1, 14);
    }

    /// <summary>
    /// An enum cannot be an attribute parameter type: declarations are lowered before any user
    /// definition exists, so the name never resolves. ION0003, not a silent accept.
    /// </summary>
    [Test]
    public void Type_EnumParameterType_IsUnresolvable()
    {
        var compiled = Compile("enum E : i4 { A = 1 }\nattribute @A(x: E);\nmsg M { a: i4; }\n");

        Assert.That(compiled.Single(UnresolvedTypeCode).Message, Does.Contain("Type 'E' not found"));
    }

    /// <summary>
    /// …and the mirror image: an enum <em>reference</em> as an argument names why it cannot work,
    /// rather than reporting a bare "expected i4".
    /// </summary>
    [Test]
    public void Type_EnumReferenceArgument_ExplainsItself()
    {
        var diagnostic = Use("x: i4", "Status.Active").Single(TypeMismatchCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain("enum reference 'Status.Active'"));
            Assert.That(diagnostic.Message, Does.Contain("must be literals of a builtin type"));
        });
    }

    // ── arrays ─────────────────────────────────────────────────────────

    [Test]
    public void Array_WellTyped_IsClean() =>
        Assert.That(Use("x: i4[]", "[1, 2, 3]").Errors, Is.Empty);

    [Test]
    public void Array_Empty_IsAcceptedAndBindsAnEmptyList()
    {
        var compiled = Use("x: i4[]", "[]");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Values("M", "A")[0], Is.InstanceOf<List<object?>>());
            Assert.That((List<object?>)compiled.Values("M", "A")[0]!, Is.Empty);
        });
    }

    /// <summary>
    /// A bad element is reported at the element, labelled by its index — not as "the argument is
    /// wrong", which would leave the author counting commas.
    /// </summary>
    [Test]
    public void Array_WrongElementType_PointsAtTheElement()
    {
        // @A(["s", 2, true])
        //    ^4     ^10 ^13
        var compiled = Use("x: i4[]", "[\"s\", 2, true]");
        var mismatches = compiled.WithCode(TypeMismatchCode);

        Assert.That(mismatches, Has.Count.EqualTo(2), compiled.Describe);

        Assert.Multiple(() =>
        {
            Assert.That(mismatches[0].Message, Does.Contain("argument 'x[0]'"));
            Assert.That(mismatches[1].Message, Does.Contain("argument 'x[2]'"));
        });

        AssertAt(mismatches[0], 2, 5, 8);
        AssertAt(mismatches[1], 2, 13, 17);
    }

    [Test]
    public void Array_NestedWhereScalarExpected_PointsAtTheInnerArray()
    {
        var diagnostic = Use("x: i4[]", "[[1]]").Single(TypeMismatchCode);

        Assert.That(diagnostic.Message, Does.Contain("argument 'x[0]'").And.Contain("array literal of 1 element(s)"));
    }

    [Test]
    public void Array_ScalarGivenToArrayParameter_NamesTheArrayType()
    {
        var diagnostic = Use("x: i4[]", "1").Single(TypeMismatchCode);

        Assert.That(diagnostic.Message, Does.Contain("expects 'i4[]'"));
    }

    [Test]
    public void Array_StringElements_AreAccepted() =>
        Assert.That(Use("x: string[]", "[\"a\", \"b\"]").Errors, Is.Empty);

    // ═══════════════════════════════════════════════════════════════════
    // INTEGER RANGES — ION0034
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Inclusive bounds, and the first value on each side that does not fit.</summary>
    private static readonly (string Type, string Min, string Max, string Under, string Over)[] Widths =
    [
        ("i1", "-128", "127", "-129", "128"),
        ("i2", "-32768", "32767", "-32769", "32768"),
        ("i4", "-2147483648", "2147483647", "-2147483649", "2147483648"),
        ("i8", "-9223372036854775808", "9223372036854775807",
            "-9223372036854775809", "9223372036854775808"),
        ("i16", "-170141183460469231731687303715884105728", "170141183460469231731687303715884105727",
            "-170141183460469231731687303715884105729", "170141183460469231731687303715884105728"),
        ("u1", "0", "255", "-1", "256"),
        ("u2", "0", "65535", "-1", "65536"),
        ("u4", "0", "4294967295", "-1", "4294967296"),
        ("u8", "0", "18446744073709551615", "-1", "18446744073709551616"),
        ("u16", "0", "340282366920938463463374607431768211455",
            "-1", "340282366920938463463374607431768211456")
    ];

    public static IEnumerable<TestCaseData> IntegerBoundaries()
    {
        foreach (var (type, min, max, _, _) in Widths)
        {
            yield return new TestCaseData(type, min).SetName($"Range_{type}_Min");
            yield return new TestCaseData(type, max).SetName($"Range_{type}_Max");
        }
    }

    public static IEnumerable<TestCaseData> IntegerOverflows()
    {
        foreach (var (type, _, _, under, over) in Widths)
        {
            yield return new TestCaseData(type, under).SetName($"Range_{type}_JustUnder");
            yield return new TestCaseData(type, over).SetName($"Range_{type}_JustOver");
        }
    }

    [TestCaseSource(nameof(IntegerBoundaries))]
    public void IntegerWidth_ExactBoundary_IsAccepted(string type, string literal)
    {
        var compiled = Use($"x: {type}", literal);

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
    }

    [TestCaseSource(nameof(IntegerOverflows))]
    public void IntegerWidth_OnePastTheBoundary_IsOutOfRange(string type, string literal)
    {
        var compiled = Use($"x: {type}", literal);
        var diagnostic = compiled.Single(OutOfRangeCode);

        Assert.Multiple(() =>
        {
            // ION0034, not ION0033: the *kind* of literal was right, only the value was not.
            Assert.That(compiled.WithCode(TypeMismatchCode), Is.Empty, compiled.Describe);
            Assert.That(diagnostic.Message, Does.Contain($"does not fit in '{type}'"));
            Assert.That(diagnostic.Message, Does.Contain("valid range"));
            Assert.That(diagnostic.Message, Does.Contain(literal));
        });

        AssertAt(diagnostic, 2, FirstArgumentColumn, FirstArgumentColumn + literal.Length);
    }

    /// <summary><c>bigint</c> is the one integer type with no ceiling.</summary>
    [Test]
    public void Integer_Bigint_HasNoRange()
    {
        var compiled = Use("x: bigint", new string('9', 200));

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Values("M", "A")[0], Is.EqualTo(BigInteger.Parse(new string('9', 200))));
        });
    }

    /// <summary>The range check reads the value, not the spelling.</summary>
    [TestCase("0xFF", true)]
    [TestCase("0x100", false)]
    [TestCase("0b11111111", true)]
    [TestCase("0b1_0000_0000", false)]
    [TestCase("2_5_5", true)]
    [TestCase("2_5_6", false)]
    public void Integer_AlternateSpellings_AreRangeCheckedByValue(string literal, bool fits)
    {
        var compiled = Use("x: u1", literal);

        if (fits)
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        else
            Assert.That(compiled.Single(OutOfRangeCode).Message,
                // The message quotes the author's spelling back, not a normalised value.
                Does.Contain(literal).And.Contain("does not fit in 'u1'"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // FLOAT RANGES — ION0034
    // ═══════════════════════════════════════════════════════════════════

    [TestCase("f2", "65504.0")]
    [TestCase("f2", "-65504.0")]
    [TestCase("f4", "3.4028235e38")]
    [TestCase("f4", "-3.4028235e38")]
    [TestCase("f8", "1.7976931348623157e308")]
    [TestCase("f8", "1e-320")]
    public void Float_WithinRange_IsAccepted(string type, string literal)
    {
        var compiled = Use($"x: {type}", literal);

        Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
    }

    [TestCase("f2", "65520.0", "65500")]
    [TestCase("f2", "1e39", "65500")]
    [TestCase("f4", "1e39", "3.4028235E+38")]
    [TestCase("f4", "-1e39", "3.4028235E+38")]
    public void Float_OverflowsTheWidth_IsOutOfRange(string type, string literal, string limit)
    {
        var compiled = Use($"x: {type}", literal);
        var diagnostic = compiled.Single(OutOfRangeCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain($"does not fit in '{type}'"));
            Assert.That(diagnostic.Message, Does.Contain(limit));
            // ASCII only: these are rendered into CI logs whose encoding is not ours to choose.
            Assert.That(diagnostic.Message, Does.Contain("+/-").And.Not.Contain("±"));
        });

        AssertAt(diagnostic, 2, FirstArgumentColumn, FirstArgumentColumn + literal.Length);
    }

    /// <summary>An integer literal widens into a float parameter — <c>@ratio(1)</c>, not <c>@ratio(1.0)</c>.</summary>
    [TestCase("f2")]
    [TestCase("f4")]
    [TestCase("f8")]
    public void Float_IntegerLiteralWidens(string type)
    {
        var compiled = Use($"x: {type}", "3");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Values("M", "A")[0]?.ToString(), Is.EqualTo("3"));
        });
    }

    /// <summary>…and an integer literal too large for the width is still ION0034.</summary>
    [Test]
    public void Float_IntegerLiteralBeyondTheWidth_IsOutOfRange()
    {
        var compiled = Use("x: f8", new string('9', 400));

        Assert.That(compiled.Single(OutOfRangeCode).Message,
            Does.Contain("does not fit in 'f8'").And.Contain("1.7976931348623157E+308"));
    }

    /// <summary>Narrowing is a value conversion, not a range error: f4 keeps float precision.</summary>
    [Test]
    public void Float_PrecisionFollowsTheDeclaredWidth()
    {
        var f4 = Use("x: f4", "0.1");
        var f8 = Use("x: f8", "0.1");

        Assert.Multiple(() =>
        {
            Assert.That(f4.Values("M", "A")[0], Is.EqualTo(0.1f));
            Assert.That(f8.Values("M", "A")[0], Is.EqualTo(0.1d));
            Assert.That(f4.Values("M", "A")[0], Is.Not.EqualTo(0.1d),
                "an f4 parameter must not silently keep double precision");
        });
    }

    [Test]
    public void Float_HalfPrecisionIsUsedForF2() =>
        Assert.That(Use("x: f2", "1.5").Values("M", "A")[0], Is.EqualTo((Half)1.5f));

    // ═══════════════════════════════════════════════════════════════════
    // NAMED ARGUMENTS — ION0035 / ION0036 / ION0037
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Named_AnyOrder_BindsToTheDeclaredPositions()
    {
        var reversed = Use("x: i4, y: string", "y: \"s\", x: 3");
        var straight = Use("x: i4, y: string", "3, \"s\"");

        Assert.Multiple(() =>
        {
            Assert.That(reversed.Errors, Is.Empty, reversed.Describe);
            Assert.That(reversed.Values("M", "A").Select(v => v?.ToString()),
                Is.EqualTo(new[] { "3", "s" }));
            Assert.That(reversed.Values("M", "A").Select(v => v?.ToString()),
                Is.EqualTo(straight.Values("M", "A").Select(v => v?.ToString())),
                "a named use and the equivalent positional use must produce identical values");
        });
    }

    [Test]
    public void Named_Mixed_PositionalThenNamed_IsLegal()
    {
        var compiled = Use("x: i4, y: string, z: bool", "1, z: true, y: \"s\"");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Values("M", "A").Select(v => v?.ToString()),
                Is.EqualTo(new[] { "1", "s", "True" }));
        });
    }

    /// <summary>The suggestion is the payload of ION0035 for a typo; without one, the list is.</summary>
    [Test]
    public void Named_UnknownName_NearMiss_Suggests()
    {
        // @A(resaon: "s")
        //    ^4     ^10
        var compiled = Use("reason: string", "resaon: \"s\"");
        var diagnostic = compiled.Single(UnknownArgumentCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain("has no parameter named 'resaon'"));
            Assert.That(diagnostic.Message, Does.Contain("Did you mean 'reason'?"));
        });

        // On the name token, not on the value and not on the whole attribute.
        AssertAt(diagnostic, 2, FirstArgumentColumn, FirstArgumentColumn + "resaon".Length);
    }

    [Test]
    public void Named_UnknownName_FarMiss_ListsTheParameters()
    {
        var diagnostic = Use("reason: string, since: string", "zzzzzzzz: \"s\"").Single(UnknownArgumentCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain("Its parameters are: 'reason', 'since'"));
            Assert.That(diagnostic.Message, Does.Not.Contain("Did you mean"));
        });
    }

    [Test]
    public void Named_UnknownName_OnZeroParameterAttribute_SaysNone()
    {
        var diagnostic = Use("", "q: 1").Single(UnknownArgumentCode);

        Assert.That(diagnostic.Message, Does.Contain("Its parameters are: (none)"));
    }

    /// <summary>Transposed characters, a missing character and an extra one all resolve.</summary>
    [TestCase("duartion")]
    [TestCase("duraton")]
    [TestCase("durationn")]
    [TestCase("duraiton")]
    public void Named_Levenshtein_ActuallySuggests(string written)
    {
        var diagnostic = Use("duration: i4", $"{written}: 1").Single(UnknownArgumentCode);

        Assert.That(diagnostic.Message, Does.Contain("Did you mean 'duration'?"), diagnostic.Message);
    }

    [Test]
    public void Named_DuplicateName_IsReportedOnTheSecondOne()
    {
        // @A(x: 1, x: 2)
        //    ^4   ^10
        var compiled = Use("x: i4", "x: 1, x: 2");
        var diagnostic = compiled.Single(DuplicateArgumentCode);

        Assert.That(diagnostic.Message, Does.Contain("argument 'x' is specified more than once"));
        AssertAt(diagnostic, 2, 10, 14);
    }

    [Test]
    public void Named_DuplicatingAPositional_SaysWhichPositionAlreadyFilledIt()
    {
        // @A(1, x: 2)
        //    ^4 ^7
        var compiled = Use("x: i4", "1, x: 2");
        var diagnostic = compiled.Single(DuplicateArgumentCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain("already supplied positionally (argument 1)"));
            Assert.That(diagnostic.Message, Does.Contain("cannot also be given by name"));
        });

        AssertAt(diagnostic, 2, 7, 11);
    }

    [Test]
    public void Named_PositionalAfterNamed_PointsAtTheStrayPositional()
    {
        // @A(x: 1, 2)
        //    ^4    ^10
        var compiled = Use("x: i4, y: i4", "x: 1, 2");
        var diagnostic = compiled.Single(PositionalAfterNamedCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain("positional argument 2 follows a named argument"));
            Assert.That(diagnostic.Message, Does.Contain("every argument after it must be named"));
        });

        AssertAt(diagnostic, 2, 10, 11);
    }

    /// <summary>
    /// A misnamed argument leaves a slot unbound, but "you also forgot 'y'" is the same mistake said
    /// twice — the author has one name to fix.
    /// </summary>
    [Test]
    public void Named_UnknownName_DoesNotAlsoReportTheUnfilledSlot()
    {
        var compiled = Use("x: i4, y: i4", "1, zzz: 2");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(UnknownArgumentCode), Has.Count.EqualTo(1));
            Assert.That(compiled.WithCode(TooFewOrManyCode), Is.Empty, compiled.Describe);
            Assert.That(compiled.Errors, Has.Count.EqualTo(1), compiled.Describe);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // OPTIONAL PARAMETERS — `T?` and ION0039
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Optional_Omitted_BindsNullInItsSlot()
    {
        var compiled = Use("x: i4, y: string?", "1");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Values("M", "A"), Has.Count.EqualTo(2),
                "the slot is still present, holding null");
            Assert.That(compiled.Values("M", "A")[1], Is.Null);
        });
    }

    [Test]
    public void Optional_Supplied_BindsTheValue()
    {
        var compiled = Use("x: i4, y: string?", "1, \"s\"");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Values("M", "A")[1], Is.EqualTo("s"));
        });
    }

    [Test]
    public void Optional_SeveralTrailingOmittedAtOnce()
    {
        var compiled = Use("x: i4, y: string?, z: bool?, w: f4?", "7");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Values("M", "A"), Has.Count.EqualTo(4));
            Assert.That(compiled.Values("M", "A").Skip(1), Is.All.Null);
        });
    }

    /// <summary>An explicit <c>null</c> and an omission are the same thing — both mean "no value".</summary>
    [Test]
    public void Optional_ExplicitNull_IsIndistinguishableFromOmission()
    {
        var written = Use("x: i4, y: string?", "1, null");
        var omitted = Use("x: i4, y: string?", "1");

        Assert.Multiple(() =>
        {
            Assert.That(written.Errors, Is.Empty, written.Describe);
            Assert.That(written.Values("M", "A")[1], Is.Null);
            Assert.That(omitted.Values("M", "A")[1], Is.Null);
        });
    }

    [Test]
    public void Optional_ArrayParameter_AcceptsBothNullAndAValue()
    {
        var withNull = Use("x: i4[]?", "null");
        var withValue = Use("x: i4[]?", "[1, 2]");

        Assert.Multiple(() =>
        {
            Assert.That(withNull.Errors, Is.Empty, withNull.Describe);
            Assert.That(withNull.Values("M", "A")[0], Is.Null);
            Assert.That(withValue.Errors, Is.Empty, withValue.Describe);
            Assert.That(withValue.Values("M", "A")[0], Is.InstanceOf<List<object?>>());
        });
    }

    /// <summary>A middle optional can be skipped by naming the one after it.</summary>
    [Test]
    public void Optional_MiddleSlotSkippedByNamingTheNext()
    {
        var compiled = Use("x: i4, y: string?, z: i4?", "1, z: 5");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Values("M", "A")[1], Is.Null);
            Assert.That(compiled.Values("M", "A")[2]?.ToString(), Is.EqualTo("5"));
        });
    }

    /// <summary>
    /// A required parameter behind an optional one can never be reached — an argument list can only
    /// be truncated from the end.
    /// </summary>
    [Test]
    public void Optional_RequiredAfterOptional_IsRejectedInTheDeclaration()
    {
        // attribute @A(x: string?, y: i4);
        //              ^14         ^26
        var compiled = Compile("attribute @A(x: string?, y: i4);\n@A(\"a\", 2)\nmsg M { a: i4; }\n");
        var diagnostic = compiled.Single(RequiredAfterOptionalCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message,
                Does.Contain("declares required parameter 'y' after optional parameter 'x'"));
            Assert.That(diagnostic.Message, Does.Contain("must come last"));
            Assert.That(compiled.Success, Is.False);
        });

        // On the offending parameter, not on the declaration.
        AssertAt(diagnostic, 1, 26);
    }

    /// <summary>Reported once: the fix is a single edit, moving the optional to the end.</summary>
    [Test]
    public void Optional_RequiredAfterOptional_IsReportedOnceForTheFirstOffender()
    {
        var compiled = Compile("attribute @A(x: i4?, y: i4, z: i4);\nmsg M { a: i4; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(RequiredAfterOptionalCode), Has.Count.EqualTo(1),
                compiled.Describe);
            Assert.That(compiled.Single(RequiredAfterOptionalCode).Message,
                Does.Contain("required parameter 'y'"));
        });
    }

    [Test]
    public void Optional_AllOptional_IsFine()
    {
        var compiled = Compile("attribute @A(x: i4?, y: string?);\n@A\nmsg M { a: i4; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Values("M", "A"), Is.EqualTo(new object?[] { null, null }));
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // TARGETS — ION0038
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every one of the twelve positions an attribute can be written on, each carrying <c>@Only</c>,
    /// laid out so a position's line and column are stable. Line 1 is the declaration under test.
    /// </summary>
    private const string AllPositions = """
        @Only msg M { @Only a: i4; }
        @Only enum E : i4 { @Only X = 1 }
        @Only flags F : u4 { @Only Read = 1 }
        @Only union U { @Only Ok(@Only x: i4) }
        @Only service S(@Only b: i4) { @Only m(@Only p: i4): void; }
        @Only typedef T = i4;
        @Only attribute @Other(@Only q: i4);
        """;

    /// <summary>The (line, column, target) of every <c>@Only</c> in <see cref="AllPositions"/>.</summary>
    private static readonly (int Line, int Col, string Target)[] Positions =
    [
        (2, 1, "msg"), (2, 15, "field"),
        (3, 1, "enum"), (3, 21, "enumMember"),
        (4, 1, "flags"), (4, 22, "enumMember"),
        (5, 1, "union"), (5, 17, "unionCase"), (5, 26, "field"),
        (6, 1, "service"), (6, 17, "argument"), (6, 32, "method"), (6, 40, "argument"),
        (7, 1, "typedef"),
        (8, 1, "attribute"), (8, 24, "argument")
    ];

    [TestCase("msg")]
    [TestCase("field")]
    [TestCase("enum")]
    [TestCase("flags")]
    [TestCase("enumMember")]
    [TestCase("union")]
    [TestCase("unionCase")]
    [TestCase("service")]
    [TestCase("method")]
    [TestCase("argument")]
    [TestCase("typedef")]
    [TestCase("attribute")]
    public void Target_EachKeyword_AllowsExactlyItsOwnPositions(string target)
    {
        var compiled = Compile($"attribute @Only() on {target};\n{AllPositions}\n");

        var rejected = compiled.WithCode(TargetCode)
            .Select(d => (d.StartPosition.Line, d.StartPosition.Col))
            .OrderBy(p => p.Line).ThenBy(p => p.Col)
            .ToList();

        var expected = Positions
            .Where(p => p.Target != target)
            .Select(p => (p.Line, p.Col))
            .OrderBy(p => p.Line).ThenBy(p => p.Col)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(rejected, Is.EqualTo(expected), compiled.Describe);
            // ION0038 is the only complaint: nothing else about these uses is wrong.
            Assert.That(compiled.ErrorCodes.Distinct(), Is.EqualTo(new[] { TargetCode }),
                compiled.Describe);
        });
    }

    /// <summary>Naming the target reads as prose, and the declaration is quoted back.</summary>
    [Test]
    public void Target_Message_NamesThePositionAndTheDeclaration()
    {
        var compiled = Compile("attribute @A() on field, unionCase;\n@A\nmsg M { a: i4; }\n");
        var diagnostic = compiled.Single(TargetCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain("'@A' cannot be applied to a msg"));
            Assert.That(diagnostic.Message, Does.Contain("It is declared 'on field, unionCase'"));
        });

        AssertAt(diagnostic, 2, 1);
    }

    /// <summary>No <c>on</c> clause means "anywhere" — every attribute written before the clause existed keeps compiling.</summary>
    [Test]
    public void Target_NoOnClause_IsAllowedEverywhere()
    {
        var compiled = Compile($"attribute @Only();\n{AllPositions}\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(TargetCode), Is.Empty, compiled.Describe);
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        });
    }

    /// <summary>
    /// An unknown keyword is reported at the keyword, and the declaration then degrades to
    /// unrestricted rather than to "forbidden everywhere" — one typo must not put an ION0038 on
    /// every use.
    /// </summary>
    [Test]
    public void Target_UnknownKeyword_IsReportedOnceAtTheKeyword()
    {
        // attribute @Only() on bogus;
        //                      ^22
        var compiled = Compile($"attribute @Only() on bogus;\n{AllPositions}\n");
        var diagnostic = compiled.Single(TargetCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain("Unknown attribute target 'bogus'"));
            Assert.That(diagnostic.Message, Does.Contain("in the 'on' clause of attribute '@Only'"));
            // The full vocabulary, in declaration order.
            Assert.That(diagnostic.Message, Does.Contain(string.Join(", ", IonAttributeTargets.Keywords)));
        });

        AssertAt(diagnostic, 1, 22, 27);
    }

    /// <summary>A known keyword alongside an unknown one is still enforced.</summary>
    [Test]
    public void Target_PartlyUnknownClause_StillEnforcesTheKnownTargets()
    {
        var compiled = Compile("attribute @A() on bogus, field;\n@A\nmsg M { @A a: i4; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(TargetCode), Has.Count.EqualTo(2), compiled.Describe);
            Assert.That(compiled.WithCode(TargetCode)[0].Message, Does.Contain("Unknown attribute target 'bogus'"));
            Assert.That(compiled.WithCode(TargetCode)[1].Message, Does.Contain("cannot be applied to a msg"));
        });
    }

    [Test]
    public void Target_Duplicated_IsAcceptedAndDeduplicated()
    {
        var accepted = Compile("attribute @A() on field, field, msg;\n@A msg M { @A a: i4; }\n");
        // The `on` clause quoted back by ION0038 must not repeat the duplicate either.
        var reported = Compile("attribute @A() on field, field;\n@A msg M { a: i4; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Errors, Is.Empty, accepted.Describe);
            Assert.That(reported.Single(TargetCode).Message, Does.Contain("'on field'"));
        });
    }

    /// <summary>
    /// A union's shared fields and an inline case's own arguments both lower to fields, so both are
    /// the <c>field</c> target — not <c>argument</c>, which is what they are spelled like.
    /// </summary>
    [Test]
    public void Target_UnionFields_AreFieldsNotArguments()
    {
        var asField = Compile("attribute @A() on field;\nunion U(@A s: i4) { Ok(@A x: i4) }\n");
        var asArgument = Compile("attribute @A() on argument;\nunion U(@A s: i4) { Ok(@A x: i4) }\n");

        Assert.Multiple(() =>
        {
            Assert.That(asField.WithCode(TargetCode), Is.Empty, asField.Describe);
            Assert.That(asArgument.WithCode(TargetCode), Has.Count.EqualTo(2), asArgument.Describe);
        });
    }

    /// <summary>
    /// A service base argument is written once and copied into every method by
    /// <c>TransformStage.PrependMethods</c>; the check must still see it once.
    /// </summary>
    [Test]
    public void Target_ServiceBaseArgument_IsReportedOncePerWrittenDeclaration()
    {
        var compiled = Compile(
            "attribute @A() on typedef;\nservice S(@A id: guid) { m(): void; n(): void; o(): void; }\n");

        Assert.That(compiled.WithCode(TargetCode), Has.Count.EqualTo(1), compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // BUILTIN ATTRIBUTES
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Each std / orleans attribute in one position it belongs and one it does not.</summary>
    [TestCase("@bits(8) msg M { a: i4; }", "msg M { @bits(8) a: i4; }", "@bits", "a field")]
    [TestCase("@bits(8) typedef T = i4;", "@bits(8) union U { Ok(x: i4) }", "@bits", "a union")]
    [TestCase("service S() { @deadline(500) m(): void; }", "@deadline(500) msg M { a: i4; }",
        "@deadline", "a msg")]
    [TestCase("@internal msg M { a: i4; }", "service S() { m(@internal x: i4): void; }",
        "@internal", "an argument")]
    public void Builtin_StdAttribute_LegalAndIllegalTarget(
        string legal, string illegal, string name, string rejectedAs)
    {
        var ok = Compile(legal);
        var bad = Compile(illegal);

        Assert.Multiple(() =>
        {
            Assert.That(ok.Errors, Is.Empty, ok.Describe);
            Assert.That(bad.Single(TargetCode).Message,
                Does.Contain($"'{name}' cannot be applied to {rejectedAs}"));
        });
    }

    [TestCase("service S(@grainId id: guid) { m(): void; }", "@grainId msg M { a: i4; }",
        "@grainId", "a msg")]
    [TestCase("service S() { @oneWay m(): void; }", "@oneWay service S() { m(): void; }",
        "@oneWay", "a service")]
    public void Builtin_OrleansAttribute_LegalAndIllegalTarget(
        string legal, string illegal, string name, string rejectedAs)
    {
        var ok = Compile(legal, "std", "orleans");
        var bad = Compile(illegal, "std", "orleans");

        Assert.Multiple(() =>
        {
            Assert.That(ok.Errors, Is.Empty, ok.Describe);
            Assert.That(bad.Single(TargetCode).Message,
                Does.Contain($"'{name}' cannot be applied to {rejectedAs}"));
        });
    }

    /// <summary>Orleans attributes are gated on the feature, and say so rather than being ignored.</summary>
    [Test]
    public void Builtin_OrleansAttribute_WithoutTheFeature_IsUndeclared()
    {
        var compiled = Compile("service S() { @oneWay m(): void; }");

        Assert.That(compiled.Single(UndeclaredAttributeCode).Message,
            Does.Contain("Attribute 'oneWay' not found"));
    }

    /// <summary>Builtin declarations carry parameter types like any other: <c>@bits</c> takes an i4.</summary>
    [Test]
    public void Builtin_Bits_ArgumentTypeIsEnforced()
    {
        var compiled = Compile("@bits(\"five\") msg M { a: i4; }");

        Assert.That(compiled.Single(TypeMismatchCode).Message,
            Does.Contain("'@bits' argument 'bitCount' expects 'i4'"));
    }

    [Test]
    public void Builtin_Deadline_ArityIsEnforced()
    {
        var compiled = Compile("service S() { @deadline m(): void; }");

        Assert.That(compiled.Single(TooFewOrManyCode).Message,
            Does.Contain("missing required argument 'time'"));
    }

    /// <summary>
    /// <c>@union</c> / <c>@unionCase</c> are synthesized by <c>TransformStage</c> onto a union and
    /// its inline cases. Their declared targets are exactly those two positions, so the synthesized
    /// use can never contradict its own declaration.
    /// </summary>
    [Test]
    public void Builtin_SynthesizedUnionMarkers_DoNotCollideWithTheirDeclarations()
    {
        var compiled = Compile("union U { Ok(x: i4), Err(y: string) }\nmsg M { u: U; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Success, Is.True);
            Assert.That(compiled.Definition("U").attributes.Any(a => a.IsUnion), Is.True,
                "the union marker is still attached");
        });
    }

    /// <summary>
    /// The compiler-synthesized markers cannot be written in source at all — not even on the
    /// construct they legitimately mark. Rejecting them outright, rather than deduping, closes a
    /// soundness hole: a hand-written <c>@builtin</c> on a <c>msg</c> made <c>IonType.IsBuiltin</c>
    /// answer true, which excluded it from cycle detection and made every generator treat it as a
    /// primitive it had no mapping for.
    /// </summary>
    [TestCase("@union msg M { a: i4; }", "union", TestName = "Reserved_Union_OnAMsg")]
    [TestCase("@unionCase msg M { a: i4; }", "unionCase", TestName = "Reserved_UnionCase_OnAMsg")]
    [TestCase("@union union U { Ok(x: i4) }", "union", TestName = "Reserved_Union_OnAUnion")]
    [TestCase("@builtin msg M { a: i4; }", "builtin", TestName = "Reserved_Builtin_OnAMsg")]
    [TestCase("@scalar msg M { a: i4; }", "scalar", TestName = "Reserved_Scalar_OnAMsg")]
    public void Builtin_CompilerInternalMarkers_CannotBeWrittenInSource(string source, string name)
    {
        Assert.That(Compile(source).Single(TargetCode).Message,
            Does.Contain($"'@{name}' is a compiler-internal marker"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // DEPRECATION — ION1004
    // ═══════════════════════════════════════════════════════════════════

    private const string Old = "@deprecated msg Old { a: i4; }\n";

    private static Compiled Deprecated(string rest) => Compile(Old + rest);

    [Test]
    public void Deprecated_IsAWarningNotAnError()
    {
        var compiled = Deprecated("msg N { x: Old; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Single(DeprecatedCode).Severity, Is.EqualTo(IonDiagnosticSeverity.Warning));
            Assert.That(compiled.Success, Is.True, "a deprecated schema is still a valid schema");
        });
    }

    [TestCase("msg N { x: Old; }\n", "the field 'x' of msg 'N'")]
    [TestCase("service S() { m(): Old; }\n", "the return type of method 'm' of service 'S'")]
    [TestCase("service S() { m(p: Old): void; }\n", "the argument 'p' of method 'm' of service 'S'")]
    [TestCase("service S(b: Old) { m(): void; }\n", "the argument 'b' of service 'S'")]
    [TestCase("union U(s: Old) { Ok(x: i4) }\n", "the shared field 's' of union 'U'")]
    [TestCase("union U { Old, Ok(x: i4) }\n", "the case 'Old' of union 'U'")]
    [TestCase("union U { Ok(x: Old) }\n", "the field 'x' of case 'Ok' of union 'U'")]
    [TestCase("typedef T = Old;\n", "the underlying type of typedef 'T'")]
    public void Deprecated_TypeReference_IsWarnedAndNamed(string rest, string expected)
    {
        var compiled = Deprecated(rest);
        var diagnostic = compiled.Single(DeprecatedCode);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain("'Old' is deprecated"));
            Assert.That(diagnostic.Message, Does.Contain($"is referenced by {expected}"));
        });

        // The warning lands on the written name, not on the declaration that contains it.
        Assert.That(diagnostic.EndPosition!.Value.Col - diagnostic.StartPosition.Col,
            Is.EqualTo("Old".Length), compiled.Describe);
    }

    /// <summary>
    /// A service base argument is copied into every method by <c>TransformStage</c>. Walking the IR
    /// reported it once per method; the syntax walk must report the one written declaration once.
    /// </summary>
    [Test]
    public void Deprecated_ServiceBaseArgument_IsWarnedExactlyOnce()
    {
        var compiled = Deprecated("service S(b: Old) { m(): void; n(): void; o(): void; }\n");

        Assert.That(compiled.WithCode(DeprecatedCode), Has.Count.EqualTo(1), compiled.Describe);
    }

    /// <summary>
    /// An <c>enum</c> / <c>flags</c> base type is a type reference like any other and is warned on.
    /// <para>
    /// Reachable only in a schema that is already wrong: a base type is resolved with
    /// <c>ResolveBuiltinType</c>, and a builtin can never be deprecated — so getting here at all
    /// means the name was not a builtin. The warning is still asserted, because a rule that is
    /// enforced in some type positions and silently skipped in others is worse than either.
    /// </para>
    /// </summary>
    [Test]
    public void Deprecated_FlagsBaseType_IsWarned()
    {
        var flags = Compile("@deprecated typedef D = u4;\nflags F : D { Read = 1 }\n");

        Assert.That(flags.Single(DeprecatedCode).Message,
            Does.Contain("'D' is deprecated").And.Contain("the base type of flags 'F'"));
    }

    [Test]
    public void Deprecated_EnumBaseType_IsWarned()
    {
        var @enum = Compile("@deprecated typedef D = i4;\nenum E : D { X = 1 }\n");

        Assert.That(@enum.Single(DeprecatedCode).Message,
            Does.Contain("'D' is deprecated").And.Contain("the base type of enum 'E'"));
    }

    /// <summary>An attribute declaration's parameter type is a type reference too.</summary>
    [Test]
    public void Deprecated_AttributeParameterType_IsWarned()
    {
        var compiled = Compile("@deprecated typedef D = i4;\nattribute @A(x: D);\n");

        Assert.That(compiled.Single(DeprecatedCode).Message,
            Does.Contain("'D' is deprecated").And.Contain("the parameter 'x' of attribute '@A'"));
    }

    /// <summary>A use of an attribute whose own declaration is deprecated.</summary>
    [Test]
    public void Deprecated_AttributeDeclaration_WarnsAtEveryUse()
    {
        var compiled = Compile("@deprecated attribute @A();\n@A msg M { @A a: i4; }\n@A msg N { b: i4; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(DeprecatedCode), Has.Count.EqualTo(3), compiled.Describe);
            Assert.That(compiled.WithCode(DeprecatedCode)[0].Message, Does.Contain("'@A' is deprecated"));
            Assert.That(compiled.Success, Is.True);
        });
    }

    // ── what must stay silent ──────────────────────────────────────────

    [Test]
    public void Deprecated_TheDeclarationItself_IsNotWarned()
    {
        var compiled = Compile(Old);

        Assert.That(compiled.WithCode(DeprecatedCode), Is.Empty, compiled.Describe);
    }

    [Test]
    public void Deprecated_SelfReference_IsNotWarned()
    {
        var compiled = Compile("@deprecated msg Old { a: Old?; }\n");

        Assert.That(compiled.WithCode(DeprecatedCode), Is.Empty, compiled.Describe);
    }

    /// <summary>
    /// Whoever deprecated the container already knows. The alternative is that deprecating a type
    /// floods its own body with warnings about itself.
    /// </summary>
    [TestCase("@deprecated msg N { x: Old; }\n", TestName = "Deprecated_Suppressed_InsideDeprecatedMsg")]
    [TestCase("msg N { @deprecated x: Old; }\n", TestName = "Deprecated_Suppressed_OnDeprecatedField")]
    [TestCase("@deprecated typedef T = Old;\n", TestName = "Deprecated_Suppressed_InsideDeprecatedTypedef")]
    [TestCase("@deprecated service S(b: Old) { m(p: Old): Old; }\n",
        TestName = "Deprecated_Suppressed_InsideDeprecatedService")]
    [TestCase("service S() { @deprecated m(p: Old): Old; }\n",
        TestName = "Deprecated_Suppressed_InsideDeprecatedMethod")]
    [TestCase("service S() { m(@deprecated p: Old): void; }\n",
        TestName = "Deprecated_Suppressed_OnDeprecatedArgument")]
    [TestCase("union U { @deprecated Ok(x: Old) }\n",
        TestName = "Deprecated_Suppressed_InsideDeprecatedUnionCase")]
    public void Deprecated_ReferenceFromInsideSomethingDeprecated_IsSilent(string rest)
    {
        var compiled = Deprecated(rest);

        Assert.That(compiled.WithCode(DeprecatedCode), Is.Empty, compiled.Describe);
    }

    /// <summary>Suppression is scoped to the deprecated member, not to its whole container.</summary>
    [Test]
    public void Deprecated_SuppressionDoesNotLeakToSiblings()
    {
        var compiled = Deprecated("msg N { @deprecated x: Old; y: Old; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(DeprecatedCode), Has.Count.EqualTo(1), compiled.Describe);
            Assert.That(compiled.Single(DeprecatedCode).Message, Does.Contain("the field 'y'"));
        });
    }

    [Test]
    public void Deprecated_SuppressionOnOneMethodDoesNotSilenceTheOther()
    {
        var compiled = Deprecated("service S() { @deprecated m(p: Old): void; n(q: Old): void; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(DeprecatedCode), Has.Count.EqualTo(1), compiled.Describe);
            Assert.That(compiled.Single(DeprecatedCode).Message, Does.Contain("the argument 'q'"));
        });
    }

    // ── @deprecated's own arguments ────────────────────────────────────

    [TestCase("@deprecated", "", "")]
    [TestCase("@deprecated(\"2.0\")", " since '2.0'", "")]
    [TestCase("@deprecated(\"2.0\", \"use N\")", " since '2.0'", " use N.")]
    [TestCase("@deprecated(reason: \"use N\")", "", " use N.")]
    [TestCase("@deprecated(reason: \"use N.\")", "", " use N.")]
    [TestCase("@deprecated(reason: \"use N\", since: \"2.0\")", " since '2.0'", " use N.")]
    public void Deprecated_ZeroOneOrTwoArguments(string marker, string since, string reason)
    {
        var compiled = Compile($"{marker} msg Old {{ a: i4; }}\nmsg N {{ x: Old; }}\n");
        var diagnostic = compiled.Single(DeprecatedCode);

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(diagnostic.Message,
                Is.EqualTo($"'Old' is deprecated{since} and is referenced by the field 'x' of msg 'N'.{reason}"));
        });
    }

    [Test]
    public void Deprecated_ThreeArguments_IsAnArityError()
    {
        var compiled = Compile("@deprecated(\"2.0\", \"use N\", 3) msg M { a: i4; }\n");

        Assert.That(compiled.Single(TooFewOrManyCode).Message,
            Does.Contain("takes 2 argument(s) but 3 were given")
                .And.Contain("'@deprecated(since: string?, reason: string?)'"));
    }

    /// <summary>
    /// <c>@deprecated</c> is declared <c>on</c> every target, spelled out rather than left
    /// unrestricted — so every one of the twelve positions has to accept it.
    /// </summary>
    [Test]
    public void Deprecated_IsAllowedOnEveryTarget()
    {
        var compiled = Compile(AllPositions.Replace("@Only", "@deprecated") + "\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(TargetCode), Is.Empty, compiled.Describe);
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // NO CASCADES
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// An undeclared attribute stops at ION0005. Nothing is known about its parameters, so an arity
    /// or type complaint would be invented rather than derived.
    /// </summary>
    [Test]
    public void Cascade_UndeclaredAttribute_IsOneDiagnostic()
    {
        var compiled = Compile("@Nope(1, 2, x: 3, null, [1, \"s\"]) msg M { a: i4; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { UndeclaredAttributeCode }),
                compiled.Describe);
            Assert.That(compiled.Single(UndeclaredAttributeCode).Message,
                Does.Contain("Attribute 'Nope' not found"));
        });

        AssertAt(compiled.Single(UndeclaredAttributeCode), 1, 1);
    }

    /// <summary>
    /// A parameter type that never resolved is reported once, at the declaration. Checking a value
    /// against a type the compiler does not have would only invent a second, bogus complaint.
    /// </summary>
    [Test]
    public void Cascade_UnresolvableParameterType_DoesNotReachTheUseSite()
    {
        var compiled = Compile("attribute @A(x: Nope);\n@A(1)\nmsg M { a: i4; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { UnresolvedTypeCode }), compiled.Describe);
            Assert.That(compiled.WithCode(TypeMismatchCode), Is.Empty);
            Assert.That(compiled.WithCode(OutOfRangeCode), Is.Empty);
        });
    }

    [Test]
    public void Cascade_DisallowedParameterType_DoesNotReachTheUseSite()
    {
        var compiled = Compile("attribute @A(x: bytes);\n@A(1)\nmsg M { a: i4; }\n");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { BadParameterTypeCode }), compiled.Describe);
    }

    /// <summary>Independent mistakes are still independent: a bad target and a bad argument are both said.</summary>
    [Test]
    public void Cascade_BadTargetAndBadArgument_AreBothReported()
    {
        var compiled = Compile("attribute @A(x: i4) on field;\n@A(\"s\")\nmsg M { a: i4; }\n");

        Assert.That(compiled.ErrorCodes.Order(), Is.EqualTo(new[] { TypeMismatchCode, TargetCode }),
            compiled.Describe);
    }

    [Test]
    public void Cascade_EveryBadArgumentIsReported()
    {
        var compiled = Compile("attribute @A(x: i4, y: i4);\n@A(\"s\", true)\nmsg M { a: i4; }\n");

        Assert.That(compiled.WithCode(TypeMismatchCode), Has.Count.EqualTo(2), compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // THE `IonAttributeInstance.arguments` CONTRACT
    // ═══════════════════════════════════════════════════════════════════

    [Test]
    public void Instance_ArgumentListIsAlwaysTheDeclaredLength()
    {
        var compiled = Use("x: i4, y: string?, z: bool?", "1");

        Assert.That(compiled.Values("M", "A"), Has.Count.EqualTo(3));
    }

    [Test]
    public void Instance_ParameterNamesAreCarriedAndReadable()
    {
        var compiled = Use("since: string?, reason: string?", "reason: \"gone\"");
        var instance = compiled.Attribute("M", "A");

        Assert.Multiple(() =>
        {
            Assert.That(instance.parameterNames, Is.EqualTo(new[] { "since", "reason" }));
            Assert.That(instance.Get<string>("reason"), Is.EqualTo("gone"));
            Assert.That(instance.Get<string>("since"), Is.Null);
            Assert.That(instance.Has("reason"), Is.True);
            Assert.That(instance.Has("since"), Is.False);
            Assert.That(instance["nosuch"], Is.Null);
        });
    }

    [Test]
    public void Instance_ArrayArgumentIsAList()
    {
        var compiled = Use("x: string[]", "[\"a\", \"b\"]");

        Assert.That(compiled.Values("M", "A")[0], Is.EqualTo(new List<object?> { "a", "b" }));
    }

    [TestCase("bool", "true", typeof(bool))]
    [TestCase("string", "\"s\"", typeof(string))]
    [TestCase("guid", "\"6F9619FF-8B86-D011-B42D-00CF4FC964FF\"", typeof(Guid))]
    [TestCase("datetime", "\"2020-01-02T03:04:05\"", typeof(DateTime))]
    [TestCase("dateonly", "\"2020-01-02\"", typeof(DateOnly))]
    [TestCase("timeonly", "\"03:04:05\"", typeof(TimeOnly))]
    [TestCase("uri", "\"https://example.com/\"", typeof(Uri))]
    [TestCase("duration", "\"01:02:03\"", typeof(TimeSpan))]
    [TestCase("f2", "1.5", typeof(Half))]
    [TestCase("f4", "1.5", typeof(float))]
    [TestCase("f8", "1.5", typeof(double))]
    [TestCase("bigint", "5", typeof(BigInteger))]
    [TestCase("i16", "5", typeof(BigInteger))]
    [TestCase("u16", "5", typeof(BigInteger))]
    public void Instance_ValueClrTypeFollowsTheDeclaredParameterType(string type, string literal, Type expected)
    {
        var compiled = Use($"x: {type}", literal);

        Assert.That(compiled.Values("M", "A")[0], Is.InstanceOf(expected), compiled.Describe);
    }

    // ═══════════════════════════════════════════════════════════════════
    // REGRESSIONS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>@deprecated</c> was declared with zero parameters before it grew
    /// <c>since</c> / <c>reason</c>; every existing bare use has to keep compiling.
    /// </summary>
    [Test]
    public void Regression_BareDeprecated_StillCompiles()
    {
        var compiled = Compile("@deprecated msg M { a: i4; }\n@deprecated enum E : i4 { X = 1 }\n" +
                               "@deprecated typedef T = i4;\n@deprecated service S() { @deprecated m(): void; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Values("M", "deprecated"), Is.EqualTo(new object?[] { null, null }));
        });
    }

    /// <summary>
    /// <c>flags P { Read, Write }</c> — every entry without an explicit value — used to spin forever:
    /// the "next free bit" counter was seeded at 0 and advanced with <c>&lt;&lt;= 1</c>, and
    /// <c>0 &lt;&lt; 1</c> is 0. Wrapped in a timeout so a regression fails the test instead of
    /// hanging the run.
    /// </summary>
    [Test]
    public void Regression_FlagsWithImplicitValues_TerminatesAndNumbersTheBits()
    {
        var work = Task.Run(() => Compile("flags P { Read, Write, Exec }\nmsg M { p: P; }\n"));

        Assert.That(work.Wait(TimeSpan.FromSeconds(30)), Is.True,
            "compiling 'flags P { Read, Write, Exec }' did not terminate — the implicit bit counter "
            + "is looping again (TransformStage.CompileFlags must seed nextValue at 1, not 0)");

        var compiled = work.Result;

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(((IonFlags)compiled.Definition("P")).members.Select(m => m.constantValue),
                Is.EqualTo(new[] { "1", "2", "4" }));
        });
    }

    /// <summary>The same shape with an explicit base type and an attribute on the declaration.</summary>
    [Test]
    public void Regression_FlagsWithImplicitValues_AndAnAttribute()
    {
        var work = Task.Run(() => Compile("@deprecated flags P : u4 { Read, Write }\nmsg M { p: P; }\n"));

        Assert.That(work.Wait(TimeSpan.FromSeconds(30)), Is.True, "flags bit counter looping again");

        var compiled = work.Result;

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            // Attributes on a `flags` declaration used to be dropped on the floor.
            Assert.That(compiled.Definition("P").attributes.Any(a => a.IsDeprecated), Is.True,
                compiled.Describe);
        });
    }

    /// <summary>
    /// The binder used to read the parameter's <em>name</em> as the type to parse the argument as and
    /// throw for anything that was not a std type name — so any attribute whose first parameter was
    /// not called <c>i4</c> / <c>string</c> crashed the compiler on its first use.
    /// </summary>
    [Test]
    public void Regression_ParameterNameUnrelatedToItsType_DoesNotCrash()
    {
        var compiled = Compile(
            "attribute @Cache(duration: i4, key: string, enabled: bool);\n" +
            "@Cache(30, \"user\", true)\nmsg M { a: i4; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Diagnostics.Any(d => d.Code == "PIPELINE"), Is.False, compiled.Describe);
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Values("M", "Cache").Select(v => v?.ToString()),
                Is.EqualTo(new[] { "30", "user", "True" }));
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // REGRESSION GUARDS — five defects this pass found, all since fixed
    //
    // Each asserts the *documented* behaviour: the contract in `IonAttributeInstance.arguments`
    // (element CLR types) and in `IonAttributeBinder.Materialize` (which pattern-matches
    // `values is [int]`). Do not "fix" a failure here by relaxing the assertion — a failure means
    // one of the following came back:
    //
    //   1. Every integer argument boxed as BigInteger regardless of declared width. The arms of the
    //      switch expression in `Integer` all converted implicitly to BigInteger, so that was its
    //      natural type and each narrow cast was widened straight back. Fixed by casting to object.
    //   2. Consequence of 1: `@tag(n)` failed `values is [int]`, so IonType.Tag silently read 0.
    //   3. Consequence of 1: `@bits(n)` left HasBitsAttribute false, so IonType.Bits *threw*.
    //   4. `Floating` skipped its range check for non-finite values, but the literal grammar has no
    //      infinity form — non-finite can only mean a decimal that saturated, i.e. out of range.
    //   5. `FindClosest` discarded distance-0 candidates to skip exact matches, but `Compute` folds
    //      case, so a case-only misspelling — the one certain suggestion — was thrown away.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>IonAttributeBinder.Integer</c> ends in a switch expression whose arms are
    /// <c>sbyte</c>/<c>short</c>/<c>int</c>/…/<c>BigInteger</c>. Every one of those converts
    /// implicitly to <see cref="BigInteger"/>, so that is the switch's natural type: the
    /// <c>(int)integer.Value</c> in the <c>"i4"</c> arm is converted straight back and boxed as a
    /// <see cref="BigInteger"/>. The declared width is therefore never observable in the IR, in
    /// contradiction of the contract documented on <c>IonAttributeInstance.arguments</c>.
    /// </summary>
    [TestCase("i1", typeof(sbyte))]
    [TestCase("i2", typeof(short))]
    [TestCase("i4", typeof(int))]
    [TestCase("i8", typeof(long))]
    [TestCase("u1", typeof(byte))]
    [TestCase("u2", typeof(ushort))]
    [TestCase("u4", typeof(uint))]
    [TestCase("u8", typeof(ulong))]
    public void IntegerArgument_IsBoxedAsItsDeclaredWidth(string type, Type expected)
    {
        var compiled = Use($"x: {type}", "5");

        Assert.That(compiled.Values("M", "A")[0], Is.InstanceOf(expected),
            "every integer arm of IonAttributeBinder.Integer is widened to BigInteger by the "
            + "switch expression's natural type; cast each arm to `object` to keep the declared width");
    }

    /// <summary>
    /// <c>@tag</c> was removed from the language. It was declared in std, modelled as its own
    /// attribute-instance subclass and exposed as <c>IonType.Tag</c>, and read by no generator and
    /// no runtime — surface that resolved and validated but could never affect the output.
    /// <para>
    /// It is pinned here rather than deleted because <c>@tag</c> was also the clearest symptom of
    /// the BigInteger boxing defect above: <c>Materialize</c> guarded on <c>values is [int tag]</c>,
    /// which a boxed <see cref="BigInteger"/> never matched, so the tag silently read back as 0. If
    /// a CBOR semantic-tag feature is ever added deliberately, that guard is the trap to avoid.
    /// </para>
    /// </summary>
    [Test]
    public void Tag_IsNoLongerADeclaredAttribute()
    {
        var compiled = Compile("@tag(5) msg M { a: i4; }\n");

        Assert.That(compiled.WithCode(UndeclaredAttributeCode), Has.Count.EqualTo(1),
            "@tag was deleted as unimplemented surface; writing it must say so, not bind silently");
    }

    /// <summary>
    /// The same fallout for <c>@bits</c>, and worse: <c>IonType.Bits</c> is
    /// <c>attributes.OfType&lt;IonBitAttributeInstance&gt;().First()</c>, so reading the width of a
    /// type that carries a source-written <c>@bits</c> throws
    /// <see cref="InvalidOperationException"/> rather than returning it.
    /// </summary>
    [Test]
    public void SourceWrittenBits_ReachesIonTypeBits()
    {
        var compiled = Compile("@bits(16) msg M { a: i4; }\n");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.Errors, Is.Empty, compiled.Describe);
            Assert.That(compiled.Attribute("M", "bits"), Is.InstanceOf<IonBitAttributeInstance>(),
                "Materialize's `values is [int bits]` never matches a boxed BigInteger");
            Assert.That(compiled.Definition("M").HasBitsAttribute, Is.True);
            Assert.That(compiled.Definition("M").Bits, Is.EqualTo(16));
        });
    }

    /// <summary>
    /// Regression guard. <c>IonAttributeBinder.Floating</c> used to range-check only when
    /// <c>double.IsFinite(value)</c>, on the grounds that "a literal written as an infinity stays
    /// one". But <c>1e400</c> is not written as an infinity — it is a finite decimal that
    /// <c>double.Parse</c> saturates to +∞ (.NET Core 3.0+ stopped throwing here), and the literal
    /// grammar has no infinity form at all, so a non-finite value can only mean overflow. It
    /// therefore sailed past the narrowing check for <c>f2</c> and <c>f4</c> and was never checked
    /// for <c>f8</c>, while the equivalent <em>integer</em> literal was correctly ION0034.
    /// </summary>
    [TestCase("f2")]
    [TestCase("f4")]
    [TestCase("f8")]
    public void FloatLiteralBeyondDoubleRange_IsOutOfRange(string type)
    {
        var compiled = Use($"x: {type}", "1e400");

        Assert.Multiple(() =>
        {
            Assert.That(compiled.WithCode(OutOfRangeCode), Has.Count.EqualTo(1),
                "a decimal literal that saturates double must not bypass the ION0034 check");
            // A rejected argument binds to nothing; the point is that +∞ never reaches the model.
            Assert.That(compiled.Values("M", "A")[0], Is.Null,
                "an out-of-range literal must not be materialized, least of all as Infinity");
        });
    }

    /// <summary>
    /// <c>LevenshteinDistance.Compute</c> lowercases both sides, and <c>FindClosest</c> discards any
    /// candidate at distance 0 — a guard meant to skip an exact match. Together they throw away the
    /// one suggestion that is certainly right: <c>@A(Duration: 1)</c> against a parameter called
    /// <c>duration</c> scores 0 and gets no "did you mean", even though identifiers are matched
    /// ordinally and the two are genuinely different names.
    /// </summary>
    [TestCase("Duration")]
    [TestCase("DURATION")]
    public void CaseOnlyMisspelling_GetsASuggestion(string written)
    {
        var diagnostic = Use("duration: i4", $"{written}: 1").Single(UnknownArgumentCode);

        Assert.That(diagnostic.Message, Does.Contain("Did you mean 'duration'?"),
            "FindClosest's `dist > 0` filter and Compute's case-insensitive cost function combine to "
            + "drop every case-only typo");
    }
}
