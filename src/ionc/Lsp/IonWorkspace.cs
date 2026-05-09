namespace ion.compiler.Lsp;

using ion.runtime;
using ion.syntax;

/// <summary>
/// Represents a single ion project rooted at a directory containing ion.config.json.
/// </summary>
public sealed class IonProject
{
    public required string RootPath { get; init; }
    public required IonProjectConfig Config { get; init; }
    public List<IonFileSyntax> ParsedFiles { get; set; } = [];
    public Dictionary<IonFileSyntax, string> FileUriMap { get; set; } = new();
    public CompilationContext? LastContext { get; set; }
    public List<IonModule> ExternalModules { get; set; } = [];

    public IReadOnlyList<string> GetFeatures()
        => Config.Features.Select(x => x.ToString().ToLowerInvariant()).ToList();

    /// <summary>
    /// Get available module names declared in this project's config.
    /// </summary>
    public IReadOnlyList<string> GetModuleNames()
        => Config.Modules?.Keys.ToList() ?? [];
}

/// <summary>
/// Manages the workspace state: open documents, project configs, and compilation.
/// Supports multiple ion projects within a single workspace.
/// </summary>
public sealed class IonWorkspace
{
    private readonly Dictionary<string, string> _openDocuments = new(StringComparer.OrdinalIgnoreCase);
    private string? _rootPath;

    // Multi-project support
    private readonly List<IonProject> _projects = [];

    // Aggregated views for handlers
    public IReadOnlyList<IonFileSyntax> ParsedFiles => _projects.SelectMany(p => p.ParsedFiles).ToList();
    public CompilationContext? LastContext => FindProjectForCurrentFile()?.LastContext;
    public IReadOnlyList<IonModule> ExternalModules => _projects.SelectMany(p => p.ExternalModules).ToList();
    public IReadOnlyList<IonProject> Projects => _projects;

    /// <summary>
    /// Get the CompilationContext for the project that owns the given file.
    /// Falls back to aggregated LastContext if file is not found.
    /// </summary>
    public CompilationContext? GetContextForFile(string filePath)
        => FindProjectForFile(filePath)?.LastContext ?? LastContext;

    /// <summary>
    /// Get the real file system path for a parsed IonFileSyntax.
    /// </summary>
    public string GetFileUri(IonFileSyntax file)
    {
        foreach (var project in _projects)
        {
            if (project.FileUriMap.TryGetValue(file, out var uri))
                return uri;
        }
        return file.file.FullName;
    }

