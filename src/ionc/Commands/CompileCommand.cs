namespace ion.compiler.Commands;

using CodeGen;
using runtime;
using Spectre.Console;
using Spectre.Console.Cli;
using syntax;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;

public class CompileOptions : CommandSettings
{
    [CommandOption("-n|--no-emit-csproj")] public bool NoEmitCsProj { get; set; }

    [CommandOption("-o|--only")] public string OnlyTarget { get; set; }

    [CommandOption("--maybe")] public bool UseMaybeWrapper { get; set; }

    [CommandOption("--update-lock")]
    [Description("Force-update the lock file, acknowledging breaking changes.")]
    public bool UpdateLock { get; set; }

    [CommandOption("--no-lock")]
    [Description("Skip schema lock validation entirely.")]
    public bool NoLock { get; set; }

    [CommandOption("--check")]
    [Description("Validate only — no code generation.")]
    public bool CheckOnly { get; set; }

    [CommandOption("-v|--verbose")]
    [Description("Show detailed timing and stage information.")]
    public bool Verbose { get; set; }

    [CommandOption("--json")]
    [Description("Output diagnostics as JSON for CI/CD.")]
    public bool JsonOutput { get; set; }
}

public class CompileCommand : AsyncCommand<CompileOptions>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CompileOptions options, CancellationToken cancellation)
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

        IonProjectConfig project;
        try
        {
            project = IonProjectConfig.FromJson(File.ReadAllText(projectFile.FullName));
        }
        catch (Exception e) when (e is JsonException or ValidationException)
        {
            // A retired or misspelled generator key ('go' was removed) reaches here as a
            // JsonException from PlatformKeyConverter. Rendering it as a diagnostic keeps the
            // failure readable instead of dumping a deserializer stack trace.
            IonDiagnosticRenderer.RenderDiagnostics([
                new IonDiagnostic("ION", IonDiagnosticSeverity.Error,
                    $"Project 'ion.config.json' is not valid: {e.Message}", new IonSyntaxBase())
            ]);
            return Task.FromResult(-1);
        }

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
        var parseErrors = new List<(FileInfo file, ParseException error)>();

        // Blocks the parser could not understand but recovered past. `IonParser.Parse` only throws
        // when even error recovery fails, so without this the CLI silently ignores unparseable
        // source: the editor shows red squiggles while `ionc check` reports success.
        // Mirrors IonWorkspace.PublishDiagnostics.
        var invalidBlocks = new List<IonDiagnostic>();

        foreach (var file in files)
        {
            using var _ = IonFileProcessingScope.Begin(file);

            try
            {
                var syntax = IonParser.Parse(file.Name, File.ReadAllText(file.FullName));
                list.Add(syntax);

                foreach (var token in (syntax.allTokens ?? []).OfType<InvalidIonBlock>())
                    invalidBlocks.Add(new IonDiagnostic(
                        "ION_PARSE",
                        IonDiagnosticSeverity.Error,
                        $"Unexpected syntax: {token.block.Trim().Split('\n')[0]}",
                        token));
            }
            catch (ParseException e)
            {
                parseErrors.Add((file, e));
                AnsiConsole.MarkupLine($"[red]Error:[/] Failed to parse file [cyan]{file.Name}[/]: {e.Message.EscapeMarkup()}");
            }
        }

        // If all files failed to parse, exit early
        if (list.Count == 0)
        {
            IonDiagnosticRenderer.RenderDiagnostics([
                new IonDiagnostic("ION", IonDiagnosticSeverity.Error,
                    "No files were successfully parsed. Cannot proceed with compilation.", new IonSyntaxBase())
            ]);
            return Task.FromResult(-1);
        }
        
        // If some files had parse errors, show warning
        if (parseErrors.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] {parseErrors.Count} file(s) failed to parse and will be skipped.\n");
        }

        var ctx = CompilationContext.Create(project.Features.Select(x => x.ToString().ToLowerInvariant()).ToList(),
            list);

        ctx.Diagnostics.AddRange(invalidBlocks);

        // Resolve external module dependencies
        List<IonModule> externalModules = [];
        if (project.Modules is { Count: > 0 })
        {
            var resolver = new ModuleResolver();
            var moduleResult = resolver.Resolve(currentDir.FullName, project.Modules);

            foreach (var diag in moduleResult.Diagnostics)
                ctx.Diagnostics.Add(diag);

            foreach (var resolved in resolver.GetTopologicalOrder())
            {
                var modFeatures = resolved.Features.ToList();
                var modCtx = CompilationContext.Create(modFeatures, resolved.Files);
                var modPipeline = new CompilationPipeline(modCtx);
                modPipeline.Execute();

                foreach (var mod in modCtx.ProcessedModules)
                {
                    var extMod = new IonModule
                    {
                        Name = mod.Name,
                        Path = mod.Path,
                        Definitions = mod.Definitions,
                        Services = mod.Services,
                        Features = mod.Features,
                        Attributes = mod.Attributes,
                        Imports = mod.Imports,
                        Syntax = mod.Syntax,
                        SourceModule = resolved.Name
                    };
                    externalModules.Add(extMod);
                    ctx.ExternalModules.Add(extMod);
                }
            }

            if (externalModules.Count > 0 && options.Verbose)
                AnsiConsole.MarkupLine($"[dim]Resolved {externalModules.Count} external module(s)[/]");
        }

        // Load existing lock file (if present and not disabled)
        IonSchemaLock? existingLock = null;
        if (!options.NoLock && !options.UpdateLock)
        {
            existingLock = IonSchemaLock.TryLoadFrom(currentDir.FullName);
            if (existingLock is not null && options.Verbose)
                AnsiConsole.MarkupLine("[dim]Loaded ion.lock.json for schema validation[/]");
        }

        // Execute compilation pipeline with live progress bar
        AnsiConsole.MarkupLine("[bold cyan]Compilation Pipeline[/]\n");

        var pipelineSuccess = false;

        AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn()
            )
            .Start(progressCtx =>
            {
                var progress = new SpectreCompilationProgressWithContext(progressCtx);
                var pipeline = new CompilationPipeline(ctx, progress, existingLock);
                pipelineSuccess = pipeline.Execute();
            });

        AnsiConsole.WriteLine();

        // Render whenever there is anything to say, not only on failure. `pipeline.Execute()` fails
        // on errors alone, so gating the renderer on it discarded every warning and info diagnostic
        // of a successful build — ION1001/ION1002 (unused), ION0025/ION0029 (lock compatibility),
        // ION0047 (#use deprecated) and ION1004 (deprecated usage) were unreachable in the CLI even
        // though the LSP surfaced them. Exit code still keys off errors only.
        if (ctx.Diagnostics.Count > 0)
        {
            if (options.JsonOutput)
                RenderDiagnosticsAsJson(ctx.Diagnostics);
            else
                IonDiagnosticRenderer.RenderDiagnostics(ctx.Diagnostics);
        }

        if (!pipelineSuccess)
            return Task.FromResult(-1);

        // Check-only mode: validate, generate lock if needed, but no code gen
        if (options.CheckOnly)
        {
            if (!options.NoLock)
                WriteLockFile(currentDir, project.Name, ctx);
            AnsiConsole.MarkupLine($"\n[green]:sparkles: Check passed in {watch.Elapsed.TotalSeconds:0.000}s[/]");
            return Task.FromResult(0);
        }

        // Build dependency graph
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("[cyan]Building dependency graph...[/]", statusCtx =>
            {
                var graph = new IonDependencyGraph(ctx.ProcessedModules.Concat(ctx.GlobalModules));
                graph.Generate();
            });

        AnsiConsole.MarkupLine("[green]✓[/] Dependency graph complete\n");

        // Code generation
        AnsiConsole.MarkupLine("[bold cyan]Code Generation[/]\n");

        // A generator may reject a construct its target cannot express (see
        // IonCodeGenDiagnostics). Those diagnostics land in the same list the pipeline uses,
        // so remember where the pipeline's own end.
        var diagnosticsBeforeCodegen = ctx.Diagnostics.Count;

        foreach (var (key, value) in project.Generators)
        {
            if (!string.IsNullOrEmpty(options.OnlyTarget))
            {
                if (!options.OnlyTarget.Equals(key.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.MarkupLine($"  [dim]Skipping {key} (--only={options.OnlyTarget})[/]");
                    continue;
                }
            }

            AnsiConsole.MarkupLine($"  [lime]→[/] Generating [cyan]{key}[/] code...");

            if (key is IonGeneratorPlatform.Dotnet)
            {
                var cfg = value as DotnetGeneratorConfig;
                var generator = CreateGenerator(IonGeneratorPlatform.Dotnet, project.Name);
                var outputDirectoryForFiles = new DirectoryInfo(projectFile.Directory!.Combine(cfg!.Outputs).FullName);

                if (!options.NoEmitCsProj)
                    generator.GenerateProjectFile(project.Name, outputDirectoryForFiles.File($"{project.Name}.csproj"));

                // Patch csproj with module ProjectReferences (edit, not overwrite)
                if (project.Modules is { Count: > 0 })
                {
                    var csprojPath = outputDirectoryForFiles.File($"{project.Name}.csproj").FullName;
                    if (File.Exists(csprojPath))
                    {
                        var moduleRefs = BuildModuleProjectReferences(project.Modules, outputDirectoryForFiles, cfg);
                        if (CodeGen.CsprojModulePatcher.EnsureProjectReferences(csprojPath, moduleRefs))
                            AnsiConsole.MarkupLine($"    [dim]Patched {project.Name}.csproj with module references[/]");
                    }
                }

                File.WriteAllText(outputDirectoryForFiles.File($"globals.cs").FullName,
                    generator.GenerateGlobalTypes());

                GenerateDotNetDefault(generator, currentDir, project, ctx, outputDirectoryForFiles.Directory("models"),
                    cfg);

                if (cfg.Features.Contains(DotnetFeature.Client))
                    GenerateClient(generator, outputDirectoryForFiles, ctx);
                if (cfg.Features.Contains(DotnetFeature.Server))
                    GenerateServer(generator, outputDirectoryForFiles, ctx);

                AnsiConsole.MarkupLine($"    [green]✓[/] Generated to [dim]{cfg.Outputs}[/]");
            }

            if (key is IonGeneratorPlatform.Browser)
            {
                var cfg = value as BrowserGeneratorConfig;
                var gen = new IonTypeScriptGenerator(project.Name);
                GenerateBrowserClient(gen, currentDir, project, ctx, cfg, externalModules);
                AnsiConsole.MarkupLine($"    [green]✓[/] Generated to [dim]{cfg.OutputFile}[/]");
            }

            if (key is IonGeneratorPlatform.Rust)
            {
                var cfg = value as RustGeneratorConfig;
                var generator = new RustCodeGenerator(project.Name);
                var outputDirectoryForFiles = new DirectoryInfo(projectFile.Directory!.Combine(cfg!.Outputs).FullName);

                if (!outputDirectoryForFiles.Exists)
                    outputDirectoryForFiles.Create();

                // Clean old .rs files
                foreach (var file in outputDirectoryForFiles.EnumerateFiles("*.rs"))
                    file.Delete();

                // Generate Cargo.toml
                var crateName = cfg.CrateName ?? project.Name.ToLowerInvariant().Replace(".", "-");
                generator.GenerateProjectFile(crateName, outputDirectoryForFiles.File("Cargo.toml"), cfg.RustcorePath);

                // Generate single file with types, formatters, and clients
                var rustContent = generator.GenerateSingleFile(ctx);

                // Empty means the generator refused the schema (see IonCodeGenDiagnostics);
                // leaving no .rs behind is better than leaving one that does not compile.
                if (rustContent.Length == 0)
                {
                    AnsiConsole.MarkupLine("    [red]✗[/] Skipped: the target cannot express this schema");
                }
                else
                {
                    var srcDir = outputDirectoryForFiles.Directory("src");
                    if (!srcDir.Exists)
                        srcDir.Create();

                    File.WriteAllText(srcDir.File("lib.rs").FullName, rustContent);
                    AnsiConsole.MarkupLine($"    [green]✓[/] Generated to [dim]{cfg.Outputs}[/]");
                }
            }
        }

        // Diagnostics raised by a generator (target capability limits) fail the build the same
        // way a pipeline error does — and before the lock file is rewritten from a schema one
        // of the targets could not emit.
        var codegenDiagnostics = ctx.Diagnostics.Skip(diagnosticsBeforeCodegen).ToList();
        if (codegenDiagnostics.Any(d => d.Severity == IonDiagnosticSeverity.Error))
        {
            AnsiConsole.WriteLine();
            if (options.JsonOutput)
                RenderDiagnosticsAsJson(codegenDiagnostics);
            else
                IonDiagnosticRenderer.RenderDiagnostics(codegenDiagnostics);
            return Task.FromResult(-1);
        }

        // Write lock file after successful code generation
        if (!options.NoLock)
            WriteLockFile(currentDir, project.Name, ctx);

        AnsiConsole.MarkupLine($"\n[green]:sparkles: Done in {watch.Elapsed.TotalSeconds:0.000}s[/]");

        return Task.FromResult(0);
    }

    private static void WriteLockFile(DirectoryInfo projectDir, string moduleName, CompilationContext ctx)
    {
        var lockFile = SchemaLockGenerator.Generate(moduleName, ctx.ProcessedModules);
        lockFile.SaveTo(projectDir.FullName);
        AnsiConsole.MarkupLine($"\n  [green]✓[/] Updated [dim]{IonSchemaLock.FileName}[/]");
    }

    private static void RenderDiagnosticsAsJson(List<IonDiagnostic> diagnostics)
    {
        var items = diagnostics.Select(d => new
        {
            code = d.Code,
            severity = d.Severity.ToString().ToLowerInvariant(),
            message = d.Message,
            file = d.SourceFile?.FullName,
            line = d.StartPosition.Line,
            col = d.StartPosition.Col
        });
        Console.WriteLine(JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void GenerateBrowserClient(IonTypeScriptGenerator generator, DirectoryInfo currentDir,
        IonProjectConfig project,
        CompilationContext context, BrowserGeneratorConfig cfg, List<IonModule> externalModules)
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
              IonDateTime, 
              IonDecimal, 
              Duration, 
              TimeOnly, 
              Guid, 
              
              IonFormatterStorage,

              IonArray,
              IonMaybe,
              IonPartial,

              IIonService,
              IIonUnion,
              
              ServiceExecutor,
              IonClientContext,
              IonRequest,
              IonWsClient,
              IonInterceptor
            } from "@argon-chat/ion.webcore";

            type guid = Guid;
            type timeonly = TimeOnly;
            type duration = Duration;
            // IonDateTime, never the deprecated `DateTimeOffset { date: Date; offsetMinutes }`
            // shape: `Date` is millisecond-resolution, so it cannot hold the 100ns ticks the
            // wire form carries, and the webcore "datetime" formatter now reads and writes
            // IonDateTime — leaving the old alias here would be a live type mismatch, not just
            // a lossy one.
            type datetime = IonDateTime;
            type decimal = IonDecimal;
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

        // Collect all definitions and services — local project first
        var allDefinitions = context.ProcessedModules
            .SelectMany(x => x.Definitions)
            .ToList();
        var allServices = context.ProcessedModules
            .SelectMany(x => x.Services)
            .ToList();

        // When singleFileOutput is enabled, merge external module types into the same file
        if (cfg.SingleFileOutput && externalModules.Count > 0)
        {
            allDefinitions.AddRange(externalModules.SelectMany(m => m.Definitions));
            allServices.AddRange(externalModules.SelectMany(m => m.Services));
        }

        var distinctDefsList = allDefinitions.DistinctBy(x => x.name.Identifier).ToList();
        var distinctServices = allServices.DistinctBy(x => x.name.Identifier).ToList();

        fileBuilder.AppendLine(generator.GenerateTypes(distinctDefsList));
        fileBuilder.AppendLine(generator.GenerateAllFormatters(distinctDefsList));

        // Partial<T> schemas. Emitted after the ordinary formatters, but order is irrelevant:
        // registerPartial resolves each field's formatter lazily. Services are passed too,
        // because a `T~` can appear only in a method argument or return type.
        fileBuilder.AppendLine(generator.GeneratePartialRegistrations(distinctDefsList, distinctServices));

        foreach (var module in context.ProcessedModules)
            fileBuilder.AppendLine(generator.GenerateServices(module));

        // When singleFileOutput, also generate services from external modules
        if (cfg.SingleFileOutput && externalModules.Count > 0)
        {
            foreach (var extModule in externalModules)
            {
                if (extModule.Services.Count > 0)
                    fileBuilder.AppendLine(generator.GenerateServices(extModule));
            }
        }

        fileBuilder.AppendLine(generator.GenerateAllServiceClientImpl(distinctServices));
        fileBuilder.AppendLine(generator.GenerateClientProxy(distinctServices));

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

    private void GenerateClient(IIonCodeGenerator generator, DirectoryInfo currentDir, CompilationContext context)
    {
        var outputDirectory = currentDir.Directory("client");

        if (!outputDirectory.Exists)
            outputDirectory.Create();

        foreach (var module in context.ProcessedModules)
        {
            File.WriteAllText(outputDirectory.File($"{module.Name}.clientImpls.cs").FullName,
                generator.GenerateAllServiceClientImpl(module.Services));
        }
    }

    private void GenerateServer(IIonCodeGenerator generator, DirectoryInfo currentDir, CompilationContext context)
    {
        var outputDirectory = currentDir.Directory("server");
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

    /// <summary>
    /// Builds the list of ProjectReference entries for external modules.
    /// Calculates relative paths from the output directory to each module's generated csproj.
    /// </summary>
    private static List<CodeGen.ModuleProjectReference> BuildModuleProjectReferences(
        Dictionary<string, string> modules,
        DirectoryInfo outputDir,
        DotnetGeneratorConfig cfg)
    {
        var refs = new List<CodeGen.ModuleProjectReference>();

        foreach (var (moduleName, modulePath) in modules)
        {
            // Each module generates its own csproj at its own outputs path.
            // We need to find the module's ion.config.json to determine its output dir and project name.
            var moduleRoot = Path.GetFullPath(Path.Combine(outputDir.FullName, "..", modulePath));
            var moduleConfigPath = Path.Combine(moduleRoot, "ion.config.json");

            if (!File.Exists(moduleConfigPath))
                continue;

            try
            {
                var moduleConfig = IonProjectConfig.FromJson(File.ReadAllText(moduleConfigPath));

                // Find the module's dotnet output path
                if (!moduleConfig.Generators.TryGetValue(IonGeneratorPlatform.Dotnet, out var modPlatformCfg))
                    continue;

                var modDotnetCfg = modPlatformCfg as DotnetGeneratorConfig;
                if (modDotnetCfg is null)
                    continue;

                var moduleCsprojDir = Path.GetFullPath(Path.Combine(moduleRoot, modDotnetCfg.Outputs));
                var moduleCsprojPath = Path.Combine(moduleCsprojDir, $"{moduleConfig.Name}.csproj");

                // Calculate relative path from our output dir to the module's csproj
                var relativePath = Path.GetRelativePath(outputDir.FullName, moduleCsprojPath);

                refs.Add(new CodeGen.ModuleProjectReference(moduleName, relativePath));
            }
            catch
            {
                // Skip modules with invalid configs
            }
        }

        return refs;
    }
}