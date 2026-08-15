namespace ion.syntax.test;

using ion.compiler;
using runtime;

/// <summary>
/// Coverage for <see cref="ImportCycleDetectionStage"/> (ION0001).
/// </summary>
/// <remarks>
/// This used to be a <c>TestDfs</c> smoke test that called a <c>Run</c> overload and asserted
/// nothing. That overload could not have worked: it keyed its module map on <c>file.FullName</c>
/// while looking up <c>IonUseSyntax.Path</c>, which is the string as written inside the quotes, so
/// every edge missed and the walk always found nothing. The stage's real entry point,
/// <c>DoProcess</c>, threw <see cref="NotImplementedException"/> and was never registered in the
/// pipeline, so ION0001 could not be produced by any code path at all.
/// </remarks>
public class ImportCycleDetectionTests
{
    private static IonFileSyntax File(string name, params string[] uses) =>
        new(name, new FileInfo(name), [.. uses.Select(u => new IonUseSyntax(u))],
            [], [], [], [], [], [], [], [], [], []);

    private static List<IonDiagnostic> Run(params IonFileSyntax[] files)
    {
        var ctx = CompilationContext.Create(["std"], [.. files]);
        new ImportCycleDetectionStage(ctx).DoProcess();
        return ctx.Diagnostics.Where(d => d.Code == "ION0001").ToList();
    }

    [Test]
    public void TwoFilesImportingEachOther_IsACycle()
    {
        var cycles = Run(File("a1", "a2"), File("a2", "a1"));

        Assert.That(cycles, Has.Count.EqualTo(1), "a1 → a2 → a1");
        Assert.That(cycles[0].Message, Does.Contain("a1").And.Contain("a2"));
    }

    [Test]
    public void ThreeFileCycle_IsReportedOnce()
    {
        var cycles = Run(File("a1", "a2"), File("a2", "a3"), File("a3", "a1"));

        Assert.That(cycles, Has.Count.EqualTo(1),
            "one loop is one mistake, however many hops it takes");
    }

    [Test]
    public void AcyclicImports_AreClean()
    {
        Assert.That(Run(File("a1", "a2"), File("a2", "a3"), File("a3")), Is.Empty);
    }

    /// <summary>
    /// A diamond is not a cycle: two files may both import a third.
    /// </summary>
    [Test]
    public void Diamond_IsNotACycle()
    {
        Assert.That(Run(File("root", "left", "right"), File("left", "leaf"),
            File("right", "leaf"), File("leaf")), Is.Empty);
    }

    [Test]
    public void SelfImport_IsIgnoredRatherThanReported()
    {
        Assert.That(Run(File("a1", "a1")), Is.Empty,
            "a file naming itself is a no-op, not a cycle worth a diagnostic");
    }

    /// <summary>
    /// The written path is matched leniently — with or without the <c>.ion</c> suffix, and through a
    /// directory prefix — because that is how the same directive is read elsewhere in the compiler.
    /// </summary>
    [TestCase("a2.ion", "a1.ion", TestName = "ImportPath_WithIonSuffix")]
    [TestCase("./sub/a2", "../a1", TestName = "ImportPath_WithDirectoryPrefix")]
    public void ImportPathSpelling_StillResolves(string fromA1, string fromA2)
    {
        Assert.That(Run(File("a1", fromA1), File("a2", fromA2)), Has.Count.EqualTo(1));
    }

    [Test]
    public void ImportOfAModuleOutsideThisProject_IsNotACycle()
    {
        Assert.That(Run(File("a1", "some-external-module")), Is.Empty,
            "an unresolvable #use names an external module, which cannot import back");
    }
}
