namespace ion.syntax.test;

using ion.runtime;

/// <summary>
/// Coverage for the <c>decimal</c> builtin, whose whole subtlety is one omission: it carries
/// <c>builtin</c> and deliberately <em>not</em> <c>scalar</c>.
/// </summary>
/// <remarks>
/// In the std module "scalar" means one fixed-width CBOR head and one machine register. A
/// <c>decimal</c> is CBOR tag 4 — a two element array of exponent and mantissa — so it is neither.
/// Two live consumers read the flag and would both be wrong if it were set:
/// <c>PartialTypeValidationStage</c> would call it "a builtin scalar type" in ION0018, and
/// <c>CircularTypeReferenceStage</c> treats <c>IsScalar</c> as a leaf. Exactness is not what the
/// flag records; width is.
/// </remarks>
public class DecimalSemanticsTests
{
    private static IonType Decimal => IonModule.GetStdModule.Value.Definitions
        .First(d => d.name.Identifier == "decimal");

    /// <summary>The flags themselves, read off the std module rather than inferred from behaviour.</summary>
    [Test]
    public void Decimal_IsBuiltinButNotScalar()
        => Assert.Multiple(() =>
        {
            Assert.That(Decimal.IsBuiltin, Is.True);
            Assert.That(Decimal.IsScalar, Is.False);
            Assert.That(Decimal, Is.Not.InstanceOf<IonGenericType>());
        });

    /// <summary>
    /// The neighbours it is being distinguished from, so the assertion above cannot pass by the whole
    /// notion of "scalar" having quietly gone away.
    /// </summary>
    [TestCase("i4", true)]
    [TestCase("f8", true)]
    [TestCase("bool", true)]
    [TestCase("duration", true)]
    [TestCase("bigint", false)]
    [TestCase("string", false)]
    [TestCase("guid", false)]
    [TestCase("bytes", false)]
    public void Decimal_SitsOnTheSameSideOfScalarAs(string name, bool scalar)
        => Assert.That(IonModule.GetStdModule.Value.Definitions
            .First(d => d.name.Identifier == name).IsScalar, Is.EqualTo(scalar));

    [Test]
    public void Decimal_IsAnOrdinaryFieldType()
    {
        var compiled = LanguageFeature.Compile("msg M { p: decimal; }\nservice Api() { Get(): M; }");

        compiled.AssertAccepted();

        Assert.Multiple(() =>
        {
            Assert.That(compiled.FieldType("M", "p"), Is.EqualTo("decimal"));
            Assert.That(compiled.Lock().Definitions["M"].Fields![0].Type, Is.EqualTo("decimal"));
        });
    }

    /// <summary>It composes with every wrapper, including the two new collections.</summary>
    [TestCase("decimal?", "Maybe<decimal>")]
    [TestCase("decimal[]", "Array<decimal>")]
    [TestCase("decimal[4]", "Array<decimal, 4>")]
    [TestCase("Set<decimal>", "Set<decimal>")]
    [TestCase("Map<string, decimal>", "Map<string, decimal>")]
    [TestCase("Map<i4, Set<decimal>>", "Map<i4, Set<decimal>>")]
    public void Decimal_ComposesWithEveryWrapper(string written, string canonical)
    {
        var compiled = LanguageFeature.Compile($"msg M {{ p: {written}; }}");

        compiled.AssertAccepted();
        Assert.That(compiled.FieldType("M", "p"), Is.EqualTo(canonical));
    }

    /// <summary>
    /// ION0018 must not describe it as a scalar. This is the assertion that fails the moment
    /// somebody "tidies up" the std module by giving <c>decimal</c> the <c>scalar</c> attribute.
    /// </summary>
    [Test]
    public void Decimal_PartialIsRejectedAsANonScalarBuiltin()
    {
        var compiled = LanguageFeature.Compile("msg M { p: decimal~; }");
        var diagnostic = compiled.Only("ION0018");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Message, Does.Contain("'decimal'"));
            Assert.That(diagnostic.Message, Does.Contain("it is a builtin type and has no fields to patch"));
            Assert.That(diagnostic.Message, Does.Not.Contain("builtin scalar type"));
        });
    }

    /// <summary>Members are numbered, so a base type has to hold an integer.</summary>
    [TestCase("enum E : decimal { A }", TestName = "Decimal_EnumBaseIsRejected")]
    [TestCase("flags F : decimal { A = 1 }", TestName = "Decimal_FlagsBaseIsRejected")]
    public void Decimal_IsNotAnEnumBase(string source)
    {
        var compiled = LanguageFeature.Compile(source);

        Assert.That(compiled.Success, Is.False, compiled.Describe);
        Assert.That(compiled.Errors.Select(d => d.Message), Has.One.Contains("'decimal'"));
    }

    /// <summary>
    /// Excluded from Map keys for the same reason as <c>bigint</c>: arbitrary precision means one
    /// value has more than one valid encoding, so ordering by bytes is not ordering by value.
    /// </summary>
    [Test]
    public void Decimal_IsNotAMapKey()
    {
        var compiled = LanguageFeature.Compile("msg M { m: Map<decimal, i4>; }");

        Assert.That(compiled.ErrorCodes, Is.EqualTo(new[] { LanguageFeature.MapKey }), compiled.Describe);
        Assert.That(compiled.Only(LanguageFeature.MapKey).Message, Does.Contain("arbitrary precision"));
    }

    /// <summary>
    /// Not scalar does not mean not a leaf: <c>CircularTypeReferenceStage</c> skips it on
    /// <c>IsBuiltin</c>, so a message holding one is not somehow part of a cycle.
    /// </summary>
    [Test]
    public void Decimal_IsNotACycleParticipant()
        => LanguageFeature.Compile("msg M { p: decimal; q: decimal[4]; }").AssertAccepted();

    /// <summary>Zero type arguments, like every other non-generic builtin.</summary>
    [Test]
    public void Decimal_IsNotGeneric()
        => Assert.That(LanguageFeature.Compile("msg M { p: decimal<i4>; }").Only(LanguageFeature.Arity).Message,
            Is.EqualTo("Type 'decimal' is not generic, but was given 1 type argument(s). Remove the '<...>'."));
}
