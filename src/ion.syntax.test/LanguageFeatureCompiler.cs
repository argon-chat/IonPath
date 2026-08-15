namespace ion.syntax.test;

using ion.compiler;
using ion.runtime;

/// <summary>
/// The shared end-to-end harness for the five language features that landed together —
/// <c>Map</c>/<c>Set</c>, <c>decimal</c>, <c>T[N]</c>, mixins and inline anonymous types.
/// </summary>
/// <remarks>
/// <para>
/// Every one of them is a <em>semantic</em> feature: the grammar's part is covered by
/// <see cref="MixinTests"/>, <see cref="GenericArgumentTests"/>, <see cref="FixedSizeArrayTests"/>
/// and <see cref="InlineTypeTests"/>, which parse and stop. What those cannot see is the half that
/// actually ships — which diagnostic comes out, where it is anchored, what the IR ends up shaped
/// like, and what lands in <c>ion.lock.json</c>. So these suites drive the real
/// <see cref="CompilationPipeline"/>, exactly as <see cref="PartialValidationTests"/> and
/// <see cref="BuiltinShadowingTests"/> do.
/// </para>
/// <para>
/// Factored out rather than copied into each of the five files: they assert against one another's
/// features constantly (a mixin field of an inline type, a fixed array of a hoisted type, a
/// <c>Map</c> keyed by a mixin), and five drifting copies of "what counts as an error here" is
/// exactly how a cross-feature hole survives a review.
/// </para>
/// </remarks>
internal static class LanguageFeature
{
    public const string Arity = "ION0060";
    public const string MapKey = "ION0061";
    public const string FixedArraySize = "ION0062";
    public const string WithClause = "ION0063";
    public const string CyclicMixin = "ION0064";
    public const string FieldCollision = "ION0065";
    public const string MixinInTypePosition = "ION0066";
    public const string InlineNameCollision = "ION0067";
    public const string InlineNotAllowed = "ION0068";

    public const string Unresolved = "ION0009";
    public const string Duplicate = "ION0002";
    public const string CircularType = "ION0030";
    public const string Advisory = "ION1001";

    /// <summary>The placeholder an un-hoisted inline body leaves behind. It must never be visible.</summary>
    public const string InlinePlaceholder = "$inline";

    public static Compiled Compile(string source, IonSchemaLock? existingLock = null)
        => CompileMany([source], existingLock);

    public static Compiled CompileMany(IReadOnlyList<string> sources, IonSchemaLock? existingLock = null)
    {
        var files = sources.Select((s, i) => IonParser.Parse($"langfeature{i}", s)).ToList();
        var ctx = CompilationContext.Create(["std"], files);
        var success = new CompilationPipeline(ctx, null, existingLock).Execute();
        return new Compiled(ctx, success, files);
    }

    /// <summary>
    /// The wire identity of a resolved type, rendered the way <c>SchemaLockGenerator</c> renders it.
    /// </summary>
    /// <remarks>
    /// Reimplemented here because <c>SchemaLockGenerator.GetCanonicalTypeName</c> is
    /// <see langword="internal"/>. That is not a loss: an independent rendering is what makes an
    /// assertion about the IR mean something, and <see cref="Compiled.Lock"/> exercises the real one
    /// wherever the lock is what matters.
    /// </remarks>
    public static string Canonical(IonType type)
    {
        if (type is not IonGenericType { TypeArguments.Count: > 0 } generic)
            return type.name.Identifier;

        var args = string.Join(", ", generic.TypeArguments.Select(Canonical));

        if (generic.FixedSize is { } size)
            args += $", {size}";

        return $"{generic.name.Identifier}<{args}>";
    }

    /// <summary>
    /// Asserts a diagnostic covers exactly <c>line:startCol</c>..<c>line:endCol</c>.
    /// </summary>
    /// <remarks>
    /// Columns are 1-based and the end is exclusive, matching <c>Pidgin.SourcePos</c> — the span of
    /// <c>f4</c> at the start of a line is 1..3. A diagnostic anchored on the wrong token is a bug in
    /// its own right, so every code in these suites has at least one test that pins the span rather
    /// than only the code.
    /// </remarks>
    public static void AssertSpan(IonDiagnostic diagnostic, int line, int startCol, int endCol)
        => Assert.Multiple(() =>
        {
            Assert.That(diagnostic.StartPosition.Line, Is.EqualTo(line), $"{diagnostic.Code} start line");
            Assert.That(diagnostic.StartPosition.Col, Is.EqualTo(startCol), $"{diagnostic.Code} start col");
            Assert.That(diagnostic.EndPosition, Is.Not.Null, $"{diagnostic.Code} has no end position");
            Assert.That(diagnostic.EndPosition!.Value.Line, Is.EqualTo(line), $"{diagnostic.Code} end line");
            Assert.That(diagnostic.EndPosition!.Value.Col, Is.EqualTo(endCol), $"{diagnostic.Code} end col");
        });
}

