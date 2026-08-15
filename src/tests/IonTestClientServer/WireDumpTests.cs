namespace IonTestClientServer;

using ion.runtime;
using System.Formats.Cbor;
using System.Numerics;
using System.Text;

/// <summary>
/// Emits this runtime's half of the cross-runtime byte-equality proof.
/// <para>
/// Asserting each runtime against the golden JSON already implies the three agree. This goes one
/// step further and writes out what each runtime's <b>real formatters actually produced</b>, so
/// the claim can be checked by literally diffing three files rather than by trusting three
/// separate assertion suites:
/// </para>
/// <code>
///   tests/golden/.dump/cs.txt     &lt;- this test
///   tests/golden/.dump/ts.txt     &lt;- packages/ion.webcore.js/test/wiredump.test.ts
///   tests/golden/.dump/rust.txt   &lt;- packages/ion.rustcore/tests/wiredump.rs
///   diff cs.txt ts.txt &amp;&amp; diff cs.txt rust.txt
/// </code>
/// <para>
/// Nothing in the dump is copied from the golden file's <c>hex</c> field — every line is produced
/// by encoding a value through <see cref="IonFormatterStorage{T}"/> and friends. The format is
/// <c>section/name TAB hex</c>, one line per vector, in golden-file order.
/// </para>
/// </summary>
public class WireDumpTests
{
    /// <summary>Where all three runtimes write their dump.</summary>
    public static string DumpDirectory => Path.Combine(GoldenFile.Directory, ".dump");

    [Test]
    public void EmitWireDump()
    {
        var sb = new StringBuilder();

        // ── float: the precedent this whole exercise follows ──
        sb.AppendLine($"float/f2-1.5\t{Encode<Half>((Half)1.5f)}");
        sb.AppendLine($"float/f4-1.5\t{Encode(1.5f)}");
        sb.AppendLine($"float/f8-1.5\t{Encode(1.5d)}");
        sb.AppendLine($"float/f4-nan\t{Encode(float.NaN)}");

        // ── datetime ──
        foreach (var v in DateTimeGoldenTests.All)
            sb.AppendLine($"datetime/{v.Name}\t{Encode(DateTimeGoldenTests.Build(v))}");

        // ── decimal ── via the parts API, so out-of-C#-range vectors are covered too
        foreach (var v in DecimalGoldenTests.All)
        {
            var w = new CborWriter();
            w.WriteIonDecimalParts(v.Exponent, v.Mantissa);
            sb.AppendLine($"decimal/{v.Name}\t{GoldenFile.Hex(w)}");
        }

        // ── map / set / fixed array ──
        foreach (var v in CollectionGoldenTests.Maps)
            sb.AppendLine($"map/{v.Name}\t{DumpMap(v)}");

        foreach (var v in CollectionGoldenTests.Sets)
            sb.AppendLine($"set/{v.Name}\t{DumpSet(v)}");

        foreach (var v in CollectionGoldenTests.Fixeds)
            sb.AppendLine($"fixed/{v.Name}\t{DumpFixed(v)}");

        System.IO.Directory.CreateDirectory(DumpDirectory);
        // "\n" explicitly: the three runtimes must produce byte-identical FILES, and Windows'
        // default "\r\n" would make the C# dump differ from the Rust and TypeScript ones for a
        // reason that has nothing to do with the wire format.
        File.WriteAllText(Path.Combine(DumpDirectory, "cs.txt"), sb.ToString().ReplaceLineEndings("\n"));

        Assert.That(sb.Length, Is.GreaterThan(0));
        TestContext.Out.WriteLine($"wire dump written to {Path.Combine(DumpDirectory, "cs.txt")}");
    }

    private static string Encode<T>(T value)
    {
        var w = new CborWriter();
        IonFormatterStorage<T>.Write(w, value);
        return GoldenFile.Hex(w);
    }

    // Containers are dumped by round-tripping the vector through the real formatters: read the
    // pinned bytes, write them back. That exercises both directions, and — because the writer
    // sorts — the output is the encoder's own opinion of canonical order, not a copy of the input.
    private static string DumpMap(CollectionGoldenTests.MapVector v)
        => ReencodeMap(v.Hex, v.KeyType, v.ValueType);

    private static string DumpSet(CollectionGoldenTests.SetVector v)
        => ReencodeSet(v.Hex, v.ElementType);

    private static string DumpFixed(CollectionGoldenTests.FixedVector v)
        => ReencodeFixed(v.Hex, v.ElementType, v.Length);

    private static string ReencodeMap(string hex, string keyType, string valueType)
        => (keyType, valueType) switch
        {
            ("string", "i4") => Map<string, int>(hex),
            ("string", "string") => Map<string, string>(hex),
            ("i4", "i4") => Map<int, int>(hex),
            ("u4", "i4") => Map<uint, int>(hex),
            ("i8", "i4") => Map<long, int>(hex),
            ("guid", "i4") => Map<Guid, int>(hex),
            ("bool", "i4") => Map<bool, int>(hex),
            _ => throw new NotSupportedException($"map<{keyType},{valueType}>")
        };

    private static string Map<TK, TV>(string hex) where TK : notnull
    {
        var map = IonMapFormatter<TK, TV>.Read(new CborReader(GoldenFile.Bytes(hex)));
        var w = new CborWriter();
        IonMapFormatter<TK, TV>.Write(w, map);
        return GoldenFile.Hex(w);
    }

    private static string ReencodeSet(string hex, string elementType) => elementType switch
    {
        "i4" => Set<int>(hex),
        "string" => Set<string>(hex),
        "guid" => Set<Guid>(hex),
        _ => throw new NotSupportedException($"set<{elementType}>")
    };

    private static string Set<T>(string hex)
    {
        var set = IonSetFormatter<T>.Read(new CborReader(GoldenFile.Bytes(hex)));
        var w = new CborWriter();
        IonSetFormatter<T>.Write(w, set);
        return GoldenFile.Hex(w);
    }

    private static string ReencodeFixed(string hex, string elementType, int length) => elementType switch
    {
        "i4" => Fixed<int>(hex, length),
        "u1" => Fixed<byte>(hex, length),
        "string" => Fixed<string>(hex, length),
        "guid" => Fixed<Guid>(hex, length),
        _ => throw new NotSupportedException($"{elementType}[{length}]")
    };

    private static string Fixed<T>(string hex, int length)
    {
        var array = IonFixedArrayFormatter<T>.Read(new CborReader(GoldenFile.Bytes(hex)), length);
        var w = new CborWriter();
        IonFixedArrayFormatter<T>.Write(w, array, length);
        return GoldenFile.Hex(w);
    }
}
