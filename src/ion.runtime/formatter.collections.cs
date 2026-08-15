namespace ion.runtime;

using System.Formats.Cbor;

/// <summary>
/// Canonical CBOR ordering, RFC 8949 §4.2.1: compare two encoded data items by their
/// <b>byte length first</b>, and only then lexicographically by their bytes.
/// <para>
/// This is the total order that makes <c>Map&lt;K,V&gt;</c> and <c>Set&lt;T&gt;</c> byte-identical
/// across runtimes. A <see cref="Dictionary{TKey,TValue}"/>, a JavaScript <c>Map</c> and a Rust
/// <c>HashMap</c> have three different iteration orders; without a total order on the wire the
/// same logical map produces three different byte strings.
/// </para>
/// <para>
/// <b>Length-first is not the same as plain bytewise comparison</b>, and integer keys make the
/// difference visible: <c>-1</c> encodes as <c>20</c> (1 byte) and <c>1000</c> as <c>1903e8</c>
/// (3 bytes). Length-first puts <c>-1</c> first; a bytewise-only sort puts <c>1000</c> first,
/// because <c>0x19 &lt; 0x20</c>. See the <c>i4-keys-length-beats-lexicographic</c> vector in
/// <c>/tests/golden/collections.golden.json</c>.
/// </para>
/// </summary>
public sealed class IonCanonicalCborComparer : IComparer<byte[]>
{
    public static readonly IonCanonicalCborComparer Instance = new();

    public int Compare(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        if (x.Length != y.Length)
            return x.Length.CompareTo(y.Length);

        return x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}

/// <summary>
/// Shared plumbing for the container formatters: encoding one item into its own buffer so it can
/// be sorted before it is written, and walking a container that may carry either a definite or an
/// indefinite length.
/// </summary>
public static class IonContainerWire
{
    /// <summary>CBOR tag 258 — the IANA-registered "set" tag.</summary>
    public const ulong SetTag = 258;

    /// <summary>Encodes a single value with its registered formatter into a standalone buffer.</summary>
    /// <remarks>
    /// Sorting has to happen over encoded bytes, and the bytes only exist once the value has been
    /// written, so each key/element is written into a scratch <see cref="CborWriter"/> first and
    /// spliced in afterwards with <see cref="CborWriter.WriteEncodedValue"/>.
    /// </remarks>
    public static byte[] EncodeItem<T>(T value)
    {
        var scratch = new CborWriter();
        IonFormatterStorage<T>.Write(scratch, value);
        return scratch.Encode();
    }
}

/// <summary>
/// Wire encoding for Ion's <c>Map&lt;K,V&gt;</c>.
/// <para>
/// <b>Rule: a definite-length CBOR map whose keys are sorted in canonical CBOR order</b>
/// (<see cref="IonCanonicalCborComparer"/>). Values use their own type's ordinary Ion encoding and
/// take no part in the ordering.
/// </para>
/// <para>
/// <b>Readers accept an indefinite-length map</b> (<c>0xBF … 0xFF</c>) and an unsorted wire order;
/// re-encoding canonicalises both. The indefinite case is called out explicitly because the
/// <c>Partial&lt;T&gt;</c> formatter had exactly that bug — <c>length ?? 0</c> read zero entries
/// and then desynchronised the reader on the closing break — and it must not be repeated here.
/// </para>
/// <para>
/// <b>Duplicate keys are rejected</b> with <see cref="IonDuplicateMapKeyException"/>. Last-wins
/// and first-wins both make the decoded value depend on the order entries happen to appear in,
/// which is the very non-determinism the canonical ordering exists to remove.
/// </para>
/// <para>
/// <b>Key types.</b> The compiler restricts keys to scalar / <c>string</c> / <c>guid</c> / enum
/// types. This formatter does not re-validate that: it encodes whatever the registered
/// <see cref="IonFormatter{T}"/> for <typeparamref name="TKey"/> produces and sorts by the
/// resulting bytes, so a composite key would still yield a deterministic map — but duplicate
/// detection would then follow <typeparamref name="TKey"/>'s own equality rather than the encoded
/// bytes, and for a reference type without value equality that means duplicates would slip
/// through. The compiler restriction is the real guarantee.
/// </para>
/// <para>Golden vectors: <c>/tests/golden/collections.golden.json</c>, section <c>map</c>.</para>
/// </summary>
public static class IonMapFormatter<TKey, TValue> where TKey : notnull
{
    /// <summary>Writes a map as a definite-length CBOR map with canonically ordered keys.</summary>
    public static void Write(CborWriter writer, IReadOnlyDictionary<TKey, TValue> map)
    {
        // Only the KEYS need pre-encoding: once the order is decided the values can be written
        // live, in place, by their own formatter.
        var ordered = new List<(byte[] Key, TValue Value)>(map.Count);
        foreach (var (key, value) in map)
            ordered.Add((IonContainerWire.EncodeItem(key), value));

        ordered.Sort(static (a, b) => IonCanonicalCborComparer.Instance.Compare(a.Key, b.Key));

        writer.WriteStartMap(ordered.Count);
        foreach (var (key, value) in ordered)
        {
            writer.WriteEncodedValue(key);
            IonFormatterStorage<TValue>.Write(writer, value);
        }
        writer.WriteEndMap();
    }

