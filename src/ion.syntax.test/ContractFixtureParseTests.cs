namespace ion.syntax.test;

using Pidgin;

/// <summary>
/// The real contracts under <c>src/tests/Contracts/Contracts</c>, parsed with the strict grammar.
/// </summary>
/// <remarks>
/// These files are the integration suite's input and are checked in alongside generated output, so a
/// grammar change that quietly alters how one of them parses is a change to shipped artefacts. They
/// are read-only here: the test asserts they still parse with <see cref="IonParser.IonFile"/> — the
/// non-recovering entry point, so anything short of a clean parse fails rather than degrading into
/// an <see cref="InvalidIonBlock"/> — and pins the shape of the result.
/// <para>
/// Between them they cover doc comments in every form, module docs, a BOM, attributes with every
/// argument kind, typedefs in both the plain and the vestigial-block spelling, services with base
/// arguments, and every modifier stacking. None of them uses a generic argument, a fixed array size,
/// a mixin or an inline type, which is exactly the point: none of the five new features may change
/// how a file that predates them reads.
/// </para>
/// </remarks>
public class ContractFixtureParseTests
{
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
    /// Prefixed per consumer. <c>TestCaseData.SetName</c> replaces the <em>whole</em> test name, so
    /// two <c>[TestCaseSource]</c> methods generating <c>Fixture_&lt;name&gt;</c> produce colliding
    /// names and the runner reports them as one result — which was hiding a real failure behind a
    /// sibling that passed.
    /// </remarks>
    private static IEnumerable<TestCaseData> FixturesNamed(string prefix) =>
        FixtureDirectory()
            .GetFiles("*.ion")
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new TestCaseData(f.Name)
                .SetName($"{prefix}_{Path.GetFileNameWithoutExtension(f.Name)}"));

    private static IEnumerable<TestCaseData> ParseFixtures() => FixturesNamed("Parses");

    private static IEnumerable<TestCaseData> ShapeFixtures() => FixturesNamed("Shape");

    [TestCaseSource(nameof(ParseFixtures))]
    public void Fixture_ParsesWithTheStrictGrammar(string fileName)
    {
        var path = Path.Combine(FixtureDirectory().FullName, fileName);
        var text = File.ReadAllText(path);

        var result = IonParser.IonFile.Parse(text);

        Assert.That(result.Success, Is.True, () => $"{fileName} no longer parses: {result.Error}");
        Assert.That(result.Value.OfType<InvalidIonBlock>(), Is.Empty);
    }

    /// <summary>
    /// At least one fixture must exist — a rename or a move must fail loudly rather than turning
    /// this whole class into a silent no-op.
    /// </summary>
    /// <remarks>
    /// Seven, not six: <c>CollectionInteraction.ion</c> was added for <c>Map</c> / <c>Set</c> /
    /// <c>T[N]</c>. Naming them rather than counting them, so the next addition says which file
    /// arrived instead of only that the number moved.
    /// </remarks>
    [Test]
    public void TheFixturesAreWhereTheyAreExpectedToBe()
    {
        Assert.That(FixtureDirectory().GetFiles("*.ion").Select(f => f.Name), Is.EquivalentTo(new[]
        {
            "AttributeInteraction.ion",
            "CollectionInteraction.ion",
            "DataInteraction.ion",
            "LedgerInteraction.ion",
            "MathInteraction.ion",
            "PartialInteraction.ion",
            "VectorInteraction.ion"
        }));
    }

    /// <summary>
    /// The declaration census. A count that moves means the grammar started or stopped seeing
    /// something in a file nobody edited.
    /// </summary>
    [TestCase("AttributeInteraction.ion", 2, 2, 0, 0, 0, 1, 2)]
    [TestCase("DataInteraction.ion", 0, 1, 0, 0, 0, 0, 0)]
    [TestCase("LedgerInteraction.ion", 2, 1, 2, 0, 0, 0, 0)]
    [TestCase("MathInteraction.ion", 0, 2, 0, 0, 0, 0, 0)]
    [TestCase("PartialInteraction.ion", 2, 1, 0, 0, 0, 0, 0)]
    [TestCase("VectorInteraction.ion", 3, 1, 2, 0, 0, 0, 0)]
    public void Fixture_DeclarationCensusIsUnchanged(
        string fileName, int messages, int services, int typedefs, int unions, int flags, int enums,
        int attributes)
    {
        var path = Path.Combine(FixtureDirectory().FullName, fileName);
        var file = IonParser.Parse(Path.GetFileNameWithoutExtension(fileName), File.ReadAllText(path));

        Assert.Multiple(() =>
        {
            Assert.That(file.messageSyntaxes, Has.Count.EqualTo(messages), "messages");
            Assert.That(file.serviceSyntaxes, Has.Count.EqualTo(services), "services");
            Assert.That(file.typedefSyntaxes, Has.Count.EqualTo(typedefs), "typedefs");
            Assert.That(file.unionSyntaxes, Has.Count.EqualTo(unions), "unions");
            Assert.That(file.flagsSyntaxes, Has.Count.EqualTo(flags), "flags");
            Assert.That(file.enumSyntaxes, Has.Count.EqualTo(enums), "enums");
            Assert.That(file.attributeDefSyntaxes, Has.Count.EqualTo(attributes), "attribute declarations");
            Assert.That(file.mixinSyntaxes, Is.Empty, "no fixture uses a mixin yet");
            Assert.That(file.allTokens!.OfType<InvalidIonBlock>(), Is.Empty);
        });
    }

    /// <summary>
    /// Every written type in a fixture that predates the five new features is a plain named
    /// reference: none of them may silently acquire an inline body or a fixed array size from a
    /// grammar change.
    /// </summary>
    /// <remarks>
    /// INVERTED for <c>CollectionInteraction.ion</c>, which was added afterwards and exists
    /// precisely to exercise <c>T[N]</c>. Scoped rather than deleted, and asserted in both
    /// directions: the six older files must stay clean, and the new one must actually contain the
    /// shapes it claims to — a fixture that quietly stopped using a fixed size would leave the
    /// integration suite testing nothing.
    /// </remarks>
    [TestCaseSource(nameof(ShapeFixtures))]
    public void Fixture_UsesTheNewTypeShapesOnlyWhereIntended(string fileName)
    {
        var path = Path.Combine(FixtureDirectory().FullName, fileName);
        var file = IonParser.Parse(Path.GetFileNameWithoutExtension(fileName), File.ReadAllText(path));

        var types = file.messageSyntaxes.SelectMany(m => m.Fields).Select(f => f.Type)
            .Concat(file.serviceSyntaxes.SelectMany(s => s.BaseArguments).Select(a => a.type))
            .Concat(file.serviceSyntaxes.SelectMany(s => s.Methods).SelectMany(m => m.arguments)
                .Select(a => a.type))
            .Concat(file.serviceSyntaxes.SelectMany(s => s.Methods)
                .Select(m => m.returnType).OfType<IonUnderlyingTypeSyntax>())
            .Concat(file.typedefSyntaxes.Select(t => t.BaseType).OfType<IonUnderlyingTypeSyntax>())
            .ToList();

        var expectsFixedSizes = fileName == "CollectionInteraction.ion";

        Assert.That(types, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            // No fixture uses an inline anonymous type, new one included.
            Assert.That(types.Where(t => t.IsInline), Is.Empty);

            if (expectsFixedSizes)
            {
                Assert.That(types.Where(t => t.ArraySize is not null), Is.Not.Empty,
                    "the collection fixture is the one that exercises 'T[N]'");
                Assert.That(types.Where(t => t.ArraySize is not null)
                    .All(t => t.ArraySizeStart is not null), Is.True,
                    "a written size must carry the span its diagnostic anchors on");
            }
            else
            {
                Assert.That(types.Where(t => t.ArraySize is not null), Is.Empty);
                Assert.That(types.Where(t => t.ArraySizeStart is not null), Is.Empty);
            }
        });
    }
}
