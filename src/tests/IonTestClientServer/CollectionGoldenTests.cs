namespace IonTestClientServer;

using ion.runtime;
using System.Formats.Cbor;
using System.Text.Json;

/// <summary>
/// Cross-runtime golden vectors for Ion's three container types: <c>Map&lt;K,V&gt;</c>,
/// <c>Set&lt;T&gt;</c> and the fixed-size array <c>T[N]</c>.
/// <para>
/// <c>/tests/golden/collections.golden.json</c> is also consumed by
/// <c>packages/ion.webcore.js/test/collections.golden.test.ts</c> and
/// <c>packages/ion.rustcore/tests/collections_golden.rs</c>.
/// </para>
/// </summary>
public class CollectionGoldenTests
{
    public sealed record MapVector(
        string Name, string KeyType, string ValueType, JsonElement Entries,
        string[] CanonicalKeyOrder, string Hex, string Notes);

    public sealed record MapDecode(
        string Name, string KeyType, string ValueType, string Hex, string ReencodedHex, string Notes);

    public sealed record SetVector(string Name, string ElementType, JsonElement Elements, string Hex, string Notes);

    public sealed record SetDecode(string Name, string ElementType, string Hex, string ReencodedHex, string Notes);

    public sealed record FixedVector(
        string Name, string ElementType, int Length, JsonElement Elements, string Hex, string Notes);

    public sealed record FixedDecode(
        string Name, string ElementType, int Length, string Hex, string ReencodedHex, string Notes);

    public sealed record FixedMalformed(
        string Name, string ElementType, int Length, int ActualLength, string Hex, string Notes);

    // ═══════════════════════════════════════════════════════════════════════
    //  Map<K,V>
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [TestCaseSource(nameof(MapNames))]
    public void Map_Encode(string name)
    {
        var v = Maps.First(x => x.Name == name);
        Assert.That(EncodeMap(v, reverse: false), Is.EqualTo(v.Hex), $"map vector '{name}': {v.Notes}");
    }

    /// <summary>
    /// THE POINT OF SORTING. The same entries inserted in the opposite order must produce exactly
    /// the same bytes — otherwise a C# <see cref="Dictionary{TKey,TValue}"/>, a JavaScript
    /// <c>Map</c> and a Rust <c>HashMap</c> would each emit their own iteration order.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(MapNames))]
    public void Map_EncodeIsIndependentOfInsertionOrder(string name)
    {
        var v = Maps.First(x => x.Name == name);
        Assert.That(EncodeMap(v, reverse: true), Is.EqualTo(v.Hex),
            $"map vector '{name}' must not depend on insertion order");
    }

    [Test]
    [TestCaseSource(nameof(MapNames))]
    public void Map_Decode(string name)
    {
        var v = Maps.First(x => x.Name == name);
        Assert.That(ReencodeMap(v.KeyType, v.ValueType, v.Hex), Is.EqualTo(v.Hex),
            $"map vector '{name}': {v.Notes}");
    }

    /// <summary>The keys really are in the length-first order the file declares.</summary>
    [Test]
    [TestCaseSource(nameof(MapNames))]
    public void Map_KeysAreInCanonicalOrder(string name)
    {
        var v = Maps.First(x => x.Name == name);
        var encoded = EncodeMap(v, reverse: false);

        // The map header is 1 byte for every vector here (fewer than 24 entries).
        var body = encoded[2..];
        foreach (var key in v.CanonicalKeyOrder)
        {
            Assert.That(body, Does.StartWith(key), $"map vector '{name}': next key must be {key}");
            body = body[key.Length..];
            body = body[NextItemHexLength(body)..]; // skip the value
        }
        Assert.That(body, Is.Empty, $"map vector '{name}': trailing bytes after the declared keys");
    }

