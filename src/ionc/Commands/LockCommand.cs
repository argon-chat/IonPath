namespace ion.compiler.Commands;

using runtime;
using Spectre.Console;
using Spectre.Console.Cli;
using syntax;
using System.ComponentModel;

public class LockInitOptions : CommandSettings;

public class LockCheckOptions : CommandSettings;

public class LockUpdateOptions : CommandSettings;

/// <summary>
/// Generate initial lock file from the current schema without code generation.
/// </summary>
public class LockInitCommand : AsyncCommand<LockInitOptions>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, LockInitOptions settings,
        CancellationToken cancellation)
    {
        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        var lockPath = Path.Combine(currentDir.FullName, IonSchemaLock.FileName);

        if (!File.Exists(lockPath))
            return await LockUpdateCommand.RunCompileForLock(context, cancellation, updateLock: true);
        AnsiConsole.MarkupLine(
            $"[yellow]{IonSchemaLock.FileName} already exists. Use 'ionc lock update' to regenerate.[/]");
        return -1;
    }
}

/// <summary>
/// Validate current schema against existing lock file.
/// </summary>
public class LockCheckCommand : AsyncCommand<LockCheckOptions>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, LockCheckOptions settings,
        CancellationToken cancellation)
    {
        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        var lockPath = Path.Combine(currentDir.FullName, IonSchemaLock.FileName);

        if (!File.Exists(lockPath))
        {
            AnsiConsole.MarkupLine($"[yellow]{IonSchemaLock.FileName} not found. Run 'ionc lock init' first.[/]");
            return -1;
        }

        var options = new CompileOptions
        {
            CheckOnly = true,
            NoLock = false,
            UpdateLock = false
        };
        var cmd = new CompileCommand();
        return await cmd.DoExecuteAsync(context, options);
    }
}

/// <summary>
/// Force-update the lock file, acknowledging any breaking changes.
/// </summary>
public class LockUpdateCommand : AsyncCommand<LockUpdateOptions>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, LockUpdateOptions settings,
        CancellationToken cancellation)
    {
        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        return await RunCompileForLock(context, cancellation, updateLock: true);
    }

    public static async Task<int> RunCompileForLock(CommandContext context, CancellationToken cancellation,
        bool updateLock)
    {
        var options = new CompileOptions
        {
            CheckOnly = true,
            NoLock = false,
            UpdateLock = updateLock
        };
        var cmd = new CompileCommand();
        return await cmd.DoExecuteAsync(context, options);
    }
}