namespace ion.compiler.Commands;

using CodeGen;
using runtime;
using Spectre.Console.Cli;
using syntax;
using System.Collections.Generic;

public class CompileCommand : AsyncCommand
{
    public override Task<int> ExecuteAsync(CommandContext context)
    {
        var currentDir = new DirectoryInfo("./project");

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
        var ctx = CompilationContext.Create(["std"], list);

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

        var generator = new IonCSharpGenerator("TestIon");


        var t = generator.GenerateModule(ctx.ProcessedModules.First());


        var rwe = generator.GenerateAllFormatters(ctx.ProcessedModules.First().Definitions);

        var baseFolder = new DirectoryInfo(@"Z:\!argon\IonPath\src\testProject");


       //IonCSharpGenerator.GenerateCsproj("ionTestProject", baseFolder.File("ionTestProject.csproj"));
        //File.WriteAllText(baseFolder.File($"globals.cs").FullName, IonCSharpGenerator.GenerateGlobalTypes());

        foreach (var module in ctx.ProcessedModules)
        {
            File.WriteAllText(baseFolder.File($"{module.Name}.cs").FullName, generator.GenerateModule(module));
            File.WriteAllText(baseFolder.File($"{module.Name}.formatters.cs").FullName, generator.GenerateAllFormatters(module.Definitions));
        }

        return Task.FromResult(0);
    }


    private static void Checks(CompilationContext ctx)
    {
        if (!ctx.HasErrors) return;
        IonDiagnosticRenderer.RenderDiagnostics(ctx.Diagnostics);
        Environment.Exit(-1);
        return;
    }
}