    /// <summary>Reads a CBOR map, definite or indefinite length.</summary>
    /// <exception cref="IonDuplicateMapKeyException">The payload contained the same key twice.</exception>
    public static Dictionary<TKey, TValue> Read(CborReader reader)
    {
        if (reader.PeekState() != CborReaderState.StartMap)
            throw new IonMalformedValueException("Map", $"expected a CBOR map, got {reader.PeekState()}");

        var length = reader.ReadStartMap();
        var result = new Dictionary<TKey, TValue>(length ?? 4);

        // PeekState() reports EndMap for a definite-length map once its declared entry count is
        // exhausted, so this single loop covers both length forms.
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            if (reader.PeekState() == CborReaderState.Finished)
                throw new IonMalformedValueException("Map", "unexpected end of CBOR data inside a map");

            // The key is lifted out as raw bytes and decoded on its own reader, rather than read
            // in place. CborReader enforces map-key uniqueness itself in every conformance mode
            // except Lax, and does so by throwing CborContentException — an opaque error, and one
            // that fires before this formatter can report the duplicate as a typed failure with
            // the offending key attached. `disableConformanceModeChecks` suppresses only that
            // pre-emption: the fresh CborReader below is Strict, so non-minimal encodings inside
            // the key are still rejected, and the uniqueness rule is still enforced — here, by us.
            var keyBytes = reader.ReadEncodedValue(disableConformanceModeChecks: true);
            var key = IonFormatterStorage<TKey>.Read(new CborReader(keyBytes));
            var value = IonFormatterStorage<TValue>.Read(reader);

            if (!result.TryAdd(key, value))
                throw new IonDuplicateMapKeyException(key);
        }

        reader.ReadEndMap();
        return result;
    }

    /// <summary>Reads a nullable map: CBOR null becomes <see langword="null"/>.</summary>
    public static Dictionary<TKey, TValue>? ReadNullable(CborReader reader)
    {
        if (reader.PeekState() != CborReaderState.Null)
            return Read(reader);
        reader.ReadNull();
        return null;
    }

    /// <summary>Writes a nullable map: <see langword="null"/> becomes CBOR null.</summary>
    public static void WriteNullable(CborWriter writer, IReadOnlyDictionary<TKey, TValue>? map)
    {
        if (map is null)
        {
            writer.WriteNull();
            return;
        }
        Write(writer, map);
    }
}

/// <summary>
/// Wire encoding for Ion's <c>Set&lt;T&gt;</c>.
/// <para>
/// <b>Rule: CBOR tag 258 — the IANA-registered set tag — wrapping a definite-length array whose
/// elements are sorted in canonical CBOR order</b> (<see cref="IonCanonicalCborComparer"/>, the
/// same rule as map keys).
/// </para>
/// <para>
/// <b>The tag is mandatory in both directions.</b> Writers always emit it and readers always
/// require it. Accepting an untagged array would not be a free leniency: a bare array is exactly
/// the encoding of <c>Array&lt;T&gt;</c>, and <c>Set&lt;T&gt;</c> and <c>Array&lt;T&gt;</c> are
/// distinct Ion types with distinct schema-lock entries. Erasing that on read would make a
/// captured payload ambiguous to anything that does not already hold the schema, which is exactly
/// what the tag exists to prevent.
/// </para>
/// <para>
/// <b>Element order is canonical, not insertion order</b>, so two sets built by inserting the same
/// elements in different orders — or a <see cref="HashSet{T}"/> and a JavaScript <c>Set</c> and a
/// Rust <c>HashSet</c>, which iterate in three different orders — produce identical bytes.
/// </para>
/// <para>
/// <b>Duplicate elements are rejected</b> with <see cref="IonDuplicateSetElementException"/>.
/// Silently collapsing them would let a three-element wire array decode as a two-element set: a
/// size change the caller can neither observe nor guard against.
/// </para>
/// <para>
/// Readers accept an indefinite-length inner array and an unsorted wire order.
/// Golden vectors: <c>/tests/golden/collections.golden.json</c>, section <c>set</c>.
/// </para>
/// </summary>
public static class IonSetFormatter<T>
{
    /// <summary>Writes a set as tag 258 + a definite-length array with canonically ordered elements.</summary>
    public static void Write(CborWriter writer, IReadOnlyCollection<T> set)
    {
        var ordered = new List<byte[]>(set.Count);
        foreach (var element in set)
            ordered.Add(IonContainerWire.EncodeItem(element));

        ordered.Sort(IonCanonicalCborComparer.Instance);

        writer.WriteTag((CborTag)IonContainerWire.SetTag);
        writer.WriteStartArray(ordered.Count);
        foreach (var element in ordered)
            writer.WriteEncodedValue(element);
        writer.WriteEndArray();
    }

