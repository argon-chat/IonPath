namespace ion.compiler.Commands;

using ion.compiler;
using Spectre.Console;

/// <summary>
/// Spectre.Console implementation with live progress bars and status spinners.
/// </summary>
public sealed class SpectreCompilationProgressWithContext(ProgressContext progressContext) : ICompilationProgress
{
    private readonly Dictionary<int, ProgressTask> _stageTasks = new();
    private int _totalStages;

    public void OnPipelineStarted(int totalStages)
    {
        _totalStages = totalStages;
    }

    public void OnStageStarted(int stageNumber, int totalStages, string stageName, string description)
    {
        var task = progressContext.AddTask($"[cyan]{stageName.EscapeMarkup()}[/]", maxValue: 100);
        task.Description = $"[dim]{description.EscapeMarkup()}[/]";
        _stageTasks[stageNumber] = task;
    }

    public void OnStageCompleted(int stageNumber, int totalStages, string stageName, int newErrors, int newWarnings)
    {
        if (_stageTasks.TryGetValue(stageNumber, out var task))
        {
            task.Value = 100;
            
            if (newErrors > 0)
            {
                var errorText = newErrors == 1 ? "error" : "errors";
                task.Description = $"[red]✗ {newErrors} {errorText}[/]";
            }
            else if (newWarnings > 0)
            {
                var warningText = newWarnings == 1 ? "warning" : "warnings";
                task.Description = $"[yellow]⚠ {newWarnings} {warningText}[/]";
            }
            else
            {
                task.Description = $"[green]✓ Passed[/]";
            }
            
            task.StopTask();
        }
    }

    public void OnStageFailed(int stageNumber, int totalStages, string stageName, Exception exception)
    {
        if (_stageTasks.TryGetValue(stageNumber, out var task))
        {
            task.Description = $"[red]✗ Crashed[/]";
            task.StopTask();
        }
        
        AnsiConsole.MarkupLine($"\n[red]✗ {stageName.EscapeMarkup()} crashed[/]");
        AnsiConsole.WriteException(exception, ExceptionFormats.ShortenEverything);
    }

    public void OnPipelineStopped(int completedStages, int totalStages, string reason)
    {
        // Stop all remaining tasks
        foreach (var task in _stageTasks.Values)
        {
            if (!task.IsFinished)
                task.StopTask();
        }
    }

    public void OnPipelineCompleted(int totalWarnings)
    {
        // All tasks should be completed by now
    }

    public void OnPipelineFailed(int totalErrors, int totalWarnings)
    {
        // Errors will be shown by diagnostic renderer
    }
}
