namespace ion.compiler.Commands;

using runtime;
using Spectre.Console;
using Spectre.Console.Cli;
using syntax;
using System.ComponentModel;
using System.Text.Json;
using CodeGen;

public class InitOptions : CommandSettings
{
    [CommandArgument(0, "[name]")]
    [Description("Project name. If omitted, the current directory name is used.")]
    public string? Name { get; set; }
}

/// <summary>
/// Scaffolds a new ion project with ion.config.json and an example .ion file.
/// </summary>
public class InitCommand : Command<InitOptions>
{
    protected override int Execute(CommandContext context, InitOptions settings, CancellationToken cancellation)
    {
        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());

        var existingConfig = currentDir.File("ion.config.json");
        if (existingConfig.Exists)
        {
            AnsiConsole.MarkupLine("[yellow]ion.config.json already exists in this directory.[/]");
            return -1;
        }

        var name = settings.Name ?? currentDir.Name;

        // Interactive feature selection
        var features = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Select [cyan]features[/]:")
                .Required()
                .AddChoices("std", "orleans")
                .Select("std"));

        // Interactive platform selection
        var platforms = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Select [cyan]target platforms[/]:")
                .Required()
                .AddChoices("dotnet", "browser", "rust")
                .Select("dotnet"));

        // Build generators config
        var generators = new Dictionary<string, object>();

        foreach (var platform in platforms)
        {
            switch (platform)
            {
                case "dotnet":
                    var dotnetFeatures = AnsiConsole.Prompt(
                        new MultiSelectionPrompt<string>()
                            .Title($"Select [cyan]dotnet[/] features:")
                            .AddChoices("models", "client", "server")
                            .Select("models"));
                    generators["dotnet"] = new
                    {
                        features = dotnetFeatures,
                        outputs = "./"
                    };
                    break;
                case "browser":
                    generators["browser"] = new
                    {
                        outputFile = $"./{name}.generated.ts"
                    };
                    break;
                // Only 'models' and 'client' are offered: RustFeature has no Server member, and
                // the Rust generator emits no server surface.
                case "rust":
                    var rustFeatures = AnsiConsole.Prompt(
                        new MultiSelectionPrompt<string>()
                            .Title($"Select [cyan]rust[/] features:")
                            .AddChoices("models", "client")
                            .Select("models"));
                    generators["rust"] = new
                    {
                        features = rustFeatures,
                        outputs = "./gen-rust",
                        // A crate name is a Cargo package name: '.' is not allowed, '-' is.
                        crateName = name.ToLowerInvariant().Replace(".", "-"),
                        // Points at the ion.rustcore crate the generated code depends on. The
                        // scaffold cannot know where it lives, so this is a placeholder the
                        // author edits — the same relative-path shape the repo's own fixture uses.
                        rustcorePath = "../packages/ion.rustcore"
                    };
                    break;
            }
        }

        var config = new
        {
            name,
            features,
            generators
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(existingConfig.FullName, json);

        AnsiConsole.MarkupLine($"[green]✓[/] Created [cyan]ion.config.json[/]");

        // Create example .ion file
        var exampleFile = currentDir.File("example.ion");
        if (!exampleFile.Exists)
        {
            File.WriteAllText(exampleFile.FullName,
                """
                msg HelloRequest {
                    name: string;
                }

                msg HelloResponse {
                    message: string;
                }

                service Greeter() {
                    SayHello(request: HelloRequest): HelloResponse;
                }
                """);
            AnsiConsole.MarkupLine($"[green]✓[/] Created [cyan]example.ion[/]");
        }

        AnsiConsole.MarkupLine($"\n[green]:sparkles: Project '{name}' initialized![/]");
        AnsiConsole.MarkupLine("[dim]Run 'ionc compile' to generate code.[/]");

        return 0;
    }
}
