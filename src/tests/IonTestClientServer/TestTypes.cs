namespace IonTestClientServer;

using ion.runtime;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text.Json;
using TestContracts;

/// <summary>
/// Stand-in for a generated <c>msg</c>, mirroring the golden-vector message in
/// <c>/tests/golden/partial.golden.json</c>:
/// <code>msg GoldenPatchTarget { n: i4; f: f4; s: string; items: i4[]; note: string?; }</code>
/// </summary>
public sealed record GoldenPatchTarget(int n, float f, string s, IonArray<int> items, IonMaybe<string> note);

public class TestTypes
{
    /// <summary>
    /// What generated code will emit for <c>GoldenPatchTarget~</c>.
    /// Registration is idempotent and order-independent w.r.t. formatter registration.
    /// </summary>
    [OneTimeSetUp]
    public void RegisterPartialSchemas() =>
        IonPartialSchema<GoldenPatchTarget>.Register(
            IonPartialSchema<GoldenPatchTarget>.Field<int>("n"),
            IonPartialSchema<GoldenPatchTarget>.Field<float>("f"),
            IonPartialSchema<GoldenPatchTarget>.Field<string>("s"),
            IonPartialSchema<GoldenPatchTarget>.Array<int>("items"),
            IonPartialSchema<GoldenPatchTarget>.Maybe<string>("note"));

    [Test]
    public void TestDateOnly()
    {
        var date = new DateTime(1992, 12, 4, 12, 19, 5);
        var dateonly = DateOnly.FromDateTime(date);

        var writer = new CborWriter();
        writer.WriteStartArray(1);


        IonFormatterStorage<DateOnly>.Write(writer, dateonly);

        writer.WriteEndArray();


        var reader = new CborReader(writer.Encode());


        reader.ReadStartArray();

        var dateOriginal = IonFormatterStorage<DateOnly>.Read(reader);

        reader.ReadEndArray();
    }

    // ── Partial<T> ──────────────────────────────────────────────────────────

    /// <summary>
    /// R3 regression guard, asserted on the encoded bytes.
    /// <para>
    /// The previous version of this test called <c>Removed()</c> on a <c>float</c> field and
    /// asserted the decoded result was <c>0</c> — which is satisfied identically whether
    /// removal worked or was corrupted into "set to zero", so it masked the bug it was
    /// supposed to catch. Clearing a value-typed field must encode as CBOR null (0xF6).
    /// </para>
    /// <para>
    /// <c>Vector</c> has no generated partial schema, so this also exercises the
    /// reflection-derived fallback schema.
    /// </para>
    /// </summary>
    [Test]
    public void TestPartial_ClearedFloatEncodesAsNull()
    {
        var p = new IonPartial<Vector>();

        p.SetField(x => x.x, PartialField<float>.Modified(1.1f));
        p.SetField(x => x.y, PartialField<float>.Removed());

        var writer = new CborWriter();
        IonFormatterStorage<IonPartial<Vector>>.Write(writer, p);
        var hex = Convert.ToHexString(writer.Encode()).ToLowerInvariant();

        //  a2                map(2)
        //    61 78           "x"
        //    fa 3f8ccccd     1.1f
        //    61 79           "y"
        //    f6              null  <- cleared, NOT the field's default value
        Assert.That(hex, Is.EqualTo("a26178fa3f8ccccd6179f6"),
            "Removed() on a float field must write CBOR null, not 0.");
        // 'fa00000000' is what a defaulted f4 field encodes as now that every runtime writes the
        // declared width; 'f90000' is what C# wrote for it before that rule landed. Neither may appear.
        Assert.That(hex, Does.Not.Contain("fa00000000").And.Not.Contain("f90000"),
            "R3: removal must not be encoded as the field's default value.");

        var pOriginal = IonFormatterStorage<IonPartial<Vector>>.Read(new CborReader(writer.Encode()));

        Assert.That(pOriginal.StateOf("x"), Is.EqualTo(PartialState.Modified));
        Assert.That(pOriginal.StateOf("y"), Is.EqualTo(PartialState.Removed));
        Assert.That(pOriginal.StateOf("z"), Is.EqualTo(PartialState.None));
        Assert.That(pOriginal.GetField(v => v.x).Value, Is.EqualTo(1.1f));

        var original = new Vector(2, 4, 8);
        pOriginal
            .On(x => x.x, x => original = original with { x = x })
            .On(x => x.y, y => original = original with { y = y })
            .On(x => x.z, z => original = original with { z = z });

        Assert.That(original.x, Is.EqualTo(1.1f));
        Assert.That(original.y, Is.EqualTo(0));
        Assert.That(original.z, Is.EqualTo(8));
    }

    [Test]
    [TestCaseSource(nameof(EncodeVectorNames))]
    public void Golden_Encode(string name)
    {
        var vector = GoldenVectors.Get(name);

        var writer = new CborWriter();
        IonFormatterStorage<IonPartial<GoldenPatchTarget>>.Write(writer, BuildPatch(name));

        Assert.That(Convert.ToHexString(writer.Encode()).ToLowerInvariant(),
            Is.EqualTo(vector.Hex), $"golden vector '{name}': {vector.Notes}");
    }

