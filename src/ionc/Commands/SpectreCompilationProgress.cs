namespace ion.compiler.Commands;

using ion.compiler;
using Spectre.Console;
using System.Diagnostics;

/// <summary>
/// Spectre.Console implementation with live progress bars and status spinners.
/// </summary>
public sealed class SpectreCompilationProgressWithContext(ProgressContext progressContext) : ICompilationProgress
{
    private readonly Dictionary<int, ProgressTask> _stageTasks = new();
    private readonly Dictionary<int, Stopwatch> _stageTimers = new();
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
        _stageTimers[stageNumber] = Stopwatch.StartNew();
    }

    public void OnStageCompleted(int stageNumber, int totalStages, string stageName, int newErrors, int newWarnings)
    {
        if (_stageTasks.TryGetValue(stageNumber, out var task))
        {
            task.Value = 100;

            var elapsed = _stageTimers.TryGetValue(stageNumber, out var sw)
                ? $" [dim]({sw.Elapsed.TotalMilliseconds:0}ms)[/]"
                : "";

            if (newErrors > 0)
            {
                var errorText = newErrors == 1 ? "error" : "errors";
                task.Description = $"[red]✗ {newErrors} {errorText}[/]{elapsed}";
            }
            else if (newWarnings > 0)
            {
                var warningText = newWarnings == 1 ? "warning" : "warnings";
                task.Description = $"[yellow]⚠ {newWarnings} {warningText}[/]{elapsed}";
            }
            else
            {
                task.Description = $"[green]✓ Passed[/]{elapsed}";
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
