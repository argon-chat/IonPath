namespace ion.compiler.Lsp;

using ion.runtime;
using ion.syntax;
using System.Text.Json;

/// <summary>
/// Manages the workspace state: open documents, project config, and compilation.
/// </summary>
public sealed class IonWorkspace
{
    private readonly Dictionary<string, string> _openDocuments = new(StringComparer.OrdinalIgnoreCase);
    private string? _rootPath;
    private IonProjectConfig? _projectConfig;

    // Cached compilation state for hover/goto/etc.
    private List<IonFileSyntax> _lastParsed = [];
    private Dictionary<IonFileSyntax, string> _fileUriMap = new();
    private CompilationContext? _lastContext;

    // External module state
    private List<IonModule> _externalModules = [];
    private ModuleResolver? _moduleResolver;

    public IReadOnlyList<IonFileSyntax> ParsedFiles => _lastParsed;
    public CompilationContext? LastContext => _lastContext;
    public IReadOnlyList<IonModule> ExternalModules => _externalModules;

    /// <summary>
    /// Get the real file system path for a parsed IonFileSyntax.
    /// </summary>
    public string GetFileUri(IonFileSyntax file)
        => _fileUriMap.TryGetValue(file, out var uri) ? uri : file.file.FullName;

    /// <summary>
    /// Find a parsed file by its file system path.
    /// </summary>
    public IonFileSyntax? FindFileByUri(string path)
        => _lastParsed.FirstOrDefault(f => GetFileUri(f).Equals(path, StringComparison.OrdinalIgnoreCase));

    public void SetRoot(string rootPath)
    {
        _rootPath = rootPath;
        Console.WriteLine($"[ionc] Workspace root: {rootPath}");
        TryLoadProjectConfig();
    }

    private void TryLoadProjectConfig()
    {
        if (_rootPath is null) return;
        var configPath = Path.Combine(_rootPath, "ion.config.json");
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"[ionc] No ion.config.json found at {configPath}");
            return;
        }
        try
        {
            _projectConfig = IonProjectConfig.FromJson(File.ReadAllText(configPath));
            Console.WriteLine($"[ionc] Loaded ion.config.json, features: {string.Join(", ", GetFeatures())}");
            ResolveExternalModules();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ionc] Failed to load ion.config.json: {ex.Message}");
            _projectConfig = null;
        }
    }

    private void ResolveExternalModules()
    {
        _externalModules.Clear();

        if (_rootPath is null || _projectConfig?.Modules is null || _projectConfig.Modules.Count == 0)
            return;

        _moduleResolver = new ModuleResolver();
        var result = _moduleResolver.Resolve(_rootPath, _projectConfig.Modules);

        foreach (var diag in result.Diagnostics)
            Console.WriteLine($"[ionc] Module resolution: {diag.Code} {diag.Message}");

        // Transform resolved modules into IonModules for the compilation context
        foreach (var resolved in result.Modules)
        {
            var features = resolved.Features.ToList();
            var ctx = CompilationContext.Create(features, resolved.Files);
            var pipeline = new CompilationPipeline(ctx);
            pipeline.Execute();

            foreach (var mod in ctx.ProcessedModules)
            {
                _externalModules.Add(new IonModule
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

            Console.WriteLine($"[ionc] Resolved module '{resolved.Name}': {ctx.ProcessedModules.Sum(m => m.Definitions.Count)} types");
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

    public IReadOnlyList<string> GetFeatures()
    {
        if (_projectConfig is null)
            return ["std"];
        return _projectConfig.Features.Select(x => x.ToString().ToLowerInvariant()).ToList();
    }

    /// <summary>
    /// Compiles all known .ion files and returns diagnostics per file URI.
    /// </summary>
    public Dictionary<string, List<IonDiagnostic>> CompileAll()
    {
        var files = CollectFiles();
        var parsed = new List<IonFileSyntax>();
        var uriMap = new Dictionary<IonFileSyntax, string>();
        var result = new Dictionary<string, List<IonDiagnostic>>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"[ionc] CompileAll: {files.Count} file(s)");

        foreach (var (uri, content) in files)
        {
            result[uri] = [];

            try
            {
                var fileInfo = new FileInfo(uri);
                using var _ = IonFileProcessingScope.Begin(fileInfo);
                var syntax = IonParser.Parse(Path.GetFileNameWithoutExtension(uri), content);

                // Surface parse recovery errors (InvalidIonBlock)
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
                Console.WriteLine($"[ionc]   Parsed {Path.GetFileName(uri)}: {syntax.messageSyntaxes.Count} msg, {syntax.serviceSyntaxes.Count} svc, {syntax.enumSyntaxes.Count} enum");
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
            _lastParsed = parsed;
            _fileUriMap = uriMap;
            _lastContext = null;
            return result;
        }

        var ctx = CompilationContext.Create(GetFeatures(), parsed, _externalModules);
        var pipeline = new CompilationPipeline(ctx);
        pipeline.Execute();

        _lastParsed = parsed;
        _fileUriMap = uriMap;
        _lastContext = ctx;

        Console.WriteLine($"[ionc]   Pipeline done: {ctx.Diagnostics.Count} diagnostic(s)");

        foreach (var diag in ctx.Diagnostics)
        {
            var sourceFile = diag.SourceFile?.FullName;
            if (sourceFile is null)
            {
                Console.WriteLine($"[ionc]   WARN: diagnostic '{diag.Code}: {diag.Message}' has no SourceFile, skipping");
                continue;
            }

            // Match by full path first, then by filename
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
            else
            {
                Console.WriteLine($"[ionc]   WARN: no match for SourceFile '{sourceFile}'");
            }
        }

        var totalDiags = result.Values.Sum(d => d.Count);
        Console.WriteLine($"[ionc] Publishing {totalDiags} diagnostic(s) across {result.Count} file(s)");

        return result;
    }

    private Dictionary<string, string> CollectFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Include open documents
        foreach (var (uri, content) in _openDocuments)
            files[uri] = content;

        // Include files from disk if we have a root
        if (_rootPath is not null)
        {
            var dir = new DirectoryInfo(_rootPath);
            foreach (var file in dir.EnumerateFiles("*.ion", SearchOption.AllDirectories))
            {
                var fullPath = file.FullName;
                if (!files.ContainsKey(fullPath))
                    files[fullPath] = File.ReadAllText(fullPath);
            }
        }

        return files;
    }
}
