namespace ion.runtime;

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