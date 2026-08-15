namespace ion.compiler;

using ion.runtime;
using syntax;

/// <summary>
/// Reports a cycle in the <em>import</em> graph of the project's modules (ION0001).
/// </summary>
/// <remarks>
/// <para>
/// <c>DoProcess</c> used to <c>throw new NotImplementedException()</c> and the stage was never
/// registered, so ION0001 could not be produced by any code path. The <c>Run</c> overload that did
/// exist could not have worked either: it keyed its module map on <c>file.FullName</c> and looked up
/// <c>IonUseSyntax.Path</c>, which is the string as written inside the quotes — <c>"common.ion"</c>,
/// never an absolute path — so every edge missed and the walk always found nothing.
/// </para>
/// <para>
/// <strong>Which graph.</strong> The edges are the explicit import directives, not type references.
/// Files in one project share a namespace and are compiled together, so <c>a.ion</c> naming a type
/// from <c>b.ion</c> while <c>b.ion</c> names one from <c>a.ion</c> is ordinary Ion and must stay
/// legal — which is exactly what <c>IonDependencyGraph.ModuleDependencyGraph</c> would report, since
/// it is derived from type dependencies. ION0001 says "cyclic module <em>import</em>", and that is
/// what this checks. Cycles between separately configured external modules are a different thing
/// and are already ION0040, raised by <see cref="ModuleResolver"/> off ion.config.json.
/// </para>
/// <para>
/// <c>#import { T } from "mod"</c> contributes no edge here. A module name in an <c>#import</c>
/// refers to an external project resolved from ion.config.json; it is a leaf as far as this
/// project's own files are concerned, and it cannot import back into them.
/// </para>
/// </remarks>
public sealed class ImportCycleDetectionStage(CompilationContext context)
    : CompilationStage(context)
{
    public override string StageName => "Import Cycle Detection";
    public override string StageDescription => "Checking for cyclic module imports";
    public override bool StopOnError => false;

    public override void DoProcess()
    {
        // Module name → the modules it imports. Same shape as
        // IonDependencyGraph.ModuleDependencyGraph, and walked by the same routine.
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byModuleName = new Dictionary<string, IonFileSyntax>(StringComparer.Ordinal);

        foreach (var file in Context.Files)
        {
            var name = ModuleNameOf(file);
            byModuleName[name] = file;
            graph.TryAdd(name, new HashSet<string>(StringComparer.Ordinal));
        }

        // The directive that closes each edge, so the diagnostic can point at a real `#use` line
        // rather than at the top of the file.
        var edgeSource = new Dictionary<(string From, string To), IonUseSyntax>();

        foreach (var file in Context.Files)
        {
            var from = ModuleNameOf(file);

            foreach (var use in file.useSyntaxes)
            {
                if (Resolve(use.Path, byModuleName) is not { } to || to == from)
                    continue;

                graph[from].Add(to);
                edgeSource.TryAdd((from, to), use);
            }
        }

        foreach (var cycle in IonDependencyGraph.FindCycles(graph))
        {
            // Point at the directive that closes the loop — the last hop — because that is the one
            // edge the author can delete to break it.
            var closing = cycle.Count >= 2
                ? edgeSource.GetValueOrDefault((cycle[^2], cycle[^1]))
                : null;

            Error(IonAnalyticCodes.ION0001_CycleImportDetected,
                (IonSyntaxBase?)closing ?? byModuleName[cycle[0]].Definitions.FirstOrDefault() ?? new IonSyntaxBase(),
                string.Join(" → ", cycle));
        }
    }

    /// <summary>
    /// The module name of a file.
    /// </summary>
    /// <remarks>
    /// <c>IonFileSyntax.Name</c>, which is what <c>TransformStage.PrepareModule</c> puts in
    /// <c>IonModule.Name</c>, so a cycle is reported in the same vocabulary as everything else that
    /// talks about modules. Deliberately not <c>file.file.Name</c>: <c>IonParser.Parse(name,
    /// content)</c> — the overload the CLI uses — synthesizes its <c>FileInfo</c> as
    /// <c>$"{name}.ion"</c> from a name that already ends in <c>.ion</c>, so that property reads
    /// <c>a.ion.ion</c>.
    /// </remarks>
    private static string ModuleNameOf(IonFileSyntax file) => file.Name;

    /// <summary>
    /// Resolves the string inside a <c>#use "…"</c> to a module in this compilation, or
    /// <see langword="null"/> when it names nothing here.
    /// </summary>
    /// <remarks>
    /// Accepts the file name with or without the <c>.ion</c> suffix, and tolerates a directory
    /// prefix, matching how <c>UnusedSymbolDetectionStage</c> reads the same directive. Unresolvable
    /// is silent: a <c>#use</c> naming a file that is not in the project is not a cycle, and
    /// ION0047 already has something to say about <c>#use</c> in general.
    /// </remarks>
    private static string? Resolve(string? written, Dictionary<string, IonFileSyntax> byModuleName)
    {
        if (string.IsNullOrWhiteSpace(written))
            return null;

        var leaf = Path.GetFileName(written.Replace('\\', '/'));

        // A module's name is whatever the parser was handed, which for the CLI is the file name and
        // therefore carries `.ion`; a hand-built compilation may not. Try the spelling as written,
        // with the suffix added, and with it removed, so neither convention depends on the other.
        var bare = leaf.EndsWith(".ion", StringComparison.OrdinalIgnoreCase) ? leaf[..^4] : leaf;

        foreach (var candidate in new[] { leaf, leaf + ".ion", bare })
        {
            var match = byModuleName.Keys
                .FirstOrDefault(k => string.Equals(k, candidate, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return match;
        }

        return null;
    }
}
