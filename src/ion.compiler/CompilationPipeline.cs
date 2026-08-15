namespace ion.compiler;

using ion.runtime;
using ion.syntax;

/// <summary>
/// Orchestrates the compilation process through multiple stages.
/// </summary>
public sealed class CompilationPipeline
{
    private readonly CompilationContext _context;
    private readonly List<CompilationStage> _stages = [];
    private readonly ICompilationProgress _progress;
    private readonly IonSchemaLock? _existingLock;

    public CompilationPipeline(CompilationContext context, ICompilationProgress? progress = null, IonSchemaLock? existingLock = null)
    {
        _context = context;
        _progress = progress ?? NullCompilationProgress.Instance;
        _existingLock = existingLock;
        ConfigurePipeline();
    }

    private void ConfigurePipeline()
    {
        // Register stages in order of execution.
        //
        // First: a `#feature` the project does not enable is the *cause* of every unresolved name
        // and unknown attribute that follows, and the pipeline runs every stage before deciding, so
        // this diagnostic has to be the one at the top of the report.
        RegisterStage(new FeatureDirectiveStage(_context));
        // Also purely syntactic — it reads only the `#use` directives — so it runs beside the other
        // pre-IR checks. A cycle here does not stop the compile: the files are one compilation unit
        // regardless, and reporting the cycle beats hiding every other diagnostic behind it.
        RegisterStage(new ImportCycleDetectionStage(_context));
        // Purely syntactic: `~`/`[]`/`?` spelling needs no name resolution, so it runs before the
        // IR exists and a doubled modifier is reported alongside, not instead of, a bad type name.
        //
        // Before InlineTypeHoistingStage, and that ordering is load-bearing. Every one of these
        // diagnostics echoes the type as the author wrote it, and `IonTypeSites.NameAsWritten`
        // renders an inline body as `msg { … }` — but only while the body is still there. Run after
        // hoisting, the same check read the name the compiler had just derived, so
        // `msg M { m: msg { … }?~; }` advised the author to "write 'MM~?'", quoting a type that
        // appears nowhere in their file, while the un-hoistable `Array<msg { … }[0]>` beside it
        // echoed correctly for no reason other than that its body had survived. One rule now: the
        // modifier suffixes are judged on the written tree, before anything rewrites it.
        RegisterStage(new TypeModifierValidationStage(_context));
        // Tree shaping, before anything reads the declaration list. Hoisting turns every inline
        // `msg { … }` into an ordinary top level message, so duplicate detection sees the derived
        // names as declarations and nothing after this point needs to know inline types exist. It
        // runs before mixin expansion so an inline type written in a mixin is hoisted once, named
        // after the mixin, rather than once per message that includes it.
        RegisterStage(new InlineTypeHoistingStage(_context));
        RegisterStage(new DuplicateSymbolValidationStage(_context));
        // Resolves every `with` clause and pins the field order the wire depends on. After
        // duplicate detection (a mixin shares the declaration namespace, so a colliding name is
        // ION0002's to report first) and before the transform, which reads the expansion.
        RegisterStage(new MixinExpansionStage(_context));
        RegisterStage(new TransformStage(_context));
        // Immediately after the transform, which is what populates the attribute *declarations* of
        // every module. Attribute uses are checked against them here rather than during lowering:
        // the syntax walk sees each written attribute once and knows what it is attached to, and
        // the transform sees service base arguments once per method and has already erased targets.
        RegisterStage(new AttributeValidationStage(_context));
        RegisterStage(new ImportValidationStage(_context));
        RegisterStage(new StreamParameterValidationStage(_context));
        RegisterStage(new RestoreUnresolvedTypeStage(_context));
        // After type resolution: `T~` validation needs typedefs erased and every name resolved.
        RegisterStage(new PartialTypeValidationStage(_context));
        // Beside the partial checker and for the same reasons: generic arity and Map key legality
        // are both about a written type, and both need typedefs erased first — a typedef is
        // transparent, so `Map<UserId, V>` is judged on what `UserId` stands for.
        RegisterStage(new GenericTypeValidationStage(_context));
        RegisterStage(new CircularTypeReferenceStage(_context));

        // Advisory, non-blocking. Deprecation runs after resolution so every written name has a
        // definition to inspect, and before the unused-symbol pass purely for report ordering.
        RegisterStage(new DeprecatedUsageStage(_context));

        // Unused symbol detection (hints, non-blocking)
        RegisterStage(new UnusedSymbolDetectionStage(_context));

        // Schema lock validation (needs fully resolved types)
        if (_existingLock is not null)
            RegisterStage(new SchemaLockValidationStage(_context, _existingLock));
    }

    public void RegisterStage(CompilationStage stage)
    {
        _stages.Add(stage);
    }

    public bool Execute()
    {
        if (_stages.Count == 0)
            return true;

        var totalStages = _stages.Count;
        _progress.OnPipelineStarted(totalStages);

        var currentStage = 0;

        foreach (var stage in _stages)
        {
            currentStage++;
            
            _progress.OnStageStarted(currentStage, totalStages, stage.StageName, stage.StageDescription);

            var errorsBefore = _context.Diagnostics.Count(d => d.Severity == IonDiagnosticSeverity.Error);
            var warningsBefore = _context.Diagnostics.Count(d => d.Severity == IonDiagnosticSeverity.Warning);

            try
            {
                stage.DoProcess();
            }
            catch (Exception ex)
            {
                _progress.OnStageFailed(currentStage, totalStages, stage.StageName, ex);
                
                _context.Diagnostics.Add(new IonDiagnostic(
                    "PIPELINE", 
                    IonDiagnosticSeverity.Error,
                    $"Internal compiler error in stage '{stage.StageName}': {ex.Message}", 
                    new IonSyntaxBase()));
                
                _progress.OnPipelineFailed(
                    _context.Diagnostics.Count(d => d.Severity == IonDiagnosticSeverity.Error),
                    _context.Diagnostics.Count(d => d.Severity == IonDiagnosticSeverity.Warning)
                );
                return false;
            }

            var errorsAfter = _context.Diagnostics.Count(d => d.Severity == IonDiagnosticSeverity.Error);
            var warningsAfter = _context.Diagnostics.Count(d => d.Severity == IonDiagnosticSeverity.Warning);
            
            var newErrors = errorsAfter - errorsBefore;
            var newWarnings = warningsAfter - warningsBefore;

            _progress.OnStageCompleted(currentStage, totalStages, stage.StageName, newErrors, newWarnings);

            // Don't stop immediately - collect all errors first
        }

        // After all stages complete, check if we have ANY errors
        var totalErrors = _context.Diagnostics.Count(d => d.Severity == IonDiagnosticSeverity.Error);
        var totalWarnings = _context.Diagnostics.Count(d => d.Severity == IonDiagnosticSeverity.Warning);

        if (totalErrors > 0)
        {
            _progress.OnPipelineFailed(totalErrors, totalWarnings);
            return false;
        }

        _progress.OnPipelineCompleted(totalWarnings);
        return true;
    }
}
