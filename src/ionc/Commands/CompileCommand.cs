namespace ion.compiler.Commands;

using CodeGen;
using Microsoft.Build.Framework;
using runtime;
using Spectre.Console.Cli;
using syntax;
using System.Collections.Generic;
using System.Diagnostics;
using Spectre.Console;

public class CompileCommand : AsyncCommand
{
    public override Task<int> ExecuteAsync(CommandContext context)
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

        var files = currentDir.EnumerateFiles("*.ion").ToList();

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

        


        foreach (var generatorCfg in project.Generators)
        {
            if (generatorCfg.Platform is IonGeneratorPlatform.Go or IonGeneratorPlatform.Rust)
                throw new NotSupportedException($"Platform {generatorCfg.Platform} currently is not support");

            if (generatorCfg.Type == IonGeneratorType.Server)
                GenerateServer(currentDir, project, ctx, generatorCfg);
            if (generatorCfg.Type == IonGeneratorType.Client)
                GenerateClient(currentDir, project, ctx, generatorCfg);
        }

        AnsiConsole.MarkupLine($"\n:sparkles: Done in [lime]{watch.Elapsed.TotalSeconds:00.000}s[/].");

        return Task.FromResult(0);
    }

    private void GenerateClient(DirectoryInfo currentDir, IonProjectConfig project, CompilationContext ctx, IonGeneratorConfig generatorCfg)
    {
        throw new NotImplementedException();
    }

    private void GenerateServer(DirectoryInfo currentDir, IonProjectConfig project, CompilationContext context, IonGeneratorConfig generatorCfg)
    {
        var outputDirectory = currentDir.Combine(generatorCfg.Output);

        if (!outputDirectory.Exists)
            outputDirectory.Create();

        var generator = new IonCSharpGenerator(project.Name);

        foreach (var file in outputDirectory.EnumerateFiles("*.cs")) file.Delete();

        IonCSharpGenerator.GenerateCsproj(project.Name, outputDirectory.File($"{project.Name}.csproj"));
        File.WriteAllText(outputDirectory.File($"globals.cs").FullName, IonCSharpGenerator.GenerateGlobalTypes());

        foreach (var module in context.ProcessedModules)
        {
            File.WriteAllText(outputDirectory.File($"{module.Name}.cs").FullName, generator.GenerateModule(module));
            File.WriteAllText(outputDirectory.File($"{module.Name}.formatters.cs").FullName, generator.GenerateAllFormatters(module.Definitions));
            File.WriteAllText(outputDirectory.File($"{module.Name}.executors.cs").FullName, generator.GenerateAllServiceExecutors(module.Services));
        }

        File.WriteAllText(outputDirectory.File($"moduleInit.cs").FullName,
            generator.GenerateModuleInit(context.ProcessedModules.SelectMany(x => x.Definitions).DistinctBy(x => x.name.Identifier), context.ProcessedModules.SelectMany(x => x.Services).DistinctBy(x => x.name.Identifier)));
    }


    private static void Checks(CompilationContext ctx)
    {
        if (!ctx.HasErrors) return;
        IonDiagnosticRenderer.RenderDiagnostics(ctx.Diagnostics);
        Environment.Exit(-1);
        return;
    }
}