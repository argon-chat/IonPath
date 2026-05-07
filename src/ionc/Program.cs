using System.Globalization;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;
using System.Text;
using ion.compiler;
using ion.compiler.Commands;
using ion.compiler.Lsp;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;


// LSP serve mode: intercept before Spectre/Console setup
if (args.Length > 0 && args[0] == "serve")
{
    // Parse --stdio flag
    if (args.Contains("--stdio"))
    {
        var server = await IonLanguageServer.CreateAsync(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput());
        await server.WaitForShutdownAsync();
    }
    else
    {
        var port = 0;
        var portIdx = Array.IndexOf(args, "--port");
        if (portIdx >= 0 && portIdx + 1 < args.Length)
            int.TryParse(args[portIdx + 1], out port);

        var (server, _) = await IonLanguageServer.CreateTcpAsync(port);
        await server.WaitForShutdownAsync();
    }
    return 0;
}


Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    Console.OutputEncoding = Encoding.Unicode;


await Host.CreateDefaultBuilder(args)
    .ConfigureLogging(x => x.SetMinimumLevel(LogLevel.None))
    .UseConsoleLifetime()
    .UseSpectreConsole(config => {
        config.SetApplicationCulture(CultureInfo.InvariantCulture);
        config.SetApplicationName("ionc");

        config.AddCommand<CompileCommand>("compile").WithAlias("build");
        config.AddCommand<CheckCommand>("check");
        config.AddCommand<InitCommand>("init");
        config.AddCommand<ServeCommand>("serve").WithDescription("Start Language Server Protocol server");

        config.AddBranch("lock", @lock => {
            @lock.SetDescription("Manage the schema lock file (ion.lock.json)");
            @lock.AddCommand<LockInitCommand>("init").WithDescription("Generate initial lock file");
            @lock.AddCommand<LockCheckCommand>("check").WithDescription("Validate schema against lock");
            @lock.AddCommand<LockUpdateCommand>("update").WithDescription("Force-update lock file");
        });
    })
    .RunConsoleAsync();

return Environment.ExitCode;