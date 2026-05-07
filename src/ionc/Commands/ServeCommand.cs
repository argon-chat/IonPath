namespace ion.compiler.Commands;

using Spectre.Console.Cli;
using System.ComponentModel;

public class ServeOptions : CommandSettings
{
    [CommandOption("--port")]
    [Description("TCP port to listen on. Use 0 for auto-assign (default).")]
    public int Port { get; set; } = 0;

    [CommandOption("--stdio")]
    [Description("Use stdin/stdout instead of TCP.")]
    public bool UseStdio { get; set; } = false;
}

public class ServeCommand : AsyncCommand<ServeOptions>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ServeOptions settings, CancellationToken cancellation)
    {
        if (settings.UseStdio)
        {
            var server = await Lsp.IonLanguageServer.CreateAsync(
                Console.OpenStandardInput(), Console.OpenStandardOutput());
            await server.WaitForShutdownAsync();
        }
        else
        {
            var (server, port) = await Lsp.IonLanguageServer.CreateTcpAsync(settings.Port);
            await server.WaitForShutdownAsync();
        }
        return 0;
    }
}
