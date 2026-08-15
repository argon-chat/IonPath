﻿namespace ion.runtime;

using System.Text;

public sealed class IonDependencyGraph(IEnumerable<IonModule> modules)
{
    public Dictionary<IonType, List<IonType>> TypeDependencyGraph { get; } = new();
    public Dictionary<string, HashSet<string>> ModuleDependencyGraph { get; } = new();

    private readonly Dictionary<IonType, IonModule> _typeToModule = new();
    private List<IonModule> _modules;


    public string ExportTypeGraphToDot()
    {
        var sb = new StringBuilder();
        sb.AppendLine("digraph TypeGraph {");

        foreach (var (type, deps) in TypeDependencyGraph)
        {
            var from = Escape(type.name.Identifier);

            foreach (var to in deps.Select(dep => Escape(dep.name.Identifier))) sb.AppendLine($"    \"{from}\" -> \"{to}\";");

            if (deps.Count == 0) sb.AppendLine($"    \"{from}\";");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Escape(string name)
    {
        return name.Replace("\"", "\\\"");
    }

    /// <summary>
    /// Every distinct cycle in a module-level dependency graph, each as the list of module names
    /// from where the cycle opens back round to it (so the first and last entry are the same).
    /// </summary>
    /// <param name="graph">
    /// A graph in the shape of <see cref="ModuleDependencyGraph"/>: module name → the module names
    /// it depends on. Edges pointing at a module that is not a key are ignored — an external
    /// dependency is a leaf here.
    /// </param>
    /// <remarks>
    /// <para>
    /// Lives beside <see cref="ModuleDependencyGraph"/> so the two cannot drift: the same routine
    /// answers "is this set of modules acyclic" whichever way the edges were derived.
    /// </para>
    /// <para>
    /// <c>ion.compiler.ImportCycleDetectionStage</c> feeds it the <em>import</em> graph rather than
    /// <see cref="ModuleDependencyGraph"/>, which is built from type references. That is deliberate,
    /// not an oversight: two files in one project may freely reference each other's types — they
    /// share a namespace and are compiled together — so a type-reference cycle between modules is
    /// ordinary, correct Ion. An <em>import</em> cycle is the thing ION0001 is about.
    /// </para>
    /// <para>
    /// Each cycle is reported once. A plain colour-marking DFS re-reports the same loop once per
    /// entry point, so cycles are keyed on their set of participants.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<string>> FindCycles(
        IReadOnlyDictionary<string, HashSet<string>> graph)
    {
        var cycles = new List<IReadOnlyList<string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var state = new Dictionary<string, byte>(StringComparer.Ordinal); // 0 white, 1 gray, 2 black
        var path = new List<string>();

        foreach (var node in graph.Keys)
            Visit(node);

        return cycles;

        void Visit(string node)
        {
            if (state.GetValueOrDefault(node) != 0)
                return;

            state[node] = 1;
            path.Add(node);

            if (graph.TryGetValue(node, out var dependencies))
            {
                foreach (var next in dependencies)
                {
                    if (!graph.ContainsKey(next))
                        continue;

                    switch (state.GetValueOrDefault(next))
                    {
                        case 0:
                            Visit(next);
                            break;

                        case 1:
                            var cycle = path.Skip(path.IndexOf(next)).Append(next).ToList();

                            // Key on the participants, order-independent, so A→B→A found from A and
                            // the same loop found from B collapse to one report.
                            var key = string.Join('|', cycle.Distinct().Order(StringComparer.Ordinal));
                            if (seen.Add(key))
                                cycles.Add(cycle);
                            break;
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            state[node] = 2;
        }
    }

    public void Generate()
    {
        _modules = modules.ToList();

        foreach (var module in _modules)
        {
            foreach (var type in module.Definitions)
                _typeToModule[type] = module;
        }

        foreach (var module in _modules)
        {
            foreach (var type in module.Definitions)
            {
                var deps = FindTypeDependencies(type);

                TypeDependencyGraph[type] = deps;

                foreach (var dep in deps)
                {
                    if (!_typeToModule.TryGetValue(dep, out var depModule) ||
                        depModule.Name == module.Name) continue;
                    if (!ModuleDependencyGraph.TryGetValue(module.Name, out var set))
                    {
                        set = [];
                        ModuleDependencyGraph[module.Name] = set;
                    }

                    set.Add(depModule.Name);
                }
            }

            foreach (var method in module.Services.SelectMany(service => service.methods))
            {
                foreach (var arg in method.arguments)
                    AddModuleDependency(module, arg.type);

                AddModuleDependency(module, method.returnType);
            }
        }
    }

    private void AddModuleDependency(IonModule from, IonType type)
    {
        if (type.IsBuiltin || type.IsUnresolved) return;

        if (!_typeToModule.TryGetValue(type, out var to) || to.Name == from.Name)
            return;
        if (!ModuleDependencyGraph.TryGetValue(from.Name, out var set))
        {
            set = [];
            ModuleDependencyGraph[from.Name] = set;
        }

        set.Add(to.Name);
    }

    private List<IonType> FindTypeDependencies(IonType type)
    {
        var deps = new HashSet<IonType>();

        foreach (var field in type.fields)
            AddType(deps, field.type);

        foreach (var attr in type.attributes)
        {
            foreach (var arg in attr.arguments.OfType<IonType>())
                AddType(deps, arg);
        }

        switch (type)
        {
            case IonEnum e:
            {
                foreach (var c in e.members)
                    AddType(deps, c.type);
                break;
            }
            case IonFlags f:
            {
                foreach (var c in f.members)
                    AddType(deps, c.type);
                break;
            }
        }

        return deps.Where(d => d is { IsUnresolved: false }).ToList();
    }

    private static void AddType(HashSet<IonType> set, IonType type)
    {
        if (type is { IsUnresolved: false })
            set.Add(type);
    }
}