    /// <summary>Reads a tag 258 set.</summary>
    /// <exception cref="IonUnexpectedTagException">The tag was missing or was not 258.</exception>
    /// <exception cref="IonDuplicateSetElementException">The payload contained the same element twice.</exception>
    public static HashSet<T> Read(CborReader reader)
    {
        if (reader.PeekState() != CborReaderState.Tag)
            throw new IonMalformedValueException("Set",
                $"expected CBOR tag {IonContainerWire.SetTag}, got {reader.PeekState()}; " +
                "an untagged array is Array<T>, not Set<T>");

        var tag = (ulong)reader.ReadTag();
        if (tag != IonContainerWire.SetTag)
            throw new IonUnexpectedTagException(IonContainerWire.SetTag, tag, "Set");

        if (reader.PeekState() != CborReaderState.StartArray)
            throw new IonMalformedValueException("Set",
                $"tag {IonContainerWire.SetTag} must wrap an array, got {reader.PeekState()}");

        var length = reader.ReadStartArray();
        var result = new HashSet<T>(length ?? 4);

        while (reader.PeekState() != CborReaderState.EndArray)
        {
            if (reader.PeekState() == CborReaderState.Finished)
                throw new IonMalformedValueException("Set", "unexpected end of CBOR data inside a set");

            var element = IonFormatterStorage<T>.Read(reader);
            if (!result.Add(element))
                throw new IonDuplicateSetElementException(element);
        }

        reader.ReadEndArray();
        return result;
    }

    /// <summary>Reads a nullable set: CBOR null becomes <see langword="null"/>.</summary>
    public static HashSet<T>? ReadNullable(CborReader reader)
    {
        if (reader.PeekState() != CborReaderState.Null)
            return Read(reader);
        reader.ReadNull();
        return null;
    }

    /// <summary>Writes a nullable set: <see langword="null"/> becomes CBOR null.</summary>
    public static void WriteNullable(CborWriter writer, IReadOnlyCollection<T>? set)
    {
        if (set is null)
        {
            writer.WriteNull();
            return;
        }
        Write(writer, set);
    }
}

/// <summary>
/// Wire encoding for Ion's fixed-size array <c>T[N]</c>.
/// <para>
/// <b>Rule: a definite-length CBOR array of exactly <c>N</c> items.</b> A reader handed any other
/// length fails with <see cref="IonFixedArrayLengthException"/>, which names both the declared
/// <c>N</c> and the length received — that check is the entire point of the feature, and knowing
/// only that the length was wrong does not tell a caller whether the peer is on an older schema
/// revision or the payload was truncated. A writer handed a mismatched array fails the same way,
/// because writers are exact.
/// </para>
/// <para>
/// <b><c>N</c> is a parameter, never baked into a per-length type</b>, so the generator can emit
/// <c>IonFixedArrayFormatter&lt;int&gt;.Read(reader, 3)</c> without a distinct formatter class per
/// declared length.
/// </para>
/// <para>
/// <b>No <c>u1[N]</c> special case.</b> A fixed array of <c>u1</c> is an array of <c>N</c> CBOR
/// integers, <i>not</i> a CBOR byte string. Collapsing it would save a few bytes and make the wire
/// type of a fixed array depend on its element type, which no reader could predict from the array
/// shape alone. Honest and predictable beats clever.
/// </para>
/// <para>
/// Extra items are <b>not</b> skipped for forward compatibility, unlike a message's trailing
/// fields: the declared length is the contract. An indefinite-length array is accepted on read as
/// long as it turns out to hold exactly <c>N</c> items.
/// </para>
/// <para>Golden vectors: <c>/tests/golden/collections.golden.json</c>, section <c>fixedArray</c>.</para>
/// </summary>
public static class IonFixedArrayFormatter<T>
{
    /// <summary>Writes exactly <paramref name="length"/> items as a definite-length CBOR array.</summary>
    /// <exception cref="IonFixedArrayLengthException">
    /// <paramref name="array"/> does not hold exactly <paramref name="length"/> items.
    /// </exception>
    public static void Write(CborWriter writer, IonArray<T> array, int length)
    {
        if (array.Size != length)
            throw new IonFixedArrayLengthException(length, array.Size);

        writer.WriteStartArray(length);
        for (var i = 0; i < length; i++)
            IonFormatterStorage<T>.Write(writer, array[i]);
        writer.WriteEndArray();
    }

