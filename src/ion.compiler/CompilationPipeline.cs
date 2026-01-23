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

    public CompilationPipeline(CompilationContext context, ICompilationProgress? progress = null)
    {
        _context = context;
        _progress = progress ?? NullCompilationProgress.Instance;
        ConfigurePipeline();
    }

    private void ConfigurePipeline()
    {
        // Register stages in order of execution
        RegisterStage(new DuplicateSymbolValidationStage(_context));
        RegisterStage(new TransformStage(_context));
        RegisterStage(new StreamParameterValidationStage(_context));
        RegisterStage(new RestoreUnresolvedTypeStage(_context));
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
