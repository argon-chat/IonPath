namespace ion.compiler.Commands;

using CodeGen;
using runtime;
using Spectre.Console;
using Spectre.Console.Cli;
using syntax;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

public class CompileOptions : CommandSettings
{
    [CommandOption("-n|--no-emit-csproj")]
    public bool NoEmitCsProj { get; set; }
}

public class CompileCommand : AsyncCommand<CompileOptions>
{
    public override Task<int> ExecuteAsync(CommandContext context, CompileOptions options)
    {
        var watch = Stopwatch.StartNew();
        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());

        var projectFile = currentDir.File("ion.config.json");
        if (!projectFile.Exists)
        {
            IonDiagnosticRenderer.RenderDiagnostics([
                new IonDiagnostic("ION", IonDiagnosticSeverity.Error,
                    "Project 'ion.config.json' not found in current directory.", new IonSyntaxBase())
            ]);
            return Task.FromResult(-1);
        }

        var project = IonProjectConfig.FromJson(File.ReadAllText(projectFile.FullName));

        var files = currentDir.EnumerateFiles("*.ion", SearchOption.AllDirectories).ToList();


        if (!files.Any())
        {
            IonDiagnosticRenderer.RenderDiagnostics([
                new IonDiagnostic("ION", IonDiagnosticSeverity.Error,
                    "Project 'ion.config.json' found, but no any *.ion files found.", new IonSyntaxBase())
            ]);
            return Task.FromResult(-1);
        }

        var list = new List<IonFileSyntax>();
        foreach (var file in files)
        {
            using var _ = IonFileProcessingScope.Begin(file);

            try
            {
                var syntax = IonParser.Parse(file.Name, File.ReadAllText(file.FullName));
                list.Add(syntax);
            }
            catch (ParseException e)
            {
                IonDiagnosticRenderer.RenderParseError(e.Error, file);
            }

        }
        var ctx = CompilationContext.Create(project.Features.Select(x => x.ToString().ToLowerInvariant()).ToList(), list);

        new VerifyInvalidStatementsStage(ctx).DoProcess();
        Checks(ctx);
        new DuplicateSymbolValidationStage(ctx).DoProcess();
        Checks(ctx);
        new TransformStage(ctx).DoProcess();
        Checks(ctx);
        new RestoreUnresolvedTypeStage(ctx).DoProcess();
        Checks(ctx);
        var graph = new IonDependencyGraph(ctx.ProcessedModules.Concat(ctx.GlobalModules));
        graph.Generate();

        var generator = CreateGenerator(IonGeneratorPlatform.Dotnet, project.Name);


        if (!options.NoEmitCsProj)
            generator.GenerateProjectFile(project.Name, projectFile.Directory!.File($"{project.Name}.csproj"));
        File.WriteAllText(projectFile.Directory!.File($"globals.cs").FullName, generator.GenerateGlobalTypes());

        GenerateDefault(currentDir, project, ctx, projectFile.Directory!.Directory("models"));

        foreach (var generatorCfg in project.Generators)
        {
            if (generatorCfg.Platform is IonGeneratorPlatform.Go or IonGeneratorPlatform.Rust)
                throw new NotSupportedException($"Platform {generatorCfg.Platform} currently is not support");
            if (generatorCfg.Type == IonGeneratorType.Server)
                GenerateServer(generator, currentDir, ctx, generatorCfg);
            if (generatorCfg.Type == IonGeneratorType.Client)
                GenerateClient(generator, currentDir, ctx, generatorCfg);
        }

        AnsiConsole.MarkupLine($"\n:sparkles: Done in [lime]{watch.Elapsed.TotalSeconds:00.000}s[/].");

        return Task.FromResult(0);
    }

    private void GenerateDefault(DirectoryInfo currentDir, IonProjectConfig project, CompilationContext context, DirectoryInfo outputFolder)
    {
        var outputDirectory = currentDir.Combine(outputFolder.FullName);

        if (!outputDirectory.Exists)
            outputDirectory.Create();

        var generator = new IonCSharpGenerator(project.Name);

        foreach (var file in outputDirectory.EnumerateFiles("*.cs")) file.Delete();

        foreach (var module in context.ProcessedModules)
        {
            File.WriteAllText(outputDirectory.File($"{module.Name}.cs").FullName, generator.GenerateModule(module));
            File.WriteAllText(outputDirectory.File($"{module.Name}.formatters.cs").FullName, generator.GenerateAllFormatters(module.Definitions));
        }

        File.WriteAllText(outputDirectory.File($"moduleInit.cs").FullName,
            generator.GenerateModuleInit(context.ProcessedModules.SelectMany(x => x.Definitions).DistinctBy(x => x.name.Identifier), 
                context.ProcessedModules.SelectMany(x => x.Services).DistinctBy(x => x.name.Identifier).ToList(),
                project.Generators.Any(x => x.Type == IonGeneratorType.Client),
                project.Generators.Any(x => x.Type == IonGeneratorType.Server)));
    }

    private void GenerateClient(IIonCodeGenerator generator, DirectoryInfo currentDir, CompilationContext context, IonGeneratorConfig generatorCfg)
    {
        var outputDirectory = currentDir.Combine(generatorCfg.Output);

        if (!outputDirectory.Exists)
            outputDirectory.Create();

        foreach (var module in context.ProcessedModules)
        {
            File.WriteAllText(outputDirectory.File($"{module.Name}.clientImpls.cs").FullName, generator.GenerateAllServiceClientImpl(module.Services));
        }
    }

    private void GenerateServer(IIonCodeGenerator generator, DirectoryInfo currentDir, CompilationContext context, IonGeneratorConfig generatorCfg)
    {
        var outputDirectory = currentDir.Combine(generatorCfg.Output);
        if (!outputDirectory.Exists)
            outputDirectory.Create();

        foreach (var module in context.ProcessedModules)
        {
            File.WriteAllText(outputDirectory.File($"{module.Name}.executors.cs").FullName, generator.GenerateAllServiceExecutors(module.Services));
        }
    }


    private static IIonCodeGenerator CreateGenerator(IonGeneratorPlatform platform, string @namespace)
    {
        if (platform is IonGeneratorPlatform.Dotnet)
            return new IonCSharpGenerator(@namespace);

        throw new InvalidOperationException();
    }

    private static void Checks(CompilationContext ctx)
    {
        if (!ctx.HasErrors) return;
        IonDiagnosticRenderer.RenderDiagnostics(ctx.Diagnostics);
        Environment.Exit(-1);
        return;
    }
}