/// <summary>One compilation, with the handful of projections these suites keep asking for.</summary>
internal sealed record Compiled(
    CompilationContext Context,
    bool Success,
    IReadOnlyList<IonFileSyntax> Files)
{
    public IReadOnlyList<IonDiagnostic> Diagnostics => Context.Diagnostics;

    public IReadOnlyList<IonDiagnostic> Errors => Diagnostics
        .Where(d => d.Severity == IonDiagnosticSeverity.Error)
        .ToList();

    /// <summary>Error codes in report order. Advisory ION10xx hints are deliberately excluded.</summary>
    public IReadOnlyList<string> ErrorCodes => Errors.Select(d => d.Code).ToList();

    public IReadOnlyList<IonDiagnostic> WithCode(string code) => Diagnostics
        .Where(d => d.Code == code)
        .ToList();

    /// <summary>The single diagnostic with <paramref name="code"/>; fails if there is not exactly one.</summary>
    public IonDiagnostic Only(string code)
    {
        var matches = WithCode(code);
        Assert.That(matches, Has.Count.EqualTo(1), $"expected exactly one {code}. {Describe()}");
        return matches[0];
    }

    public string Describe() => Diagnostics.Count == 0
        ? "(no diagnostics)"
        : string.Join("; ", Diagnostics.Select(d =>
            $"{d.Code}@{d.StartPosition.Line}:{d.StartPosition.Col} {d.Message}"));

    /// <summary>
    /// Whether the parser had to fall back to error recovery anywhere.
    /// </summary>
    /// <remarks>
    /// <c>CompilationPipeline</c> never looks at <see cref="InvalidIonBlock"/> — only <c>ionc</c> and
    /// the LSP do — so a source that fails to parse compiles "clean" here with zero definitions. Any
    /// test that means to exercise a semantic rule has to assert this is false, or a typo in the
    /// fixture silently turns the test into a no-op.
    /// </remarks>
    public bool HasParseErrors => Files.Any(f => (f.allTokens ?? []).OfType<InvalidIonBlock>().Any());

    // RestoreUnresolvedTypeStage re-adds every module to ProcessedModules, so each definition appears
    // twice. See PartialValidationTests / TypedefTests, which do the same.
    public IReadOnlyList<IonType> Definitions => Context.ProcessedModules
        .SelectMany(m => m.Definitions)
        .DistinctBy(d => d.name.Identifier)
        .ToList();

    public IReadOnlyList<string> DefinitionNames => Definitions
        .Where(d => !d.IsBuiltin)
        .Select(d => d.name.Identifier)
        .ToList();

    public IonType Definition(string name) =>
        Definitions.FirstOrDefault(d => d.name.Identifier == name)
        ?? throw new AssertionException($"no definition named '{name}' (have: " +
                                        $"{string.Join(", ", DefinitionNames)})");

    /// <summary>The field names of a message, in wire order — which for a mixin includer is the point.</summary>
    public IReadOnlyList<string> FieldNames(string typeName) =>
        Definition(typeName).fields.Select(f => f.name.Identifier).ToList();

    public string FieldType(string typeName, string fieldName) =>
        LanguageFeature.Canonical(
            Definition(typeName).fields.FirstOrDefault(f => f.name.Identifier == fieldName)?.type
            ?? throw new AssertionException($"no field '{fieldName}' on '{typeName}'"));

    public IonSchemaLock Lock() => SchemaLockGenerator.Generate("langfeature", Context.ProcessedModules);

    /// <summary>Asserts the compile is clean — no errors, no warnings, and the source really parsed.</summary>
    public void AssertAccepted()
    {
        Assert.Multiple(() =>
        {
            Assert.That(HasParseErrors, Is.False, "the fixture does not parse; the test proves nothing");
            Assert.That(Errors, Is.Empty, Describe);
            Assert.That(Success, Is.True, Describe);
        });
    }
}
