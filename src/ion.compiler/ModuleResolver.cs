namespace ion.compiler;

using ion.runtime;
using ion.syntax;
using System.Security.Cryptography;
using System.Text;
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
    public sealed record ResolvedModule(
        string Name,
        string ConfigPath,
        string RootPath,
        IReadOnlyList<IonFileSyntax> Files,
        IReadOnlyList<string> Features,
        IReadOnlyDictionary<string, string> ChildModules,
        string ContentHash);

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
            var contentBuilder = new StringBuilder();

            foreach (var file in ionFiles.OrderBy(f => f))
            {
                var fi = new FileInfo(file);
                try
                {
                    var parsed = IonParser.Parse(fi);
                    parsedFiles.Add(parsed);
                    contentBuilder.Append(File.ReadAllText(file));
                }
                catch
                {
                    // Skip unparseable files in external modules
                }
            }

            var hash = ComputeHash(contentBuilder.ToString());

            var childModules = config.Modules ?? new Dictionary<string, string>();

            var resolved = new ResolvedModule(
                Name: config.Name ?? name,
                ConfigPath: configPath,
                RootPath: moduleRoot,
                Files: parsedFiles,
                Features: config.Features ?? [],
                ChildModules: childModules,
                ContentHash: hash);

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

    private static string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
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
