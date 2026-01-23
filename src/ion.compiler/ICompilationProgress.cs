namespace ion.compiler;

/// <summary>
/// Progress reporter for compilation pipeline.
/// </summary>
public interface ICompilationProgress
{
    /// <summary>
    /// Report that pipeline execution has started.
    /// </summary>
    void OnPipelineStarted(int totalStages);

    /// <summary>
    /// Report that a stage has started.
    /// </summary>
    void OnStageStarted(int stageNumber, int totalStages, string stageName, string description);

    /// <summary>
    /// Report that a stage has completed.
    /// </summary>
    void OnStageCompleted(int stageNumber, int totalStages, string stageName, int newErrors, int newWarnings);

    /// <summary>
    /// Report that a stage has failed with an exception.
    /// </summary>
    void OnStageFailed(int stageNumber, int totalStages, string stageName, Exception exception);

    /// <summary>
    /// Report that pipeline has stopped due to critical errors.
    /// </summary>
    void OnPipelineStopped(int completedStages, int totalStages, string reason);

    /// <summary>
    /// Report that pipeline has completed successfully.
    /// </summary>
    void OnPipelineCompleted(int totalWarnings);

    /// <summary>
    /// Report that pipeline has completed with errors.
    /// </summary>
    void OnPipelineFailed(int totalErrors, int totalWarnings);
}

/// <summary>
/// Null implementation that does nothing.
/// </summary>
public sealed class NullCompilationProgress : ICompilationProgress
{
    public static readonly NullCompilationProgress Instance = new();

    public void OnPipelineStarted(int totalStages) { }
    public void OnStageStarted(int stageNumber, int totalStages, string stageName, string description) { }
    public void OnStageCompleted(int stageNumber, int totalStages, string stageName, int newErrors, int newWarnings) { }
    public void OnStageFailed(int stageNumber, int totalStages, string stageName, Exception exception) { }
    public void OnPipelineStopped(int completedStages, int totalStages, string reason) { }
    public void OnPipelineCompleted(int totalWarnings) { }
    public void OnPipelineFailed(int totalErrors, int totalWarnings) { }
}