    [Test]
    [TestCaseSource(nameof(MapDecodeNames))]
    public void Map_DecodeOnly(string name)
    {
        var v = MapDecodes.First(x => x.Name == name);
        Assert.That(ReencodeMap(v.KeyType, v.ValueType, v.Hex), Is.EqualTo(v.ReencodedHex),
            $"map decode-only vector '{name}': {v.Notes}");
    }

    /// <summary>
    /// Duplicate keys are REJECTED, with a typed error. Last-wins and first-wins both make the
    /// decoded value depend on wire order — the very non-determinism sorting exists to remove.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(MapMalformedNames))]
    public void Map_DuplicateKeysRaiseTypedError(string name)
    {
        var v = MapMalformed.First(x => x.Name == name);
        Assert.That(() => ReencodeMap(v.KeyType, v.ValueType, v.Hex),
            Throws.InstanceOf<IonDuplicateMapKeyException>().And.InstanceOf<IonDecodeException>(),
            $"map malformed vector '{name}': {v.Notes}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Set<T>
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [TestCaseSource(nameof(SetNames))]
    public void Set_Encode(string name)
    {
        var v = Sets.First(x => x.Name == name);
        Assert.That(EncodeSet(v, reverse: false), Is.EqualTo(v.Hex), $"set vector '{name}': {v.Notes}");
    }

    [Test]
    [TestCaseSource(nameof(SetNames))]
    public void Set_EncodeIsIndependentOfInsertionOrder(string name)
    {
        var v = Sets.First(x => x.Name == name);
        Assert.That(EncodeSet(v, reverse: true), Is.EqualTo(v.Hex),
            $"set vector '{name}' must not depend on insertion order");
    }

    /// <summary>
    /// ORDER-INDEPENDENCE, stated as the golden file states it: two vectors holding the same
    /// elements in different authored orders are pinned to the *same* hex.
    /// </summary>
    [Test]
    public void Set_TwoInsertionOrdersProduceIdenticalBytes()
    {
        var a = Sets.First(x => x.Name == "insertion-order-a");
        var b = Sets.First(x => x.Name == "insertion-order-b");

        Assert.Multiple(() =>
        {
            Assert.That(a.Hex, Is.EqualTo(b.Hex), "the golden file itself must pin them to the same bytes");
            Assert.That(EncodeSet(a, false), Is.EqualTo(EncodeSet(b, false)));
            Assert.That(EncodeSet(a, false), Is.EqualTo("d9010283010203"));
        });
    }

    [Test]
    [TestCaseSource(nameof(SetNames))]
    public void Set_Decode(string name)
    {
        var v = Sets.First(x => x.Name == name);
        Assert.That(ReencodeSet(v.ElementType, v.Hex), Is.EqualTo(v.Hex), $"set vector '{name}': {v.Notes}");
    }

    /// <summary>Every set on the wire is tagged 258 — that is what distinguishes it from an array.</summary>
    [Test]
    [TestCaseSource(nameof(SetNames))]
    public void Set_IsAlwaysTagged258(string name)
    {
        var v = Sets.First(x => x.Name == name);
        Assert.That(v.Hex, Does.StartWith("d90102"), $"set vector '{name}': tag 258 is 0xd9 0x01 0x02");
        Assert.That(EncodeSet(v, false), Does.StartWith("d90102"));
    }

    [Test]
    [TestCaseSource(nameof(SetDecodeNames))]
    public void Set_DecodeOnly(string name)
    {
        var v = SetDecodes.First(x => x.Name == name);
        Assert.That(ReencodeSet(v.ElementType, v.Hex), Is.EqualTo(v.ReencodedHex),
            $"set decode-only vector '{name}': {v.Notes}");
    }

    [Test]
    [TestCaseSource(nameof(SetMalformedNames))]
    public void Set_MalformedRaisesTypedError(string name)
    {
        var v = SetMalformed.First(x => x.Name == name);
        Assert.That(() => ReencodeSet(v.ElementType, v.Hex), Throws.InstanceOf<IonDecodeException>(),
            $"set malformed vector '{name}': {v.Notes}");
    }

    [Test]
    public void Set_MalformedErrorsAreSpecificallyTyped()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => ReencodeSet("i4", SetMalformed.First(x => x.Name == "duplicate-elements").Hex),
                Throws.InstanceOf<IonDuplicateSetElementException>());
            Assert.That(() => ReencodeSet("i4", SetMalformed.First(x => x.Name == "wrong-tag").Hex),
                Throws.InstanceOf<IonUnexpectedTagException>());
            // An untagged array is Array<T>, not Set<T>, and is rejected rather than accepted as
            // a courtesy — accepting it would erase the distinction at the only point it can
            // still be checked.
            Assert.That(() => ReencodeSet("i4", SetMalformed.First(x => x.Name == "missing-tag").Hex),
                Throws.InstanceOf<IonMalformedValueException>());
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  T[N]
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [TestCaseSource(nameof(FixedNames))]
    public void Fixed_Encode(string name)
    {
        var v = Fixeds.First(x => x.Name == name);
        Assert.That(EncodeFixed(v), Is.EqualTo(v.Hex), $"fixed-array vector '{name}': {v.Notes}");
    }

    [Test]
    [TestCaseSource(nameof(FixedNames))]
    public void Fixed_Decode(string name)
    {
        var v = Fixeds.First(x => x.Name == name);
        Assert.That(ReencodeFixed(v.ElementType, v.Length, v.Hex), Is.EqualTo(v.Hex),
            $"fixed-array vector '{name}': {v.Notes}");
    }

    /// <summary>
    /// NO-BYTE-STRING GUARD. <c>u1[4]</c> is an array of four integers (<c>0x84 …</c>), not a
    /// 4-byte CBOR byte string (<c>0x44 01020304</c>). Special-casing it would make the wire type
    /// of a fixed array depend on its element type, which no reader could predict from the shape.
    /// </summary>
    [Test]
    public void Fixed_U1IsAnArrayOfIntegersNotAByteString()
    {
        var v = Fixeds.First(x => x.Name == "u1-n4");

        Assert.Multiple(() =>
        {
            Assert.That(v.Hex, Is.EqualTo("8401020304"));
            Assert.That(v.Hex, Does.Not.StartWith("44"), "must not collapse into a byte string");
            Assert.That(EncodeFixed(v), Is.EqualTo("8401020304"));
        });
    }

    [Test]
    [TestCaseSource(nameof(FixedDecodeNames))]
    public void Fixed_DecodeOnly(string name)
    {
        var v = FixedDecodes.First(x => x.Name == name);
        Assert.That(ReencodeFixed(v.ElementType, v.Length, v.Hex), Is.EqualTo(v.ReencodedHex),
            $"fixed-array decode-only vector '{name}': {v.Notes}");
    }

    /// <summary>
    /// THE ENTIRE POINT OF THE FEATURE. A wrong length is a typed error that names BOTH lengths —
    /// knowing only that the length was wrong does not tell a caller whether the peer is on an
    /// older schema revision or the payload was truncated.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(FixedMalformedNames))]
    public void Fixed_WrongLengthRaisesTypedErrorNamingBothLengths(string name)
    {
        var v = FixedMalformeds.First(x => x.Name == name);

        var e = Assert.Throws<IonFixedArrayLengthException>(
            () => ReencodeFixed(v.ElementType, v.Length, v.Hex),
            $"fixed-array malformed vector '{name}': {v.Notes}")!;

        Assert.Multiple(() =>
        {
            Assert.That(e, Is.InstanceOf<IonDecodeException>());
            Assert.That(e.ExpectedLength, Is.EqualTo(v.Length), "the error names the declared N");
            Assert.That(e.ActualLength, Is.EqualTo(v.ActualLength), "the error names the length received");
            Assert.That(e.Message, Does.Contain(v.Length.ToString()).And.Contain(v.ActualLength.ToString()));
        });
    }

    /// <summary>Writers are exact too: a mismatched array is rejected before it reaches the wire.</summary>
    [Test]
    public void Fixed_WriteRejectsAMismatchedLength()
    {
        var w = new CborWriter();
        var e = Assert.Throws<IonFixedArrayLengthException>(
            () => IonFixedArrayFormatter<int>.Write(w, new IonArray<int>(new[] { 1, 2 }), 3))!;

        Assert.Multiple(() =>
        {
            Assert.That(e.ExpectedLength, Is.EqualTo(3));
            Assert.That(e.ActualLength, Is.EqualTo(2));
        });
    }

    /// <summary><c>N</c> is a parameter: one formatter type serves every declared length.</summary>
    [Test]
    public void Fixed_LengthIsAParameterNotPartOfTheType()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EncodeFixedArray(new[] { 1 }, 1), Is.EqualTo("8101"));
            Assert.That(EncodeFixedArray(new[] { 1, 2 }, 2), Is.EqualTo("820102"));
            Assert.That(EncodeFixedArray(new[] { 1, 2, 3 }, 3), Is.EqualTo("83010203"));
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  cross-cutting
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Length-first ordering is NOT plain bytewise ordering, and this is the case that proves it:
    /// <c>-1</c> encodes as <c>20</c> (1 byte) and <c>1000</c> as <c>1903e8</c> (3 bytes), so
    /// length-first puts <c>-1</c> first while a bytewise-only sort would put <c>1000</c> first
    /// (<c>0x19 &lt; 0x20</c>).
    /// </summary>
    [Test]
    public void CanonicalOrderIsLengthFirstNotBytewise()
    {
        var minusOne = new byte[] { 0x20 };
        var thousand = new byte[] { 0x19, 0x03, 0xe8 };

        Assert.Multiple(() =>
        {
            Assert.That(IonCanonicalCborComparer.Instance.Compare(minusOne, thousand), Is.LessThan(0),
                "length-first: the 1-byte key comes first");
            Assert.That(minusOne.AsSpan().SequenceCompareTo(thousand.AsSpan()), Is.GreaterThan(0),
                "…while a plain bytewise comparison would say the opposite, which is why this matters");

            var v = Maps.First(x => x.Name == "i4-keys-length-beats-lexicographic");
            Assert.That(EncodeMap(v, false), Is.EqualTo("a4000420021818031903e801"));
        });
    }

