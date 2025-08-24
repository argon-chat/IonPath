namespace ion.compiler.Commands;

using CodeGen;
using runtime;
using Spectre.Console;
using Spectre.Console.Cli;
using syntax;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Text;

public class CompileOptions : CommandSettings
{
    [CommandOption("-n|--no-emit-csproj")] public bool NoEmitCsProj { get; set; }
}

public class CompileCommand : AsyncCommand<CompileOptions>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CompileOptions options)
    {
        try
        {
            return await DoExecuteAsync(context, options);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    public Task<int> DoExecuteAsync(CommandContext context, CompileOptions options)
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


        foreach (var (key, value) in project.Generators)
        {
            if (key is IonGeneratorPlatform.Go or IonGeneratorPlatform.Rust)
                throw new NotSupportedException($"Platform {key} currently is not support");
            

            if (key is IonGeneratorPlatform.Dotnet)
            {
                var cfg = value as DotnetGeneratorConfig;
                var generator = CreateGenerator(IonGeneratorPlatform.Dotnet, project.Name);


                if (!options.NoEmitCsProj)
                    generator.GenerateProjectFile(project.Name, projectFile.Directory!.File($"{project.Name}.csproj"));
                File.WriteAllText(projectFile.Directory!.Combine(cfg!.Outputs).File($"globals.cs").FullName, generator.GenerateGlobalTypes());

                GenerateDotNetDefault(generator, currentDir, project, ctx, projectFile.Directory!.Combine(cfg!.Outputs).Directory("models"), cfg);

                if (cfg.Features.Contains(DotnetFeature.Client))
                    GenerateClient(generator, currentDir, ctx, cfg);
                if (cfg.Features.Contains(DotnetFeature.Client))
                    GenerateServer(generator, currentDir, ctx, cfg);
            }

            if (key is IonGeneratorPlatform.Browser)
            {
                var cfg = value as BrowserGeneratorConfig;
                var gen = new IonTypeScriptGenerator(project.Name);
                GenerateBrowserClient(gen, currentDir, project, ctx, cfg);
            }
        }

        AnsiConsole.MarkupLine($"\n:sparkles: Done in [lime]{watch.Elapsed.TotalSeconds:00.000}s[/].");

        return Task.FromResult(0);
    }

    private void GenerateBrowserClient(IonTypeScriptGenerator generator, DirectoryInfo currentDir, IonProjectConfig project,
        CompilationContext context, BrowserGeneratorConfig cfg)
    {
        var outputFile = currentDir.File(cfg.OutputFile);

        if (outputFile.Exists)
            outputFile.Delete();

        var fileBuilder = new StringBuilder();

        fileBuilder.AppendLine(generator.FileHeader());

        fileBuilder.AppendLine(
            """
            import { 
              CborReader, 
              CborWriter, 
              
              DateOnly, 
              DateTimeOffset, 
              Duration, 
              TimeOnly, 
              Guid, 
              
              IonFormatterStorage,
            
              IonArray, 
              IonMaybe,
            
              IIonService,
              IIonUnion
            } from "./cbor";
            
            type guid = Guid;
            type timeonly = TimeOnly;
            type duration = Duration;
            type datetime = DateTimeOffset;
            type dateonly = DateOnly;
            
            declare type bool = boolean;
            
            declare type i1 = number;
            declare type i2 = number;
            declare type i4 = number;
            declare type i8 = bigint;
            declare type i16 = bigint;
            
            
            declare type u1 = number;
            declare type u2 = number;
            declare type u4 = number;
            declare type u8 = bigint;
            declare type u16 = bigint;
            
            
            declare type f2 = number;
            declare type f4 = number;
            declare type f8 = number;
            """);

        fileBuilder.AppendLine(generator.GenerateTypes(context.ProcessedModules.SelectMany(x => x.Definitions).DistinctBy(x => x.name.Identifier)));

        foreach (var module in context.ProcessedModules) 
            fileBuilder.AppendLine(generator.GenerateServices(module));

        //foreach (var module in context.ProcessedModules)
        //{
        //    fileBuilder.AppendLine(generator.GenerateAllFormatters(module.Definitions));
        //}




        File.WriteAllText(outputFile.FullName, fileBuilder.ToString());
    }

    private void GenerateDotNetDefault(IIonCodeGenerator generator, DirectoryInfo currentDir, IonProjectConfig project,
        CompilationContext context, DirectoryInfo outputFolder, DotnetGeneratorConfig cfg)
    {
        var outputDirectory = currentDir.Combine(outputFolder.FullName);

        if (!outputDirectory.Exists)
            outputDirectory.Create();

        foreach (var file in outputDirectory.EnumerateFiles("*.cs")) file.Delete();

        foreach (var module in context.ProcessedModules)
        {
            File.WriteAllText(outputDirectory.File($"{module.Name}.cs").FullName, generator.GenerateModule(module));
            File.WriteAllText(outputDirectory.File($"{module.Name}.formatters.cs").FullName,
                generator.GenerateAllFormatters(module.Definitions));
        }

        File.WriteAllText(outputDirectory.File($"moduleInit.cs").FullName,
            generator.GenerateModuleInit(
                context.ProcessedModules.SelectMany(x => x.Definitions).DistinctBy(x => x.name.Identifier),
                context.ProcessedModules.SelectMany(x => x.Services).DistinctBy(x => x.name.Identifier).ToList(),
                cfg.Features.Contains(DotnetFeature.Client),
                cfg.Features.Contains(DotnetFeature.Server)));
    }

    private void GenerateClient(IIonCodeGenerator generator, DirectoryInfo currentDir, CompilationContext context,
        DotnetGeneratorConfig generatorCfg)
    {
        var outputDirectory = currentDir.Combine(generatorCfg.Outputs).Combine("client");

        if (!outputDirectory.Exists)
            outputDirectory.Create();

        foreach (var module in context.ProcessedModules)
        {
            File.WriteAllText(outputDirectory.File($"{module.Name}.clientImpls.cs").FullName,
                generator.GenerateAllServiceClientImpl(module.Services));
        }
    }

    private void GenerateServer(IIonCodeGenerator generator, DirectoryInfo currentDir, CompilationContext context,
        DotnetGeneratorConfig generatorCfg)
    {
        var outputDirectory = currentDir.Combine(generatorCfg.Outputs).Combine("server");
        if (!outputDirectory.Exists)
            outputDirectory.Create();

        foreach (var module in context.ProcessedModules)
        {
            File.WriteAllText(outputDirectory.File($"{module.Name}.executors.cs").FullName,
                generator.GenerateAllServiceExecutors(module.Services));
        }
    }


    private static IIonCodeGenerator CreateGenerator(IonGeneratorPlatform platform, string @namespace)
    {
        if (platform is IonGeneratorPlatform.Dotnet)
            return new IonCSharpGenerator(@namespace);
        if (platform is IonGeneratorPlatform.Browser)
            return new IonTypeScriptGenerator(@namespace);
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