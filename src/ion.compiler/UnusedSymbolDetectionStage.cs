namespace ion.compiler;

using runtime;
using syntax;

public sealed class UnusedSymbolDetectionStage(CompilationContext context)
    : CompilationStage(context)
{
    public override string StageName => "Unused Symbol Detection";
    public override string StageDescription => "Checking for unused types and imports";
    public override bool StopOnError => false;

    public override void DoProcess()
    {
        DetectUnusedImports();
        DetectUnusedImportedTypes();
        DetectUnusedTypes();
        DetectUnusedMixins();
    }

    /// <summary>
    /// ION1001 — a <c>mixin</c> that no message and no other mixin includes.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DetectUnusedTypes"/> because a mixin is not in
    /// <c>IonFileSyntax.Definitions</c> and, more to the point, is not reachable the way a type is:
    /// the only thing that can use one is a <c>with</c> clause. A mixin nobody includes contributes
    /// nothing to any wire format at all, so it is exactly the dead declaration this band is for.
    /// </remarks>
    private void DetectUnusedMixins()
    {
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Context.Files)
        {
            foreach (var message in file.messageSyntaxes)
            foreach (var name in message.Mixins ?? [])
                included.Add(name.Identifier);

            foreach (var mixin in file.mixinSyntaxes)
            foreach (var name in mixin.Mixins ?? [])
                included.Add(name.Identifier);
        }

        foreach (var file in Context.Files)
        foreach (var mixin in file.mixinSyntaxes)
            if (!included.Contains(mixin.Name.Identifier))
                Info(IonAnalyticCodes.ION1001_UnusedMixin, mixin, mixin.Name.Identifier);
    }

    /// <summary>
    /// ION0045 — a type named in <c>#import { A, B } from "mod"</c> that the file never references.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from ION1002, which is about a whole <c>#use</c> being pointless. An
    /// <c>#import</c> names its types one by one, so it can be partly used: importing five types and
    /// referencing two is the case worth reporting, and the one ION1002's file-level test cannot
    /// see.
    /// </para>
    /// <para>
    /// A warning rather than a hint, unlike its ION10xx neighbours, because a stale entry in an
    /// import list is a real, resolvable dependency on another module — it keeps that module in the
    /// build graph and in the generated project's references for no reason.
    /// </para>
    /// </remarks>
    private void DetectUnusedImportedTypes()
    {
        foreach (var file in Context.Files)
        {
            if (file.importSyntaxes.Count == 0)
                continue;

            var referenced = CollectReferencedTypeNames(file);

            foreach (var import in file.importSyntaxes)
            {
                foreach (var typeName in import.TypeNames.Where(typeName => !referenced.Contains(typeName)))
                    Warn(IonAnalyticCodes.ION0045_ModuleUnusedImport, import, typeName, import.ModuleName);
            }
        }
    }

    private void DetectUnusedImports()
    {
        foreach (var file in Context.Files)
        {
            foreach (var use in file.useSyntaxes)
            {
                var importedPath = use.Path;
                var isUsed = false;

                // Check if any type from the imported file is referenced in this file
                var importedFile = Context.Files.FirstOrDefault(f =>
                    f.file.Name.Equals(importedPath, StringComparison.OrdinalIgnoreCase) ||
                    f.file.Name.Equals(importedPath + ".ion", StringComparison.OrdinalIgnoreCase));

                if (importedFile is null) continue;

                var importedTypeNames = importedFile.Definitions
                    .Select(GetDefinitionName)
                    .Where(n => n is not null)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Scan all type references in the current file
                var referencedTypes = CollectReferencedTypeNames(file);

                if (importedTypeNames.Any(name => referencedTypes.Contains(name!)))
                    isUsed = true;

                if (!isUsed)
                {
                    Info(IonAnalyticCodes.ION1002_UnusedImport, use, importedPath);
                }
            }
        }
    }

    private void DetectUnusedTypes()
    {
        // Collect all type names defined across all files
        var allDefinedTypes = new Dictionary<string, IonSyntaxMember>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Context.Files)
        {
            foreach (var def in file.Definitions)
            {
                var name = GetDefinitionName(def);
                if (name is not null)
                    allDefinedTypes.TryAdd(name, def);
            }
        }

        // Collect all referenced type names across all files
        var allReferencedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Context.Files)
        {
            foreach (var name in CollectReferencedTypeNames(file))
                allReferencedTypes.Add(name);
        }

        // Check which defined types are never referenced
        foreach (var (name, def) in allDefinedTypes)
        {
            // Services are entry points — never flag as unused
            if (def is IonServiceSyntax) continue;
            // Attribute definitions are metadata — never flag as unused
            if (def is IonAttributeDefSyntax) continue;

            if (!allReferencedTypes.Contains(name))
            {
                Info(IonAnalyticCodes.ION1001_UnusedType, def, name);
            }
        }
    }

    private static string? GetDefinitionName(IonSyntaxMember def) => def switch
    {
        IonMessageSyntax msg => msg.Name.Identifier,
        IonEnumSyntax e => e.Name.Identifier,
        IonFlagsSyntax f => f.Name.Identifier,
        IonServiceSyntax s => s.serviceName.Identifier,
        IonTypedefSyntax t => t.TypeName.Name.Identifier,
        IonUnionSyntax u => u.unionName.Identifier,
        IonAttributeDefSyntax a => a.Name.Identifier,
        _ => null
    };

    private static HashSet<string> CollectReferencedTypeNames(IonFileSyntax file)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var msg in file.messageSyntaxes)
        {
            foreach (var field in msg.Fields)
                CollectTypeRefs(field.Type, refs);
        }

        // A mixin's field types are real references: a msg used only by a mixin is used, and
        // reporting it as ION1001 would tell the author to delete something the wire depends on.
        // The `with` clause names go in too — they are not type references, but the set is also
        // read by the #use / #import passes, and importing a file for its mixin is a real use of it.
        foreach (var mixin in file.mixinSyntaxes)
        {
            foreach (var field in mixin.Fields)
                CollectTypeRefs(field.Type, refs);

            foreach (var included in mixin.Mixins ?? [])
                refs.Add(included.Identifier);
        }

        foreach (var msg in file.messageSyntaxes)
        foreach (var included in msg.Mixins ?? [])
            refs.Add(included.Identifier);

        foreach (var svc in file.serviceSyntaxes)
        {
            foreach (var arg in svc.BaseArguments)
                CollectTypeRefs(arg.type, refs);

            foreach (var method in svc.Methods)
            {
                foreach (var arg in method.arguments)
                    CollectTypeRefs(arg.type, refs);
                if (method.returnType is not null)
                    CollectTypeRefs(method.returnType, refs);
            }
        }

        foreach (var union in file.unionSyntaxes)
        {
            foreach (var field in union.baseFields)
                CollectTypeRefs(field.type, refs);
            foreach (var c in union.cases)
            {
                refs.Add(c.caseName.Name.Identifier);
                foreach (var arg in c.arguments)
                    CollectTypeRefs(arg.type, refs);
            }
        }

        foreach (var td in file.typedefSyntaxes)
        {
            if (td.BaseType is not null)
                CollectTypeRefs(td.BaseType, refs);
        }

        return refs;
    }

    /// <remarks>
    /// Recurses through <c>IonTypeParameterSyntax.Type</c>, not <c>.Name</c>. <c>.Name</c> is only
    /// the argument's head name, so <c>Map&lt;string, Array&lt;User&gt;&gt;</c> used to register
    /// <c>Array</c> and never <c>User</c> — and <c>User</c> was then reported as an unused type
    /// (ION1001) while being a live part of the wire format. The inline body arm is for a
    /// <c>msg { … }</c> that <c>InlineTypeHoistingStage</c> could not hoist; a hoisted one is an
    /// ordinary message by the time this runs.
    /// </remarks>
    private static void CollectTypeRefs(IonUnderlyingTypeSyntax type, HashSet<string> refs)
    {
        refs.Add(type.Name.Identifier);

        foreach (var generic in type.generics)
        {
            if (generic.Type is { } argument)
                CollectTypeRefs(argument, refs);
            else
                refs.Add(generic.Name.Identifier);
        }

        foreach (var field in type.InlineBody?.Fields ?? [])
            CollectTypeRefs(field.Type, refs);
    }
}
