namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ion.runtime;
using ion.syntax;

public class IonCodeLensHandler(IonWorkspace workspace) : CodeLensHandlerBase
{
    protected override CodeLensRegistrationOptions CreateRegistrationOptions(
        CodeLensCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CodeLensRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion"),
            ResolveProvider = true
        };
    }

    public override Task<CodeLensContainer?> Handle(CodeLensParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        Console.WriteLine($"[ionc] CodeLens requested for: {uri}");
        var file = workspace.ParsedFiles
            .FirstOrDefault(f => workspace.GetFileUri(f).Equals(uri, StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            Console.WriteLine($"[ionc] CodeLens: file not found in parsed files");
            return Task.FromResult<CodeLensContainer?>(null);
        }

        var lenses = new List<CodeLens>();

        foreach (var msg in file.messageSyntaxes)
        {
            var range = ToRange(msg);
            var refs = IonLspHelpers.FindReferences(msg.Name.Identifier, workspace, false);
            lenses.Add(MakeRefLens(range, refs.Count));

            var ctx = workspace.LastContext;
            if (ctx is not null)
            {
                var allDefs = ctx.ProcessedModules
                    .Concat(ctx.GlobalModules)
                    .SelectMany(m => m.Definitions)
                    .ToList();

                var typeDef = allDefs.FirstOrDefault(d =>
                    d.name.Identifier.Equals(msg.Name.Identifier, StringComparison.OrdinalIgnoreCase));
                if (typeDef is not null)
                {
                    var size = ComputeSize(typeDef, allDefs);
                    if (size is not null)
                        lenses.Add(MakeInfoLens(range, size));
                }
            }
        }

        foreach (var svc in file.serviceSyntaxes)
        {
            var range = ToRange(svc);
            var refs = IonLspHelpers.FindReferences(svc.serviceName.Identifier, workspace, false);
            lenses.Add(MakeRefLens(range, refs.Count));
            lenses.Add(MakeInfoLens(range, $"{svc.Methods.Count} method{(svc.Methods.Count == 1 ? "" : "s")}"));
        }

        foreach (var en in file.enumSyntaxes)
        {
            var range = ToRange(en);
            var refs = IonLspHelpers.FindReferences(en.Name.Identifier, workspace, false);
            lenses.Add(MakeRefLens(range, refs.Count));
        }

        foreach (var fl in file.flagsSyntaxes)
        {
            var range = ToRange(fl);
            var refs = IonLspHelpers.FindReferences(fl.Name.Identifier, workspace, false);
            lenses.Add(MakeRefLens(range, refs.Count));
        }

        foreach (var un in file.unionSyntaxes)
        {
            var range = ToRange(un);
            var refs = IonLspHelpers.FindReferences(un.unionName.Identifier, workspace, false);
            lenses.Add(MakeRefLens(range, refs.Count));
        }

        Console.WriteLine($"[ionc] CodeLens: returning {lenses.Count} lens(es)");
        return Task.FromResult<CodeLensContainer?>(new CodeLensContainer(lenses));
    }

    public override Task<CodeLens> Handle(CodeLens request, CancellationToken cancellationToken)
        => Task.FromResult(request);

    private static CodeLens MakeRefLens(Range range, int count)
    {
        return new CodeLens
        {
            Range = range,
            Command = new Command
            {
                Title = $"{count} reference{(count == 1 ? "" : "s")}",
                Name = "editor.action.findReferences"
            }
        };
    }

    private static CodeLens MakeInfoLens(Range range, string text)
    {
        return new CodeLens
        {
            Range = range,
            Command = new Command { Title = text, Name = "" }
        };
    }

    private static string? ComputeSize(IonType type, IReadOnlyList<IonType> allDefs)
    {
        if (type.fields.Count == 0) return null;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBits = 0;
        var hasVariable = false;

        foreach (var field in type.fields)
        {
            var bits = GetBits(field.type, visited, allDefs);
            if (bits is not null)
                totalBits += bits.Value;
            else
                hasVariable = true;
        }

        if (totalBits == 0 && hasVariable) return "variable size";
        var bytes = totalBits / 8;
        return hasVariable ? $"≥ {bytes} bytes + variable" : $"{bytes} bytes ({totalBits} bits)";
    }

    private static int? GetBits(IonType type, HashSet<string> visited, IReadOnlyList<IonType> allDefs)
    {
        if (type.IsMaybe || type.IsArray || type.IsPartial) return null;
        var name = type.name.Identifier;
        return name switch
        {
            "bool" => 8, "guid" => 128, "datetime" => 64, "dateonly" => 32,
            "timeonly" => 64, "duration" => 64, "void" => 0,
            "string" or "bytes" or "bigint" or "uri" => null,
            _ => type.HasBitsAttribute ? type.Bits : ResolveNested(name, visited, allDefs)
        };
    }

    private static int? ResolveNested(string name, HashSet<string> visited, IReadOnlyList<IonType> allDefs)
    {
        var resolved = allDefs.FirstOrDefault(d =>
            d.name.Identifier.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (resolved is null || !visited.Add(name)) return null;

        if (resolved is IonEnum e) return GetBits(e.baseType, visited, allDefs);
        if (resolved is IonFlags f) return GetBits(f.baseType, visited, allDefs);

        if (resolved.fields.Count == 0) { visited.Remove(name); return null; }

        var total = 0;
        foreach (var field in resolved.fields)
        {
            var b = GetBits(field.type, visited, allDefs);
            if (b is null) { visited.Remove(name); return null; }
            total += b.Value;
        }
        visited.Remove(name);
        return total;
    }

    private static Range ToRange(IonSyntaxBase node)
        => IonLspHelpers.ToLspRange(node);
}
