namespace ion.compiler.Commands;

using runtime;
using Spectre.Console;
using Spectre.Console.Cli;
using syntax;
using System.ComponentModel;
using System.Diagnostics;

public class CheckOptions : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Show detailed timing and stage information.")]
    public bool Verbose { get; set; }

    [CommandOption("--json")]
    [Description("Output diagnostics as JSON for CI/CD.")]
    public bool JsonOutput { get; set; }
}

/// <summary>
/// Parse + validate without code generation. Fast feedback loop.
/// </summary>
public class CheckCommand : AsyncCommand<CheckOptions>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CheckOptions settings, CancellationToken cancellation)
    {
        var compileOptions = new CompileOptions
        {
            CheckOnly = true,
            Verbose = settings.Verbose,
            JsonOutput = settings.JsonOutput,
            NoLock = false,
            UpdateLock = false
        };
        var cmd = new CompileCommand();
        return await cmd.DoExecuteAsync(context, compileOptions);
    }
}
