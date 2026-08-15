namespace IonTestClientServer;

using ion.runtime;
using System.Formats.Cbor;
using System.Text.Json;

/// <summary>
/// Cross-runtime golden vectors for Ion's three float widths.
/// <para>
/// <c>/tests/golden/float.golden.json</c> is also consumed by
/// <c>packages/ion.webcore.js/test/float.golden.test.ts</c> and
/// <c>packages/ion.rustcore/tests/float_golden.rs</c>. All three runtimes must produce
/// byte-identical CBOR for the same value — that shared file is the contract.
/// </para>
/// </summary>
public class FloatGoldenTests
{
    // ── encode: value built from the IEEE bit pattern must produce exactly `hex` ──

    [Test]
    [TestCaseSource(nameof(VectorNames))]
    public void Golden_Encode(string name)
    {
        var v = FloatGoldenVectors.Get(name);

        Assert.That(Encode(v.Type, ValueFromBits(v.Type, v.Bits)), Is.EqualTo(v.Hex),
            $"golden float vector '{name}' ({v.Repr}): {v.Notes}");
    }

    // ── decode: `hex` must read back and re-encode to itself ──

    [Test]
    [TestCaseSource(nameof(VectorNames))]
    public void Golden_Decode(string name)
    {
        var v = FloatGoldenVectors.Get(name);

        Assert.That(Reencode(v.Type, v.Hex), Is.EqualTo(v.Hex),
            $"golden float vector '{name}' ({v.Repr}): {v.Notes}");
    }

    /// <summary>
    /// Readers accept every wire width for every declared width, in both directions — including
    /// the shrunken payloads the previous C# release wrote, which is what makes this change
    /// non-wire-breaking.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(CrossWidthNames))]
    public void Golden_CrossWidthRead(string name)
    {
        var v = FloatGoldenVectors.GetCrossWidth(name);

        Assert.That(Reencode(v.Type, v.Hex), Is.EqualTo(v.ReencodedHex),
            $"cross-width vector '{name}': {v.Notes}");
    }

    // ── the specific claims the golden file makes about the writer ──

    /// <summary>
    /// The declared width is honoured regardless of value: <c>f2</c> is always 3 bytes on the
    /// wire, <c>f4</c> always 5, <c>f8</c> always 9. This is the whole point of the fix — the
    /// old writer emitted 3 bytes for a <c>f8</c> field holding 1.5.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(VectorNames))]
    public void Golden_LengthIsAlwaysTheDeclaredWidth(string name)
    {
        var v = FloatGoldenVectors.Get(name);
        var expected = v.Type switch { "f2" => 3, "f4" => 5, "f8" => 9, _ => throw new NotSupportedException(v.Type) };

        Assert.That(v.Hex, Has.Length.EqualTo(expected * 2), $"'{name}' must occupy {expected} wire bytes");
        Assert.That(Encode(v.Type, ValueFromBits(v.Type, v.Bits)), Has.Length.EqualTo(expected * 2));
    }