    /// <summary>Containers nest: a Set inside a Map value still counts as exactly one item.</summary>
    [Test]
    public void ContainersNestThroughTheFormatterStorage()
    {
        var map = new Dictionary<string, HashSet<int>> { ["a"] = [2, 1] };

        var w = new CborWriter();
        IonMapFormatter<string, HashSet<int>>.Write(w, map);

        Assert.That(GoldenFile.Hex(w), Is.EqualTo("a16161" + "d9010282" + "0102"));
    }

    /// <summary>
    /// A <see cref="Dictionary{TKey,TValue}"/> and a <see cref="HashSet{T}"/> resolve through
    /// <see cref="IonFormatterStorage"/>, so the generator can nest them anywhere a scalar goes.
    /// </summary>
    [Test]
    public void ContainersResolveThroughFormatterStorage()
    {
        var w = new CborWriter();
        IonFormatterStorage<Dictionary<string, int>>.Write(w, new Dictionary<string, int> { ["b"] = 2, ["a"] = 1 });
        Assert.That(GoldenFile.Hex(w), Is.EqualTo("a2616101616202"));

        var w2 = new CborWriter();
        IonFormatterStorage<HashSet<int>>.Write(w2, [3, 1, 2]);
        Assert.That(GoldenFile.Hex(w2), Is.EqualTo("d9010283010203"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  helpers — scalar conversion and per-type dispatch
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads a JSON scalar as the CLR type the Ion type name maps to. Large integers appear as
    /// JSON strings in the golden file so no precision is lost in any consumer's JSON parser.
    /// </summary>
    private static T Scalar<T>(JsonElement e)
    {
        var str = e.ValueKind == JsonValueKind.String ? e.GetString()! : null;
        object value = typeof(T) switch
        {
            var t when t == typeof(string) => str!,
            var t when t == typeof(Guid) => Guid.Parse(str!),
            var t when t == typeof(bool) => e.GetBoolean(),
            var t when t == typeof(byte) => str is null ? e.GetByte() : byte.Parse(str),
            var t when t == typeof(int) => str is null ? e.GetInt32() : int.Parse(str),
            var t when t == typeof(uint) => str is null ? e.GetUInt32() : uint.Parse(str),
            var t when t == typeof(long) => str is null ? e.GetInt64() : long.Parse(str),
            _ => throw new NotSupportedException(typeof(T).FullName)
        };
        return (T)value;
    }

    private static List<JsonElement> Items(JsonElement array, bool reverse)
    {
        var list = array.EnumerateArray().ToList();
        if (reverse) list.Reverse();
        return list;
    }

    private static string EncodeMap(MapVector v, bool reverse)
    {
        var entries = Items(v.Entries, reverse);
        return (v.KeyType, v.ValueType) switch
        {
            ("string", "i4") => MapHex<string, int>(entries),
            ("string", "string") => MapHex<string, string>(entries),
            ("i4", "i4") => MapHex<int, int>(entries),
            ("u4", "i4") => MapHex<uint, int>(entries),
            ("i8", "i4") => MapHex<long, int>(entries),
            ("guid", "i4") => MapHex<Guid, int>(entries),
            ("bool", "i4") => MapHex<bool, int>(entries),
            _ => throw new NotSupportedException($"map<{v.KeyType},{v.ValueType}>")
        };
    }

    private static string MapHex<TK, TV>(List<JsonElement> entries) where TK : notnull
    {
        var dict = new Dictionary<TK, TV>();
        foreach (var entry in entries)
            dict.Add(Scalar<TK>(entry.GetProperty("key")), Scalar<TV>(entry.GetProperty("value")));

        var w = new CborWriter();
        IonMapFormatter<TK, TV>.Write(w, dict);
        return GoldenFile.Hex(w);
    }

    private static string ReencodeMap(string keyType, string valueType, string hex) => (keyType, valueType) switch
    {
        ("string", "i4") => MapReencode<string, int>(hex),
        ("string", "string") => MapReencode<string, string>(hex),
        ("i4", "i4") => MapReencode<int, int>(hex),
        ("u4", "i4") => MapReencode<uint, int>(hex),
        ("i8", "i4") => MapReencode<long, int>(hex),
        ("guid", "i4") => MapReencode<Guid, int>(hex),
        ("bool", "i4") => MapReencode<bool, int>(hex),
        _ => throw new NotSupportedException($"map<{keyType},{valueType}>")
    };

    private static string MapReencode<TK, TV>(string hex) where TK : notnull
    {
        var map = IonMapFormatter<TK, TV>.Read(new CborReader(GoldenFile.Bytes(hex)));
        var w = new CborWriter();
        IonMapFormatter<TK, TV>.Write(w, map);
        return GoldenFile.Hex(w);
    }

    private static string EncodeSet(SetVector v, bool reverse)
    {
        var elements = Items(v.Elements, reverse);
        return v.ElementType switch
        {
            "i4" => SetHex<int>(elements),
            "string" => SetHex<string>(elements),
            "guid" => SetHex<Guid>(elements),
            _ => throw new NotSupportedException($"set<{v.ElementType}>")
        };
    }

    private static string SetHex<T>(List<JsonElement> elements)
    {
        // A LinkedHashSet-alike is not needed: the formatter sorts, and that is the claim.
        var set = new List<T>();
        foreach (var e in elements) set.Add(Scalar<T>(e));

        var w = new CborWriter();
        IonSetFormatter<T>.Write(w, set);
        return GoldenFile.Hex(w);
    }

    private static string ReencodeSet(string elementType, string hex) => elementType switch
    {
        "i4" => SetReencode<int>(hex),
        "string" => SetReencode<string>(hex),
        "guid" => SetReencode<Guid>(hex),
        _ => throw new NotSupportedException($"set<{elementType}>")
    };

    private static string SetReencode<T>(string hex)
    {
        var set = IonSetFormatter<T>.Read(new CborReader(GoldenFile.Bytes(hex)));
        var w = new CborWriter();
        IonSetFormatter<T>.Write(w, set);
        return GoldenFile.Hex(w);
    }

    private static string EncodeFixed(FixedVector v)
    {
        var elements = Items(v.Elements, false);
        return v.ElementType switch
        {
            "i4" => FixedHex<int>(elements, v.Length),
            "u1" => FixedHex<byte>(elements, v.Length),
            "string" => FixedHex<string>(elements, v.Length),
            "guid" => FixedHex<Guid>(elements, v.Length),
            _ => throw new NotSupportedException($"{v.ElementType}[{v.Length}]")
        };
    }

    private static string FixedHex<T>(List<JsonElement> elements, int length)
        => EncodeFixedArray(elements.Select(Scalar<T>).ToArray(), length);

    private static string EncodeFixedArray<T>(T[] values, int length)
    {
        var w = new CborWriter();
        IonFixedArrayFormatter<T>.Write(w, new IonArray<T>(values), length);
        return GoldenFile.Hex(w);
    }

    private static string ReencodeFixed(string elementType, int length, string hex) => elementType switch
    {
        "i4" => FixedReencode<int>(length, hex),
        "u1" => FixedReencode<byte>(length, hex),
        "string" => FixedReencode<string>(length, hex),
        "guid" => FixedReencode<Guid>(length, hex),
        _ => throw new NotSupportedException($"{elementType}[{length}]")
    };

    private static string FixedReencode<T>(int length, string hex)
    {
        var array = IonFixedArrayFormatter<T>.Read(new CborReader(GoldenFile.Bytes(hex)), length);
        var w = new CborWriter();
        IonFixedArrayFormatter<T>.Write(w, array, length);
        return GoldenFile.Hex(w);
    }

    /// <summary>Length in hex characters of the single CBOR item at the head of <paramref name="hex"/>.</summary>
    private static int NextItemHexLength(string hex)
    {
        var bytes = GoldenFile.Bytes(hex);
        var reader = new CborReader(bytes);
        reader.SkipValue();
        return (bytes.Length - reader.BytesRemaining) * 2;
    }

    // ── loading ──

    private static readonly Lazy<Loaded> loaded = new(Load);

    private sealed record Loaded(
        MapVector[] Maps, MapDecode[] MapDecodes, MapDecode[] MapMalformed,
        SetVector[] Sets, SetDecode[] SetDecodes, SetDecode[] SetMalformed,
        FixedVector[] Fixeds, FixedDecode[] FixedDecodes, FixedMalformed[] FixedMalformeds);

    internal static MapVector[] Maps => loaded.Value.Maps;
    private static MapDecode[] MapDecodes => loaded.Value.MapDecodes;
    private static MapDecode[] MapMalformed => loaded.Value.MapMalformed;
    internal static SetVector[] Sets => loaded.Value.Sets;
    private static SetDecode[] SetDecodes => loaded.Value.SetDecodes;
    private static SetDecode[] SetMalformed => loaded.Value.SetMalformed;
    internal static FixedVector[] Fixeds => loaded.Value.Fixeds;
    private static FixedDecode[] FixedDecodes => loaded.Value.FixedDecodes;
    private static FixedMalformed[] FixedMalformeds => loaded.Value.FixedMalformeds;

    private static Loaded Load()
    {
        var root = GoldenFile.Load("collections.golden.json");
        var map = root.GetProperty("map");
        var set = root.GetProperty("set");
        var fixedArray = root.GetProperty("fixedArray");

        MapVector Map(JsonElement v) => new(
            v.GetProperty("name").GetString()!,
            v.GetProperty("keyType").GetString()!,
            v.GetProperty("valueType").GetString()!,
            v.GetProperty("entries"),
            v.GetProperty("canonicalKeyOrder").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            v.GetProperty("hex").GetString()!,
            v.Str("notes"));

        MapDecode MapDec(JsonElement v) => new(
            v.GetProperty("name").GetString()!,
            v.GetProperty("keyType").GetString()!,
            v.GetProperty("valueType").GetString()!,
            v.GetProperty("hex").GetString()!,
            v.Str("reencodedHex"),
            v.Str("notes"));

        SetVector Set(JsonElement v) => new(
            v.GetProperty("name").GetString()!,
            v.GetProperty("elementType").GetString()!,
            v.GetProperty("elements"),
            v.GetProperty("hex").GetString()!,
            v.Str("notes"));

        SetDecode SetDec(JsonElement v) => new(
            v.GetProperty("name").GetString()!,
            v.GetProperty("elementType").GetString()!,
            v.GetProperty("hex").GetString()!,
            v.Str("reencodedHex"),
            v.Str("notes"));

        FixedVector Fixed(JsonElement v) => new(
            v.GetProperty("name").GetString()!,
            v.GetProperty("elementType").GetString()!,
            v.GetProperty("length").GetInt32(),
            v.GetProperty("elements"),
            v.GetProperty("hex").GetString()!,
            v.Str("notes"));

        FixedDecode FixedDec(JsonElement v) => new(
            v.GetProperty("name").GetString()!,
            v.GetProperty("elementType").GetString()!,
            v.GetProperty("length").GetInt32(),
            v.GetProperty("hex").GetString()!,
            v.Str("reencodedHex"),
            v.Str("notes"));

        FixedMalformed FixedMal(JsonElement v) => new(
            v.GetProperty("name").GetString()!,
            v.GetProperty("elementType").GetString()!,
            v.GetProperty("length").GetInt32(),
            v.GetProperty("actualLength").GetInt32(),
            v.GetProperty("hex").GetString()!,
            v.Str("notes"));

        return new Loaded(
            map.GetProperty("vectors").EnumerateArray().Select(Map).ToArray(),
            map.GetProperty("decodeOnly").EnumerateArray().Select(MapDec).ToArray(),
            map.GetProperty("malformed").EnumerateArray().Select(MapDec).ToArray(),
            set.GetProperty("vectors").EnumerateArray().Select(Set).ToArray(),
            set.GetProperty("decodeOnly").EnumerateArray().Select(SetDec).ToArray(),
            set.GetProperty("malformed").EnumerateArray().Select(SetDec).ToArray(),
            fixedArray.GetProperty("vectors").EnumerateArray().Select(Fixed).ToArray(),
            fixedArray.GetProperty("decodeOnly").EnumerateArray().Select(FixedDec).ToArray(),
            fixedArray.GetProperty("malformed").EnumerateArray().Select(FixedMal).ToArray());
    }

    private static IEnumerable<string> MapNames() => Maps.Select(v => v.Name);
    private static IEnumerable<string> MapDecodeNames() => MapDecodes.Select(v => v.Name);
    private static IEnumerable<string> MapMalformedNames() => MapMalformed.Select(v => v.Name);
    private static IEnumerable<string> SetNames() => Sets.Select(v => v.Name);
    private static IEnumerable<string> SetDecodeNames() => SetDecodes.Select(v => v.Name);
    private static IEnumerable<string> SetMalformedNames() => SetMalformed.Select(v => v.Name);
    private static IEnumerable<string> FixedNames() => Fixeds.Select(v => v.Name);
    private static IEnumerable<string> FixedDecodeNames() => FixedDecodes.Select(v => v.Name);
    private static IEnumerable<string> FixedMalformedNames() => FixedMalformeds.Select(v => v.Name);
}
