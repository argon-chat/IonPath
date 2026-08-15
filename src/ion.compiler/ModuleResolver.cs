namespace ion.compiler;

using ion.runtime;
using ion.syntax;
using System.Text.Json;

/// <summary>
/// Resolves external module dependencies declared in ion.config.json.
/// Handles transitive resolution with cycle detection.
/// </summary>
public sealed class ModuleResolver
{
    private readonly Dictionary<string, ResolvedModule> _resolved = new();
    private readonly HashSet<string> _resolving = new(); // cycle detection

    /// <summary>
    /// Resolved module with its parsed files and metadata.
    /// </summary>
    /// <remarks>
    /// <c>ContentHash</c> — a SHA-256 over the concatenated source of every <c>.ion</c> file in the
    /// module — used to be the last member. Nothing ever compared it: it was computed on every
    /// resolve, stored here, and read by no one, and ION0046 ("content hash has changed since last
    /// lock") could not fire because there was nowhere to record the expected value. Module pinning
    /// is a worthwhile feature, and when it is built it needs somewhere to keep that expectation;
    /// until then a hash nobody checks is worse than no hash, because it reads like a guarantee.
    /// </remarks>
    public sealed record ResolvedModule(
        string Name,
        string ProjectName,
        string ConfigPath,
        string RootPath,
        IReadOnlyList<IonFileSyntax> Files,
        IReadOnlyList<string> Features,
        IReadOnlyDictionary<string, string> ChildModules);

    /// <summary>
    /// Resolve all modules declared in the given config, relative to the project root.
    /// </summary>
    public ModuleResolutionResult Resolve(string projectRoot, IReadOnlyDictionary<string, string> modules)
    {
        var diagnostics = new List<IonDiagnostic>();

        foreach (var (name, relativePath) in modules)
        {
            ResolveModule(name, relativePath, projectRoot, diagnostics);
        }

        return new ModuleResolutionResult(_resolved.Values.ToList(), diagnostics);
    }

    private void ResolveModule(string name, string relativePath, string basePath, List<IonDiagnostic> diagnostics)
    {
        if (_resolved.ContainsKey(name))
            return;

        if (!_resolving.Add(name))
        {
            var cycle = string.Join(" → ", _resolving) + " → " + name;
            diagnostics.Add(new IonDiagnostic(
                IonAnalyticCodes.ION0040_ModuleCircularDependency.code,
                IonDiagnosticSeverity.Error,
                string.Format(IonAnalyticCodes.ION0040_ModuleCircularDependency.template, cycle),
                new IonSyntaxBase()));
            return;
        }

        try
        {
            var moduleRoot = Path.GetFullPath(Path.Combine(basePath, relativePath));
            var configPath = Path.Combine(moduleRoot, "ion.config.json");

            if (!File.Exists(configPath))
            {
                diagnostics.Add(new IonDiagnostic(
                    IonAnalyticCodes.ION0041_ModuleConfigNotFound.code,
                    IonDiagnosticSeverity.Error,
                    string.Format(IonAnalyticCodes.ION0041_ModuleConfigNotFound.template, name, configPath),
                    new IonSyntaxBase()));
                return;
            }

            var config = ParseModuleConfig(configPath);
            if (config is null)
            {
                diagnostics.Add(new IonDiagnostic(
                    IonAnalyticCodes.ION0041_ModuleConfigNotFound.code,
                    IonDiagnosticSeverity.Error,
                    string.Format(IonAnalyticCodes.ION0041_ModuleConfigNotFound.template, name, configPath),
                    new IonSyntaxBase()));
                return;
            }

            // Parse all .ion files in the module
            var ionFiles = Directory.GetFiles(moduleRoot, "*.ion", SearchOption.AllDirectories);
            var parsedFiles = new List<IonFileSyntax>();

            foreach (var file in ionFiles.OrderBy(f => f))
            {
                var fi = new FileInfo(file);
                try
                {
                    parsedFiles.Add(IonParser.Parse(fi));
                }
                catch
                {
                    // Skip unparseable files in external modules
                }
            }

            var childModules = config.Modules ?? new Dictionary<string, string>();

            var resolved = new ResolvedModule(
                Name: name,
                ProjectName: config.Name ?? name,
                ConfigPath: configPath,
                RootPath: moduleRoot,
                Files: parsedFiles,
                Features: config.Features ?? [],
                ChildModules: childModules);

            _resolved[name] = resolved;

            // Resolve transitive dependencies
            foreach (var (childName, childPath) in childModules)
            {
                ResolveModule(childName, childPath, moduleRoot, diagnostics);
            }
        }
        finally
        {
            _resolving.Remove(name);
        }
    }

    private static IonConfigFile? ParseModuleConfig(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<IonConfigFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns all resolved modules in topological order (dependencies before dependents).
    /// </summary>
    public IReadOnlyList<ResolvedModule> GetTopologicalOrder()
    {
        var visited = new HashSet<string>();
        var result = new List<ResolvedModule>();

        void Visit(string name)
        {
            if (!visited.Add(name)) return;
            if (!_resolved.TryGetValue(name, out var mod)) return;

            foreach (var childName in mod.ChildModules.Keys)
                Visit(childName);

            result.Add(mod);
        }

        foreach (var name in _resolved.Keys)
            Visit(name);

        return result;
    }
}

/// <summary>
/// Minimal representation of ion.config.json for module resolution.
/// </summary>
public sealed class IonConfigFile
{
    public string? Name { get; set; }
    public List<string>? Features { get; set; }
    public Dictionary<string, string>? Modules { get; set; }
    public Dictionary<string, JsonElement>? Generators { get; set; }
}

/// <summary>
/// Result of module resolution containing resolved modules and any diagnostics.
/// </summary>
public sealed record ModuleResolutionResult(
    IReadOnlyList<ModuleResolver.ResolvedModule> Modules,
    IReadOnlyList<IonDiagnostic> Diagnostics);