    /// <summary>
    /// R-FLOAT GUARD. .NET's <see cref="float.NaN"/> has the sign bit set (<c>ffc00000</c>) while
    /// Rust and JS produce <c>7fc00000</c>; a NaN with a payload is a third pattern again. All of
    /// them must leave the writer as the canonical positive quiet NaN, or NaN would be the one
    /// value on which the three runtimes still disagreed.
    /// </summary>
    [Test]
    public void NaNIsCanonicalisedRegardlessOfSignOrPayload()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Encode("f2", Half.NaN), Is.EqualTo("f97e00"));
            Assert.That(Encode("f4", float.NaN), Is.EqualTo("fa7fc00000"));
            Assert.That(Encode("f8", double.NaN), Is.EqualTo("fb7ff8000000000000"));

            // .NET's own NaN constants are sign-set; prove that is what we just canonicalised.
            Assert.That(BitConverter.SingleToUInt32Bits(float.NaN), Is.EqualTo(0xFFC00000u));
            Assert.That(BitConverter.DoubleToUInt64Bits(double.NaN), Is.EqualTo(0xFFF8000000000000ul));

            // Sign-set NaN carrying a payload, and a signalling NaN.
            Assert.That(Encode("f4", BitConverter.UInt32BitsToSingle(0xFFC00001)), Is.EqualTo("fa7fc00000"));
            Assert.That(Encode("f4", BitConverter.UInt32BitsToSingle(0x7F800001)), Is.EqualTo("fa7fc00000"));
            Assert.That(Encode("f8", BitConverter.UInt64BitsToDouble(0xFFF8000000000001)), Is.EqualTo("fb7ff8000000000000"));
            Assert.That(Encode("f2", BitConverter.UInt16BitsToHalf(0xFE00)), Is.EqualTo("f97e00"));
        });
    }

    /// <summary>
    /// <c>-0.0</c> is the counterpart to the NaN rule: it must NOT be canonicalised. It is a
    /// distinct value that survives a round-trip, including through a legacy shrunken payload.
    /// </summary>
    [Test]
    public void NegativeZeroIsPreservedAndDistinctFromPositiveZero()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Encode("f4", -0.0f), Is.EqualTo("fa80000000"));
            Assert.That(Encode("f4", 0.0f), Is.EqualTo("fa00000000"));
            Assert.That(Encode("f8", -0.0d), Is.EqualTo("fb8000000000000000"));
            Assert.That(Encode("f2", (Half)(-0.0f)), Is.EqualTo("f98000"));

            var back = IonFormatterStorage<float>.Read(new CborReader(Convert.FromHexString("fa80000000")));
            Assert.That(BitConverter.SingleToUInt32Bits(back), Is.EqualTo(0x80000000u), "-0.0 must not decay to +0.0");
        });
    }

    /// <summary>
    /// Half (<c>f2</c>) was already correct and must stay that way: <c>0xF9</c> + 2 bytes, never
    /// widened by the fix that widened <c>f4</c>/<c>f8</c>.
    /// </summary>
    [Test]
    public void HalfStillWritesTwoPayloadBytes()
    {
        foreach (var v in FloatGoldenVectors.All.Where(x => x.Type == "f2"))
        {
            var hex = Encode("f2", ValueFromBits("f2", v.Bits));
            Assert.That(hex, Does.StartWith("f9").And.Length.EqualTo(6), $"f2 vector '{v.Name}'");
        }
    }

    /// <summary>
    /// The escape hatch must not depend on the writer's conformance mode — in particular it must
    /// not require <c>Ctap2Canonical</c>, which would also mandate sorted map keys and forbid
    /// indefinite lengths and so break <see cref="IonPartial{T}"/>'s declaration-order map.
    /// </summary>
    [Test]
    public void DeclaredWidthHoldsInEveryConformanceMode()
    {
        foreach (var mode in Enum.GetValues<CborConformanceMode>())
        {
            var w = new CborWriter(mode);
            IonFormatterStorage<float>.Write(w, 1.5f);
            Assert.That(Convert.ToHexString(w.Encode()).ToLowerInvariant(), Is.EqualTo("fa3fc00000"),
                $"conformance mode {mode}");
        }
    }

    /// <summary>A float written inside a container still counts as exactly one item.</summary>
    [Test]
    public void FloatsNestInContainersCorrectly()
    {
        var w = new CborWriter();
        w.WriteStartArray(3);
        IonFormatterStorage<Half>.Write(w, (Half)1.5f);
        IonFormatterStorage<float>.Write(w, 1.5f);
        IonFormatterStorage<double>.Write(w, 1.5d);
        w.WriteEndArray();

        Assert.That(Convert.ToHexString(w.Encode()).ToLowerInvariant(),
            Is.EqualTo("83" + "f93e00" + "fa3fc00000" + "fb3ff8000000000000"));
    }

    // ── helpers ──

    private static string Encode<T>(T value)
    {
        var w = new CborWriter();
        IonFormatterStorage<T>.Write(w, value);
        return Convert.ToHexString(w.Encode()).ToLowerInvariant();
    }

    private static string Encode(string type, object value) => type switch
    {
        "f2" => Encode((Half)value),
        "f4" => Encode((float)value),
        "f8" => Encode((double)value),
        _ => throw new NotSupportedException(type)
    };

    private static string Reencode(string type, string hex)
    {
        var reader = new CborReader(Convert.FromHexString(hex));
        return type switch
        {
            "f2" => Encode(IonFormatterStorage<Half>.Read(reader)),
            "f4" => Encode(IonFormatterStorage<float>.Read(reader)),
            "f8" => Encode(IonFormatterStorage<double>.Read(reader)),
            _ => throw new NotSupportedException(type)
        };
    }

    private static object ValueFromBits(string type, string bits) => type switch
    {
        "f2" => BitConverter.UInt16BitsToHalf(ushort.Parse(bits, System.Globalization.NumberStyles.HexNumber)),
        "f4" => BitConverter.UInt32BitsToSingle(uint.Parse(bits, System.Globalization.NumberStyles.HexNumber)),
        "f8" => BitConverter.UInt64BitsToDouble(ulong.Parse(bits, System.Globalization.NumberStyles.HexNumber)),
        _ => throw new NotSupportedException(type)
    };

    private static IEnumerable<string> VectorNames() => FloatGoldenVectors.All.Select(v => v.Name);

    private static IEnumerable<string> CrossWidthNames() => FloatGoldenVectors.CrossWidth.Select(v => v.Name);
}

/// <summary>Loader for the shared cross-runtime vectors in <c>/tests/golden/float.golden.json</c>.</summary>
public static class FloatGoldenVectors
{
    public sealed record Vector(string Name, string Type, string Bits, string Repr, string Hex, string Notes);

    public sealed record CrossWidthVector(string Name, string Type, string Hex, string ReencodedHex, string Notes);

    private static readonly Lazy<(Vector[] Vectors, CrossWidthVector[] CrossWidth)> loaded = new(Load);

    public static Vector[] All => loaded.Value.Vectors;

    public static CrossWidthVector[] CrossWidth => loaded.Value.CrossWidth;

    public static Vector Get(string name) =>
        All.FirstOrDefault(v => v.Name == name)
        ?? throw new InvalidOperationException($"Golden float vector '{name}' not found");

    public static CrossWidthVector GetCrossWidth(string name) =>
        CrossWidth.FirstOrDefault(v => v.Name == name)
        ?? throw new InvalidOperationException($"Cross-width vector '{name}' not found");

    private static (Vector[], CrossWidthVector[]) Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "golden", "float.golden.json");
            if (File.Exists(candidate))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
                static string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.GetString() ?? "" : "";

                var vectors = doc.RootElement.GetProperty("vectors").EnumerateArray().Select(v => new Vector(
                    v.GetProperty("name").GetString()!,
                    v.GetProperty("type").GetString()!,
                    v.GetProperty("bits").GetString()!,
                    Str(v, "repr"),
                    v.GetProperty("hex").GetString()!,
                    Str(v, "notes"))).ToArray();

                var cross = doc.RootElement.GetProperty("crossWidth").EnumerateArray().Select(v => new CrossWidthVector(
                    v.GetProperty("name").GetString()!,
                    v.GetProperty("type").GetString()!,
                    v.GetProperty("hex").GetString()!,
                    v.GetProperty("reencodedHex").GetString()!,
                    Str(v, "notes"))).ToArray();

                return (vectors, cross);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate tests/golden/float.golden.json above " + AppContext.BaseDirectory);
    }
}
