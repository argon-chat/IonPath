using System.Globalization;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;
using System.Text;
using ion.compiler;
using ion.compiler.Commands;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;


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

        config.AddBranch("lock", @lock => {
            @lock.SetDescription("Manage the schema lock file (ion.lock.json)");
            @lock.AddCommand<LockInitCommand>("init").WithDescription("Generate initial lock file");
            @lock.AddCommand<LockCheckCommand>("check").WithDescription("Validate schema against lock");
            @lock.AddCommand<LockUpdateCommand>("update").WithDescription("Force-update lock file");
        });
    })
    .RunConsoleAsync();

return Environment.ExitCode;