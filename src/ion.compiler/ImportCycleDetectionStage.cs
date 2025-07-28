namespace ion.compiler;

using syntax;

public sealed class ImportCycleDetectionStage(CompilationContext context)
    : CompilationStage(context)
{
    private enum VisitState { Unvisited, Visiting, Visited }

    public void Run(List<IonFileSyntax> modules)
    {
        var pathToModule = modules.ToDictionary(m => m.file.FullName);
        var state = new Dictionary<string, VisitState>();
        var stack = new Stack<string>();

        foreach (var module in modules)
        {
            var fullPath = module.file.FullName;
            if (!state.ContainsKey(fullPath))
                Dfs(fullPath, pathToModule, state, stack);
        }
    }

    private void Dfs(
        string currentPath,
        Dictionary<string, IonFileSyntax> modules,
        Dictionary<string, VisitState> state,
        Stack<string> stack
    )
    {
        state[currentPath] = VisitState.Visiting;
        stack.Push(currentPath);

        var current = modules[currentPath];

        foreach (var use in current.useSyntaxes)
        {
            var targetPath = use.Path;
            if (targetPath is null || !modules.ContainsKey(targetPath))
                continue;

            if (!state.TryGetValue(targetPath, out var visitState))
            {
                Dfs(targetPath, modules, state, stack);
            }
            else if (visitState is VisitState.Visiting)
            {
                var cycle = stack.Reverse().SkipWhile(x => x != targetPath).ToList();
                cycle.Add(targetPath);
                Error(IonAnalyticCodes.ION0001_CycleImportDetected, use, $"Import cycle: {string.Join(" → ", cycle)}");
            }
        }

        stack.Pop();
        state[currentPath] = VisitState.Visited;
    }
}