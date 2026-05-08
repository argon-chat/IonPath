namespace ion.compiler.CodeGen;

using System.Xml.Linq;

/// <summary>
/// Patches an existing .csproj file to add/update ProjectReference entries
/// for ion module dependencies without overwriting other content.
/// </summary>
public static class CsprojModulePatcher
{
    private const string IonModuleComment = "IonPath Module Dependencies";

    /// <summary>
    /// Ensures the csproj at <paramref name="csprojPath"/> contains ProjectReferences
    /// for the given module dependency paths. Creates the file only if it doesn't exist.
    /// Returns true if the file was modified.
    /// </summary>
    public static bool EnsureProjectReferences(string csprojPath, IReadOnlyList<ModuleProjectReference> references)
    {
        if (references.Count == 0)
            return false;

        if (!File.Exists(csprojPath))
            return false; // Don't create csproj, only patch existing ones

        var doc = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
        var root = doc.Root;

        if (root is null)
            return false;

        var ns = root.GetDefaultNamespace();
        var modified = false;

        // Find or create the ItemGroup for ion module references
        var ionItemGroup = FindIonModuleItemGroup(root, ns);

        if (ionItemGroup is null)
        {
            ionItemGroup = new XElement(ns + "ItemGroup",
                new XComment($" {IonModuleComment} "));
            root.Add(ionItemGroup);
            modified = true;
        }

        // Get existing ion module ProjectReferences
        var existingRefs = ionItemGroup.Elements(ns + "ProjectReference")
            .ToDictionary(
                e => e.Attribute("Include")?.Value ?? "",
                e => e,
                StringComparer.OrdinalIgnoreCase);

        // Determine desired state
        var desiredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var moduleRef in references)
        {
            var relativePath = moduleRef.RelativeCsprojPath;
            desiredPaths.Add(relativePath);

            if (existingRefs.ContainsKey(relativePath))
                continue;

            // Add new reference
            var refElement = new XElement(ns + "ProjectReference",
                new XAttribute("Include", relativePath));

            ionItemGroup.Add(refElement);
            modified = true;
        }

        // Remove stale references that are no longer needed
        foreach (var (path, element) in existingRefs)
        {
            if (desiredPaths.Contains(path))
                continue;

            element.Remove();
            modified = true;
        }

        if (modified)
        {
            doc.Save(csprojPath);
        }

        return modified;
    }

    private static XElement? FindIonModuleItemGroup(XElement root, XNamespace ns)
    {
        // Look for an ItemGroup that has our marker comment
        foreach (var itemGroup in root.Elements(ns + "ItemGroup"))
        {
            var hasMarker = itemGroup.Nodes()
                .OfType<XComment>()
                .Any(c => c.Value.Contains(IonModuleComment));

            if (hasMarker)
                return itemGroup;
        }

        return null;
    }
}

/// <summary>
/// Represents a module dependency that needs a ProjectReference in the csproj.
/// </summary>
public sealed record ModuleProjectReference(
    string ModuleName,
    string RelativeCsprojPath);
