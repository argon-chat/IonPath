using System.Globalization;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;
using System.Text;
using ion.compiler;
using ion.compiler.Commands;
using ion.runtime;
using ion.runtime.locking;
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
    })
    .RunConsoleAsync();

return Environment.ExitCode;