    /// <summary>
    /// Find a parsed file by its file system path.
    /// </summary>
    public IonFileSyntax? FindFileByUri(string path)
        => ParsedFiles.FirstOrDefault(f => GetFileUri(f).Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Find the project that owns a given file path.
    /// </summary>
    public IonProject? FindProjectForFile(string filePath)
    {
        // Find the project whose root is the closest ancestor of the file
        IonProject? best = null;
        var bestLen = -1;

        foreach (var project in _projects)
        {
            if (filePath.StartsWith(project.RootPath, StringComparison.OrdinalIgnoreCase)
                && project.RootPath.Length > bestLen)
            {
                best = project;
                bestLen = project.RootPath.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Get external modules for a specific file's project.
    /// </summary>
    public IReadOnlyList<IonModule> GetExternalModulesForFile(string filePath)
        => FindProjectForFile(filePath)?.ExternalModules ?? [];

    private IonProject? FindProjectForCurrentFile()
    {
        // Return the first project that has parsed files, or the first project
        return _projects.FirstOrDefault(p => p.ParsedFiles.Count > 0) ?? _projects.FirstOrDefault();
    }

    public void SetRoot(string rootPath)
    {
        _rootPath = rootPath;
        Console.WriteLine($"[ionc] Workspace root: {rootPath}");
        DiscoverProjects();
    }

    /// <summary>
    /// Discovers all ion projects in the workspace by scanning for ion.config.json files.
    /// </summary>
    private void DiscoverProjects()
    {
        _projects.Clear();

        if (_rootPath is null) return;

        // Find all ion.config.json files in the workspace
        var configFiles = Directory.GetFiles(_rootPath, "ion.config.json", SearchOption.AllDirectories);

        if (configFiles.Length == 0)
        {
            Console.WriteLine($"[ionc] No ion.config.json found in workspace");
            return;
        }

        foreach (var configPath in configFiles)
        {
            try
            {
                var config = IonProjectConfig.FromJson(File.ReadAllText(configPath));
                var projectRoot = Path.GetDirectoryName(configPath)!;

                // Normalize path to end with separator for reliable prefix matching
                if (!projectRoot.EndsWith(Path.DirectorySeparatorChar))
                    projectRoot += Path.DirectorySeparatorChar;

                var project = new IonProject
                {
                    RootPath = projectRoot,
                    Config = config
                };

                _projects.Add(project);
                Console.WriteLine($"[ionc] Discovered project '{config.Name}' at {projectRoot}");

                // Resolve external modules for this project
                ResolveExternalModules(project);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ionc] Failed to load {configPath}: {ex.Message}");
            }
        }

        Console.WriteLine($"[ionc] {_projects.Count} project(s) discovered");
    }

    private void ResolveExternalModules(IonProject project)
    {
        project.ExternalModules.Clear();

        if (project.Config.Modules is null || project.Config.Modules.Count == 0)
            return;

        var resolver = new ModuleResolver();
        var result = resolver.Resolve(project.RootPath, project.Config.Modules);

        foreach (var diag in result.Diagnostics)
            Console.WriteLine($"[ionc] Module resolution ({project.Config.Name}): {diag.Code} {diag.Message}");

        foreach (var resolved in result.Modules)
        {
            var features = resolved.Features.ToList();
            var ctx = CompilationContext.Create(features, resolved.Files);
            var pipeline = new CompilationPipeline(ctx);
            pipeline.Execute();

            foreach (var mod in ctx.ProcessedModules)
            {
                project.ExternalModules.Add(new IonModule
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
                });
            }

            Console.WriteLine($"[ionc] Resolved module '{resolved.Name}' for '{project.Config.Name}': {ctx.ProcessedModules.Sum(m => m.Definitions.Count)} types");
        }
    }

    public void OpenDocument(string uri, string content)
    {
        Console.WriteLine($"[ionc] Document opened: {uri}");
        _openDocuments[uri] = content;
    }

    public void UpdateDocument(string uri, string content)
    {
        Console.WriteLine($"[ionc] Document changed: {uri}");
        _openDocuments[uri] = content;
    }

    public void CloseDocument(string uri)
    {
        Console.WriteLine($"[ionc] Document closed: {uri}");
        _openDocuments.Remove(uri);
    }

    public string? GetDocumentContent(string uri)
    {
        return _openDocuments.TryGetValue(uri, out var content) ? content : null;
    }

    /// <summary>
    /// Compiles all known .ion files and returns diagnostics per file URI.
    /// Each project is compiled independently with its own context and modules.
    /// </summary>
    public Dictionary<string, List<IonDiagnostic>> CompileAll()
    {
        var result = new Dictionary<string, List<IonDiagnostic>>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in _projects)
        {
            var projectDiags = CompileProject(project);
            foreach (var (uri, diags) in projectDiags)
            {
                if (!result.ContainsKey(uri))
                    result[uri] = [];
                result[uri].AddRange(diags);
            }
        }

        var totalDiags = result.Values.Sum(d => d.Count);
        Console.WriteLine($"[ionc] Publishing {totalDiags} diagnostic(s) across {result.Count} file(s)");

        return result;
    }

    private Dictionary<string, List<IonDiagnostic>> CompileProject(IonProject project)
    {
        var files = CollectFilesForProject(project);
        var parsed = new List<IonFileSyntax>();
        var uriMap = new Dictionary<IonFileSyntax, string>();
        var result = new Dictionary<string, List<IonDiagnostic>>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"[ionc] CompileProject '{project.Config.Name}': {files.Count} file(s)");

        foreach (var (uri, content) in files)
        {
            result[uri] = [];

            try
            {
                var fileInfo = new FileInfo(uri);
                using var _ = IonFileProcessingScope.Begin(fileInfo);
                var syntax = IonParser.Parse(Path.GetFileNameWithoutExtension(uri), content);

                if (syntax.allTokens is not null)
                {
                    foreach (var token in syntax.allTokens.OfType<InvalidIonBlock>())
                    {
                        result[uri].Add(new IonDiagnostic(
                            "ION_PARSE",
                            IonDiagnosticSeverity.Error,
                            $"Unexpected syntax: {token.block.Trim().Split('\n')[0]}",
                            token
                        ));
                    }
                }

                parsed.Add(syntax);
                uriMap[syntax] = uri;
            }
            catch (ParseException ex)
            {
                Console.WriteLine($"[ionc]   Parse FAILED {Path.GetFileName(uri)}: {ex.Message}");
                result[uri].Add(new IonDiagnostic(
                    "ION_PARSE",
                    IonDiagnosticSeverity.Error,
                    ex.Message,
                    new IonSyntaxBase().WithPos(
                        ex.Error?.ErrorPos ?? new Pidgin.SourcePos(1, 1),
                        ex.Error?.ErrorPos ?? new Pidgin.SourcePos(1, 1))
                ));
            }
        }

        if (parsed.Count == 0)
        {
            project.ParsedFiles = parsed;
            project.FileUriMap = uriMap;
            project.LastContext = null;
            return result;
        }

        var ctx = CompilationContext.Create(project.GetFeatures(), parsed, project.ExternalModules);
        var pipeline = new CompilationPipeline(ctx);
        pipeline.Execute();

        project.ParsedFiles = parsed;
        project.FileUriMap = uriMap;
        project.LastContext = ctx;

        Console.WriteLine($"[ionc]   Pipeline done: {ctx.Diagnostics.Count} diagnostic(s)");

        foreach (var diag in ctx.Diagnostics)
        {
            var sourceFile = diag.SourceFile?.FullName;
            if (sourceFile is null)
            {
                Console.WriteLine($"[ionc]   WARN: diagnostic '{diag.Code}: {diag.Message}' has no SourceFile, skipping");
                continue;
            }

            var matchedUri = files.Keys.FirstOrDefault(u =>
                string.Equals(u, sourceFile, StringComparison.OrdinalIgnoreCase))
                ?? files.Keys.FirstOrDefault(u =>
                    u.EndsWith(Path.GetFileName(sourceFile), StringComparison.OrdinalIgnoreCase));

            if (matchedUri is not null)
            {
                if (!result.ContainsKey(matchedUri))
                    result[matchedUri] = [];
                result[matchedUri].Add(diag);
            }
        }

        return result;
    }

    private Dictionary<string, string> CollectFilesForProject(IonProject project)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Get all other project roots to exclude nested projects
        var otherProjectRoots = _projects
            .Where(p => p != project && p.RootPath.StartsWith(project.RootPath, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.RootPath)
            .ToList();

        bool BelongsToThisProject(string filePath)
        {
            if (!filePath.StartsWith(project.RootPath, StringComparison.OrdinalIgnoreCase))
                return false;
            // Exclude files that belong to a nested project
            return !otherProjectRoots.Any(r => filePath.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        }

        // Include open documents that belong to this project
        foreach (var (uri, content) in _openDocuments)
        {
            if (BelongsToThisProject(uri))
                files[uri] = content;
        }

        // Include files from disk
        var dir = new DirectoryInfo(project.RootPath);
        if (dir.Exists)
        {
            foreach (var file in dir.EnumerateFiles("*.ion", SearchOption.AllDirectories))
            {
                var fullPath = file.FullName;
                if (!files.ContainsKey(fullPath) && BelongsToThisProject(fullPath))
                    files[fullPath] = File.ReadAllText(fullPath);
            }
        }

        return files;
    }
}
