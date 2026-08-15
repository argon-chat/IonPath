namespace IonTestClientServer;

using System.Formats.Cbor;
using System.Text.Json;

/// <summary>
/// Locates and caches the shared cross-runtime vector files in <c>/tests/golden</c>.
/// <para>
/// Every file under that directory is consumed by all three runtimes — C# here,
/// <c>packages/ion.webcore.js/test/*.golden.test.ts</c> and
/// <c>packages/ion.rustcore/tests/*_golden.rs</c>. The files are the contract; these tests are
/// one of its three readers.
/// </para>
/// </summary>
public static class GoldenFile
{
    private static readonly Dictionary<string, JsonDocument> cache = new();

    /// <summary>The repository's <c>tests/golden</c> directory.</summary>
    public static string Directory { get; } = Locate();

    /// <summary>Loads (and caches) a golden file by name, e.g. <c>"decimal.golden.json"</c>.</summary>
    public static JsonElement Load(string fileName)
    {
        lock (cache)
        {
            if (!cache.TryGetValue(fileName, out var doc))
            {
                doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, fileName)));
                cache[fileName] = doc;
            }
            return doc.RootElement;
        }
    }

    /// <summary>Optional string property, empty when absent.</summary>
    public static string Str(this JsonElement e, string property)
        => e.TryGetProperty(property, out var v) ? v.GetString() ?? "" : "";

    public static string Hex(CborWriter writer)
        => Convert.ToHexString(writer.Encode()).ToLowerInvariant();

    public static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "golden");
            if (System.IO.Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "float.golden.json")))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate tests/golden above " + AppContext.BaseDirectory);
    }
}
