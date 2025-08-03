namespace ion.compiler.Commands;

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


        new DuplicateSymbolValidationStage(ctx).DoProcess();
        Checks(ctx);
        new TransformStage(ctx).DoProcess();
        Checks(ctx);
        new RestoreUnresolvedTypeStage(ctx).DoProcess();
        Checks(ctx);

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