    [Test]
    [TestCaseSource(nameof(DecodeVectorNames))]
    public void Golden_Decode(string name)
    {
        var vector = GoldenVectors.Get(name);

        var reader = new CborReader(Convert.FromHexString(vector.Hex));
        var decoded = IonFormatterStorage<IonPartial<GoldenPatchTarget>>.Read(reader);

        var writer = new CborWriter();
        IonFormatterStorage<IonPartial<GoldenPatchTarget>>.Write(writer, decoded);

        Assert.That(Convert.ToHexString(writer.Encode()).ToLowerInvariant(),
            Is.EqualTo(vector.ReencodedHex ?? vector.Hex), $"golden vector '{name}': {vector.Notes}");
    }

    [Test]
    public void Golden_ClearedAndModifiedNoneAreTheSameBytes()
    {
        Assert.That(GoldenVectors.Get("modified-optional-none").Hex,
            Is.EqualTo(GoldenVectors.Get("cleared-optional").Hex),
            "R4: 'modified to none' and 'cleared' are deliberately the same patch on the wire.");
    }

    [Test]
    public void Partial_UnsetFieldIsUntouched()
    {
        var p = new IonPartial<GoldenPatchTarget>();
        p.SetField(x => x.n, PartialField<int>.Modified(1));
        p.SetField(x => x.n, PartialField<int>.None);

        Assert.That(p.Count, Is.EqualTo(0));
        Assert.That(p.StateOf("n"), Is.EqualTo(PartialState.None));
    }

    [Test]
    public void Partial_FieldOutsideSchemaThrows()
    {
        var p = new IonPartial<GoldenPatchTarget>();
        p.SetField<int>("nope", PartialField<int>.Modified(1));

        Assert.Throws<InvalidOperationException>(() =>
            IonFormatterStorage<IonPartial<GoldenPatchTarget>>.Write(new CborWriter(), p));
    }

    private static IEnumerable<string> EncodeVectorNames() =>
        GoldenVectors.All.Where(v => v.Direction is "encode" or "roundtrip").Select(v => v.Name);

    private static IEnumerable<string> DecodeVectorNames() =>
        GoldenVectors.All.Where(v => v.Direction is "decode" or "roundtrip").Select(v => v.Name);

    private static IonPartial<GoldenPatchTarget> BuildPatch(string name)
    {
        var p = new IonPartial<GoldenPatchTarget>();
        switch (name)
        {
            case "empty":
                break;
            case "modified-scalar-int":
                p.SetField(x => x.n, PartialField<int>.Modified(7));
                break;
            case "modified-scalar-float":
                p.SetField(x => x.f, PartialField<float>.Modified(1.1f));
                break;
            case "modified-scalar-float-half-representable":
                p.SetField(x => x.f, PartialField<float>.Modified(1.5f));
                break;
            case "cleared-scalar-float":
                p.SetField(x => x.f, PartialField<float>.Removed());
                break;
            case "cleared-scalar-reference":
                p.SetField(x => x.s, PartialField<string>.Removed());
                break;
            case "modified-array":
                p.SetField(x => x.items, PartialField<IonArray<int>>.Modified(new IonArray<int>([1, 2, 3])));
                break;
            case "cleared-array":
                p.SetField(x => x.items, PartialField<IonArray<int>>.Removed());
                break;
            case "modified-optional-some":
                p.SetField(x => x.note, PartialField<IonMaybe<string>>.Modified(IonMaybe<string>.Some("hi")));
                break;
            case "cleared-optional":
                p.SetField(x => x.note, PartialField<IonMaybe<string>>.Removed());
                break;
            case "modified-optional-none":
                p.SetField(x => x.note, PartialField<IonMaybe<string>>.Modified(IonMaybe<string>.None));
                break;
            case "all-fields":
                // deliberately NOT in declaration order — the encoder must reorder
                p.SetField(x => x.note, PartialField<IonMaybe<string>>.Modified(IonMaybe<string>.Some("hi")));
                p.SetField(x => x.items, PartialField<IonArray<int>>.Modified(new IonArray<int>([1, 2, 3])));
                p.SetField(x => x.s, PartialField<string>.Modified("ab"));
                p.SetField(x => x.f, PartialField<float>.Removed());
                p.SetField(x => x.n, PartialField<int>.Modified(7));
                break;
            default:
                throw new NotSupportedException($"No C# builder for golden vector '{name}'");
        }

        return p;
    }
}

/// <summary>Loader for the shared cross-runtime vectors in <c>/tests/golden/partial.golden.json</c>.</summary>
public static class GoldenVectors
{
    public sealed record Vector(string Name, string Direction, string Hex, string? ReencodedHex, string Notes);

    private static readonly Lazy<Vector[]> vectors = new(Load);

    public static Vector[] All => vectors.Value;

    public static Vector Get(string name) =>
        All.FirstOrDefault(v => v.Name == name)
        ?? throw new InvalidOperationException($"Golden vector '{name}' not found");

    public static string Path { get; private set; } = "";

    private static Vector[] Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "tests", "golden", "partial.golden.json");
            if (File.Exists(candidate))
            {
                Path = candidate;
                using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
                return doc.RootElement.GetProperty("vectors").EnumerateArray().Select(v => new Vector(
                    v.GetProperty("name").GetString()!,
                    v.GetProperty("direction").GetString()!,
                    v.GetProperty("hex").GetString()!,
                    v.TryGetProperty("reencodedHex", out var re) ? re.GetString() : null,
                    v.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "")).ToArray();
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate tests/golden/partial.golden.json above " + AppContext.BaseDirectory);
    }
}