    /// <summary>Reads a CBOR array that must hold exactly <paramref name="length"/> items.</summary>
    /// <exception cref="IonFixedArrayLengthException">The payload held a different number of items.</exception>
    public static IonArray<T> Read(CborReader reader, int length)
    {
        if (reader.PeekState() != CborReaderState.StartArray)
            throw new IonMalformedValueException($"{typeof(T).Name}[{length}]",
                $"expected a CBOR array, got {reader.PeekState()}");

        var declared = reader.ReadStartArray();
        if (declared is not null && declared != length)
            throw new IonFixedArrayLengthException(length, declared.Value);

        var values = new List<T>(length);
        while (reader.PeekState() != CborReaderState.EndArray)
        {
            if (reader.PeekState() == CborReaderState.Finished)
                throw new IonMalformedValueException($"{typeof(T).Name}[{length}]",
                    "unexpected end of CBOR data inside a fixed-size array");

            // Guard the indefinite-length case: stop before running past N so a hostile payload
            // cannot make the reader allocate without bound.
            if (values.Count == length)
                throw new IonFixedArrayLengthException(length, length + 1);

            values.Add(IonFormatterStorage<T>.Read(reader));
        }

        reader.ReadEndArray();

        if (values.Count != length)
            throw new IonFixedArrayLengthException(length, values.Count);

        return new IonArray<T>(values);
    }

    /// <summary>Reads a nullable fixed-size array: CBOR null becomes <see langword="null"/>.</summary>
    public static IonArray<T>? ReadNullable(CborReader reader, int length)
    {
        if (reader.PeekState() != CborReaderState.Null)
            return Read(reader, length);
        reader.ReadNull();
        return null;
    }

    /// <summary>Writes a nullable fixed-size array: <see langword="null"/> becomes CBOR null.</summary>
    public static void WriteNullable(CborWriter writer, IonArray<T>? array, int length)
    {
        if (array is null)
        {
            writer.WriteNull();
            return;
        }
        Write(writer, array.Value, length);
    }
}

/// <summary>
/// <see cref="IonFormatter{T}"/> adapter so a <see cref="Dictionary{TKey,TValue}"/> resolves
/// through <see cref="IonFormatterStorage"/> and can therefore be nested inside a message, an
/// array, a <c>Maybe</c> or a <c>Partial</c> without the generator special-casing it.
/// </summary>
public sealed class Ion_map_Formatter<TKey, TValue> : IonFormatter<Dictionary<TKey, TValue>>
    where TKey : notnull
{
    public Dictionary<TKey, TValue> Read(CborReader reader)
        => IonMapFormatter<TKey, TValue>.Read(reader);

    public void Write(CborWriter writer, Dictionary<TKey, TValue> value)
        => IonMapFormatter<TKey, TValue>.Write(writer, value);
}

/// <summary>
/// <see cref="IonFormatter{T}"/> adapter so a <see cref="HashSet{T}"/> resolves through
/// <see cref="IonFormatterStorage"/> and can be nested like any other value.
/// </summary>
public sealed class Ion_set_Formatter<T> : IonFormatter<HashSet<T>>
{
    public HashSet<T> Read(CborReader reader)
        => IonSetFormatter<T>.Read(reader);

    public void Write(CborWriter writer, HashSet<T> value)
        => IonSetFormatter<T>.Write(writer, value);
}
