namespace ion.runtime;

using ion.runtime.network;
#pragma warning disable CA2255
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

public static class IonBinarySerializer
{
    public static void Serialize<T>(T value, Action<ReadOnlyMemory<byte>> onSerialized)
    {
        var writer = new CborWriter();

        IonFormatterStorage<T>.Value.Write(writer, value);

        using var mem = MemoryPool<byte>.Shared.Rent(writer.BytesWritten);

        writer.Encode(mem.Memory.Span);

        onSerialized(mem.Memory);
    }

    public static async Task SerializeAsync<T>(T value, Func<ReadOnlyMemory<byte>, Task> onSerialized)
    {
        var writer = new CborWriter();

        IonFormatterStorage<T>.Value.Write(writer, value);

        using var mem = MemoryPool<byte>.Shared.Rent(writer.BytesWritten);

        writer.Encode(mem.Memory.Span);

        await onSerialized(mem.Memory);
    }
}

public static class CborExtensions
{
    /// <summary>
    /// Opens a message's positional array and checks it against the declared field count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of <see cref="ReadEndArrayAndSkip"/>: this is the <i>front</i> of the
    /// trailing-skip mechanism, and the point at which a payload that is too short can still be
    /// reported honestly. A positional array with fewer items than the schema declares is a
    /// <see cref="IonFieldCountException"/> naming both counts — never a read that walks past the
    /// array into whatever follows it.
    /// </para>
    /// <para>
    /// Emitted by <c>ionc</c> in place of the older
    /// <c>reader.ReadStartArray() ?? throw new Exception("undefined len array not allowed")</c>,
    /// which produced an untyped failure and left the short case to be discovered — or not — by
    /// the field reads themselves.
    /// </para>
    /// </remarks>
    /// <param name="reader">The reader, positioned on the message array.</param>
    /// <param name="expectedFields">How many positional fields this schema revision reads.</param>
    /// <param name="context">The message name, for the failure message.</param>
    /// <returns>The declared item count, to be passed to <see cref="ReadEndArrayAndSkip"/>.</returns>
    public static int ReadStartMessage(this CborReader reader, int expectedFields, string context)
    {
        IonDecodeGuard.EnsureDepth(reader);

        // Through IonDecodeGuard, so that an empty or exhausted buffer is reported as the
        // truncation it is rather than as whatever PeekState throws. Same order as
        // `IonFormatterStorage.readStartMessage` in TypeScript.
        var state = IonDecodeGuard.PeekState(reader, context);
        if (state != CborReaderState.StartArray)
            throw new IonUnexpectedCborTypeException(context, "an array", state.ToString());

        var declared = reader.ReadStartArray() ?? throw new IonIndefiniteLengthException(context);

        if (declared < expectedFields)
            throw new IonFieldCountException(context, expectedFields, declared);

        return declared;
    }

    /// <summary>
    /// Opens a <c>union</c> envelope and reads its case index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A union envelope is exactly <c>[index, payload]</c> — two items, in every revision of
    /// every union.</b> Growth happens inside the case payload, which is a message and skips its
    /// own tail; the envelope itself never grows. A third item is therefore not a newer peer but a
    /// malformed frame, and the generated reader used to walk straight past it: it read the index
    /// and the payload and stopped, leaving the stray item in the stream for the <i>next field of
    /// the enclosing message</i> to read as its own value. In the compat suite that turned
    /// <c>n: 5</c> into <c>n: 9</c> with no error anywhere.
    /// </para>
    /// <para>
    /// Rejecting rather than skipping is deliberate: it is what
    /// <see cref="System.Formats.Cbor.CborReader"/> already enforced for this runtime, so it is the
    /// answer all three runtimes can converge on without changing what any of them accepts today.
    /// </para>
    /// </remarks>
    /// <param name="reader">The reader, positioned on the envelope array.</param>
    /// <param name="unionType">The union's name, for the failure message.</param>
    /// <param name="declaredCases">How many cases this schema revision declares.</param>
    /// <returns>The case index, already known to be one this revision declares.</returns>
    public static uint ReadStartUnion(this CborReader reader, string unionType, uint declaredCases)
    {
        IonDecodeGuard.EnsureDepth(reader);

        var state = IonDecodeGuard.PeekState(reader, unionType);
        if (state != CborReaderState.StartArray)
            throw new IonUnexpectedCborTypeException(unionType, "a [index, payload] array", state.ToString());

        var declared = reader.ReadStartArray() ?? throw new IonIndefiniteLengthException(unionType);
        if (declared != 2)
            throw new IonUnionEnvelopeException(unionType, declared);

        var index = IonInteger.ReadUnsigned(reader, uint.MaxValue, $"{unionType} case index");
        if (index >= declaredCases)
            throw new IonInvalidUnionIndexException(unionType, (uint)index, declaredCases);

        return (uint)index;
    }

    /// <summary>Closes a <c>union</c> envelope opened by <see cref="ReadStartUnion"/>.</summary>
    public static void ReadEndUnion(this CborReader reader) => reader.ReadEndArray();

    /// <summary>
    /// Skips a message's trailing items and closes its array.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="skipCount"/> is <c>declaredLength - fieldsRead</c>. A <b>negative</b> value
    /// means the payload was shorter than the schema — which is a decode failure, not a distance.
    /// It used to be run through <see cref="Math.Abs(int)"/>, which turned "one field missing" into
    /// "skip one more item", i.e. into a read past the end of the array and into the next frame.
    /// </para>
    /// <para>
    /// The skip itself is depth-bounded (<see cref="SkipValueBounded"/>): a field the reader never
    /// looks at is still attacker-controlled nesting.
    /// </para>
    /// </remarks>
    public static void ReadEndArrayAndSkip(this CborReader reader, int skipCount)
    {
        if (skipCount < 0)
            throw new IonFieldCountException("<message>", -skipCount, 0);

        for (var i = 0; i < skipCount; i++)
            reader.SkipValueBounded();
        reader.ReadEndArray();
    }

    /// <summary>
    /// Skips exactly one data item, refusing to descend past
    /// <see cref="IonDecodeLimits.MaxDepth"/> open containers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CborReader.SkipValue"/> on its own is iterative and therefore cheap at any depth
    /// — which is precisely the problem: 100 KB of <c>0x81</c> bytes is 100 000 levels of nesting
    /// that this runtime walks without complaint while the TypeScript one dies of a recursive
    /// <c>skipValue</c>. Skipping is the easiest place for an unauthenticated peer to reach deep
    /// nesting, because it needs no knowledge of the schema at all, so it gets the same limit as
    /// everything else.
    /// </para>
    /// </remarks>
    public static void SkipValueBounded(this CborReader reader)
    {
        var start = reader.CurrentDepth;

        while (true)
        {
            var state = reader.PeekState();

            switch (state)
            {
                case CborReaderState.StartArray:
                    IonDecodeGuard.EnsureDepth(reader);
                    reader.ReadStartArray();
                    continue;

                case CborReaderState.StartMap:
                    IonDecodeGuard.EnsureDepth(reader);
                    reader.ReadStartMap();
                    continue;

                case CborReaderState.EndArray:
                    if (reader.CurrentDepth <= start)
                        throw new IonContainerExhaustedException("a skipped value");
                    reader.ReadEndArray();
                    break;

                case CborReaderState.EndMap:
                    if (reader.CurrentDepth <= start)
                        throw new IonContainerExhaustedException("a skipped value");
                    reader.ReadEndMap();
                    break;

                case CborReaderState.Tag:
                    // A tag and the item it wraps are one data item; keep going.
                    reader.ReadTag();
                    continue;

                case CborReaderState.Finished:
                    throw new IonTruncatedPayloadException("a skipped value");

                default:
                    reader.SkipValue();
                    break;
            }

            if (reader.CurrentDepth <= start)
                return;
        }
    }

    public static void WriteUndefineds(this CborWriter writer, int count)
    {
        for (var i = 0; i < count; i++) writer.WriteSimpleValue(CborSimpleValue.Undefined);
    }
}

public interface IonFormatter<T>
{
    T Read(CborReader reader);

    void Write(CborWriter writer, T value);
}

public static class IonFormatterEx
{
    extension(CborReader reader)
    {
        public T? ReadNullable<T>(_StructTag<T> _ = default)
            where T : struct
        {
            var state = IonDecodeGuard.PeekState(reader, typeof(T).Name);
            if (state != CborReaderState.Null)
                return IonFormatterStorage<T>.Read(reader);

            reader.ReadNull();
            return null;
        }

        public T ReadNullable<T>(_ClassTag<T> _ = default)
            where T : class
        {
            var state = IonDecodeGuard.PeekState(reader, typeof(T).Name);
            if (state != CborReaderState.Null)
                return IonFormatterStorage<T>.Read(reader);

            reader.ReadNull();
            return null!;
        }
    }

    public readonly struct _StructTag<T> where T : struct
    {
    }

    public readonly struct _ClassTag<T> where T : class
    {
    }
}
public static class IonFormatterStorage
{
    internal static Dictionary<Type, Type> FormatterRelation { get; } = new();
    internal static Dictionary<Type, object> FormatterInstances { get; } = new();

    public static void SetFormatterTypeFor(Type type, Type fmtType)
        => FormatterRelation[type] = fmtType;

    public static Type GetFormatterTypeFor(Type type)
    {
        if (FormatterRelation.TryGetValue(type, out var fmtType))
        {
#if DEBUG
            if (!fmtType.GetInterfaces()
                    .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IonFormatter<>)))
                throw new InvalidOperationException($"Found {fmtType.FullName} for {type.FullName}, but {fmtType.FullName} is not IonFormatter");
#endif
            return fmtType;
        }

        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            if (FormatterRelation.TryGetValue(genericDef, out var openFmtType))
            {
                return openFmtType.MakeGenericType(type.GetGenericArguments());
            }
        }

        throw new InvalidOperationException($"Ion Formatter for type '{type.FullName}' is not registered");
    }

    public static IonFormatter<T> GetFormatter<T>()
    {
        var t = typeof(T);

        if (FormatterInstances.TryGetValue(t, out var cached))
            return (IonFormatter<T>)cached;

        var fmtType = GetFormatterTypeFor(t);
        var instance = (IonFormatter<T>)Activator.CreateInstance(fmtType)!;
        FormatterInstances[t] = instance;
        return instance;
    }

    public static void SetFormatter<T>(IonFormatter<T> fmt)
    {
        SetFormatterTypeFor(typeof(T), fmt.GetType());
        FormatterInstances[typeof(T)] = fmt;
    }
}

public static class IonFormatterStorage<T>
{
    public static IonFormatter<T> Value
    {
        get => IonFormatterStorage.GetFormatter<T>();
        set => IonFormatterStorage.SetFormatter(value);
    }

    /// <summary>
    /// Reads one value of <typeparamref name="T"/>, enforcing the nesting limit and translating
    /// anything the underlying <see cref="CborReader"/> throws into an
    /// <see cref="IonDecodeException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the choke point.</b> Every Ion read — a message field, an array element, a map
    /// value, a union payload, a method argument — goes through here, so this is the one place
    /// that can guarantee a caller never sees a decode failure spelled
    /// <see cref="InvalidOperationException"/>, <see cref="CborContentException"/> or
    /// <see cref="OverflowException"/>. Generated formatters and
    /// <see cref="System.Formats.Cbor"/> cannot be taught the Ion hierarchy; this can.
    /// </para>
    /// <para>
    /// The <c>try</c>/<c>catch</c> costs nothing on the success path. An
    /// <see cref="IonDecodeException"/> from further in is rethrown untouched, so the innermost —
    /// most specific — diagnosis is the one the caller gets.
    /// </para>
    /// </remarks>
    public static T Read(CborReader reader)
    {
        if (Value is null)
            throw new InvalidOperationException($"Ion Formatter for type '{typeof(T).FullName}' is not registered");

        IonDecodeGuard.EnsureDepth(reader);

        try
        {
            return Value.Read(reader);
        }
        catch (IonDecodeException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw IonDecodeGuard.Translate(reader, e, typeof(T).Name);
        }
    }

    public static void Write(CborWriter writer, T value)
    {
        if (Value is null)
            throw new InvalidOperationException($"Ion Formatter for type '{typeof(T).FullName}' is not registered");
        Value.Write(writer, value);
    }

    public static T? ReadNullable(CborReader reader)
    {
        var state = IonDecodeGuard.PeekState(reader, typeof(T).Name);
        if (state != CborReaderState.Null)
            return Read(reader);
        reader.ReadNull();
        return default;
    }

    public static IonMaybe<T> ReadMaybe(CborReader reader)
    {
        var state = IonDecodeGuard.PeekState(reader, typeof(T).Name);
        if (state != CborReaderState.Null)
            return Read(reader);
        reader.ReadNull();
        return IonMaybe<T>.None;
    }

    public static void WriteMaybe(CborWriter writer, IonMaybe<T> ionMaybe)
    {
        if (!ionMaybe.HasValue)
        {
            writer.WriteNull();
            return;
        }

        var value = ionMaybe.Value!;
        Write(writer, value);
    }

    public static void WriteNullable<TNullable>(CborWriter writer, TNullable? ionMaybe) where TNullable : class
    {
        if (ionMaybe is null)
        {
            writer.WriteNull();
            return;
        }

        Write(writer, (T)(object)ionMaybe);
    }

    public static void WriteNullable<TNullable>(CborWriter writer, TNullable? ionMaybe) where TNullable : struct
    {
        if (!ionMaybe.HasValue)
        {
            writer.WriteNull();
            return;
        }

        if (ionMaybe.Value is T unwrapped)
            Write(writer, unwrapped);
        else
            throw new InvalidOperationException($"T({typeof(T).Name}) != TNullable({typeof(TNullable).Name})");
    }


    /// <summary>
    /// Reads a <c>T[]</c> field: a definite-length CBOR array of <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The declared length is never trusted for allocation.</b> Every CBOR data item occupies
    /// at least one byte, so an element count above the number of bytes left in the buffer is
    /// provably a lie and is rejected — with <see cref="IonLengthOverclaimException"/> — before
    /// anything is rented. Without that check a nine-byte payload declaring 2^32 elements asks the
    /// pool for a 2^32-element buffer.
    /// </para>
    /// <para>
    /// An indefinite-length array is refused: <c>T[]</c> is length-prefixed on the wire, and the
    /// trailing-skip arithmetic of the enclosing message is computed from declared lengths.
    /// </para>
    /// </remarks>
    public static IonArray<T> ReadArray(CborReader reader)
    {
        var context = $"{typeof(T).Name}[]";

        IonDecodeGuard.EnsureDepth(reader);

        if (IonDecodeGuard.PeekState(reader, context) != CborReaderState.StartArray)
            throw new IonUnexpectedCborTypeException(context, "an array",
                IonDecodeGuard.PeekState(reader, context).ToString());

        int size;
        try
        {
            size = reader.ReadStartArray() ?? throw new IonIndefiniteLengthException(context);
        }
        catch (IonDecodeException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw IonDecodeGuard.Translate(reader, e, context);
        }

        // A definite length is a claim about the input, not a licence to allocate from it.
        if (size > reader.BytesRemaining)
            throw new IonLengthOverclaimException(context, size, reader.BytesRemaining);

        using var span = MemoryPool<T>.Shared.Rent(size);

        for (var i = 0; i < size; i++)
            span.Memory.Span[i] = Read(reader);

        try
        {
            reader.ReadEndArray();
        }
        catch (IonDecodeException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw IonDecodeGuard.Translate(reader, e, context);
        }

        return new IonArray<T>(span.Memory.Span[..size]);
    }

    public static IonArray<T>? ReadNullableArray(CborReader reader)
    {
        var state = IonDecodeGuard.PeekState(reader, $"{typeof(T).Name}[]");
        if (state != CborReaderState.Null)
            return ReadArray(reader);
        reader.ReadNull();
        return null;
    }

    public static void WriteNullableArray(CborWriter writer, IonArray<T>? array)
    {
        if (array is null)
        {
            writer.WriteNull();
            return;
        }
        WriteArray(writer, array.Value);
    }

    public static void WriteArray(CborWriter writer, IonArray<T> array)
    {
        writer.WriteStartArray(array.Size);
        if (array.Size == 0)
        {
            writer.WriteEndArray();
            return;
        }

        for (var i = 0; i < array.Size; i++)
            Write(writer, array[i]);
        writer.WriteEndArray();
    }

    // ── T[N] — fixed-size arrays ────────────────────────────────────────────────────────────
    // Same shape as ReadArray/WriteArray plus the declared length. `length` is a parameter, not
    // part of the type, so one formatter serves every declared N. See IonFixedArrayFormatter<T>
    // in formatter.collections.cs for the wire rule and the reasoning.

    /// <inheritdoc cref="IonFixedArrayFormatter{T}.Read"/>
    public static IonArray<T> ReadFixedArray(CborReader reader, int length)
    {
        try
        {
            return IonFixedArrayFormatter<T>.Read(reader, length);
        }
        catch (IonDecodeException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw IonDecodeGuard.Translate(reader, e, $"{typeof(T).Name}[{length}]");
        }
    }

    /// <inheritdoc cref="IonFixedArrayFormatter{T}.ReadNullable"/>
    public static IonArray<T>? ReadNullableFixedArray(CborReader reader, int length)
    {
        if (IonDecodeGuard.PeekState(reader, $"{typeof(T).Name}[{length}]") == CborReaderState.Null)
        {
            reader.ReadNull();
            return null;
        }
        return ReadFixedArray(reader, length);
    }

    /// <inheritdoc cref="IonFixedArrayFormatter{T}.Write"/>
    public static void WriteFixedArray(CborWriter writer, IonArray<T> array, int length)
        => IonFixedArrayFormatter<T>.Write(writer, array, length);

    /// <inheritdoc cref="IonFixedArrayFormatter{T}.WriteNullable"/>
    public static void WriteNullableFixedArray(CborWriter writer, IonArray<T>? array, int length)
        => IonFixedArrayFormatter<T>.WriteNullable(writer, array, length);

    // ── Set<T> ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc cref="IonSetFormatter{T}.Read"/>
    public static HashSet<T> ReadSet(CborReader reader)
    {
        try
        {
            return IonSetFormatter<T>.Read(reader);
        }
        catch (IonDecodeException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw IonDecodeGuard.Translate(reader, e, $"Set<{typeof(T).Name}>");
        }
    }

    /// <inheritdoc cref="IonSetFormatter{T}.ReadNullable"/>
    public static HashSet<T>? ReadNullableSet(CborReader reader)
    {
        if (IonDecodeGuard.PeekState(reader, $"Set<{typeof(T).Name}>") == CborReaderState.Null)
        {
            reader.ReadNull();
            return null;
        }
        return ReadSet(reader);
    }

    /// <inheritdoc cref="IonSetFormatter{T}.Write"/>
    public static void WriteSet(CborWriter writer, IReadOnlyCollection<T> set)
        => IonSetFormatter<T>.Write(writer, set);

    /// <inheritdoc cref="IonSetFormatter{T}.WriteNullable"/>
    public static void WriteNullableSet(CborWriter writer, IReadOnlyCollection<T>? set)
        => IonSetFormatter<T>.WriteNullable(writer, set);
}

public static class IonFormatterStorageModuleInit
{
    [ModuleInitializer]
    public static void Init()
    {
        IonFormatterStorage<bool>.Value = new Ion_bool_Formatter();
        IonFormatterStorage<string>.Value = new Ion_string_Formatter();
        IonFormatterStorage<BigInteger>.Value = new Ion_bigint_Formatter();
        IonFormatterStorage<Guid>.Value = new Ion_guid_Formatter();
        IonFormatterStorage<DateTime>.Value = new Ion_datetime_Formatter();
        IonFormatterStorage<DateTimeOffset>.Value = new Ion_datetime_offset_Formatter();
        IonFormatterStorage<DateOnly>.Value = new Ion_dateonly_Formatter();
        IonFormatterStorage<TimeOnly>.Value = new Ion_timeonly_Formatter();
        IonFormatterStorage<TimeSpan>.Value = new Ion_duration_Formatter();
        IonFormatterStorage<IonBytes>.Value = new Ion_bytes_Formatter();
        IonFormatterStorage<Half>.Value = new Ion_f2_Formatter();
        IonFormatterStorage<float>.Value = new Ion_f4_Formatter();
        IonFormatterStorage<double>.Value = new Ion_f8_Formatter();
        IonFormatterStorage<byte>.Value = new Ion_u1_Formatter();
        IonFormatterStorage<sbyte>.Value = new Ion_i1_Formatter();
        IonFormatterStorage<short>.Value = new Ion_i2_Formatter();
        IonFormatterStorage<ushort>.Value = new Ion_u2_Formatter();
        IonFormatterStorage<int>.Value = new Ion_i4_Formatter();
        IonFormatterStorage<uint>.Value = new Ion_u4_Formatter();
        IonFormatterStorage<long>.Value = new Ion_i8_Formatter();
        IonFormatterStorage<ulong>.Value = new Ion_u8_Formatter();
        IonFormatterStorage<Int128>.Value = new Ion_i16_Formatter();
        IonFormatterStorage<UInt128>.Value = new Ion_u16_Formatter();
        IonFormatterStorage<decimal>.Value = new Ion_decimal_Formatter();
        IonFormatterStorage<IonProtocolError>.Value = new IonProtocolErrorFormatter();

        // Open-generic containers, so IonFormatterStorage<Dictionary<K,V>> and
        // IonFormatterStorage<HashSet<T>> resolve for any K/V/T and can therefore be nested inside
        // a message, an array, a Maybe or a Partial with no special-casing in the generator.
        // T[N] deliberately has NO entry here: N is not part of the CLR type, so a fixed-size array
        // is reached through IonFixedArrayFormatter<T>.Read(reader, n) with n passed at the call
        // site — see formatter.collections.cs.
        IonFormatterStorage.SetFormatterTypeFor(typeof(Dictionary<,>), typeof(Ion_map_Formatter<,>));
        IonFormatterStorage.SetFormatterTypeFor(typeof(HashSet<>), typeof(Ion_set_Formatter<>));

        // Open-generic fallback: makes IonFormatterStorage<IonPartial<X>> resolve for any X,
        // even when no generated schema was registered — in that case PartialFormatter<X>
        // derives the field schema reflectively (see ReflectionPartialSchema).
        // Generated code should call IonPartialSchema<X>.Register(...) instead, which installs
        // a concrete PartialFormatter<X> eagerly and needs neither MakeGenericType nor Activator.
        IonFormatterStorage.SetFormatterTypeFor(typeof(IonPartial<>), typeof(PartialFormatter<>));
    }
}

public sealed class Ion_bool_Formatter : IonFormatter<bool>
{
    public bool Read(CborReader reader)
        => reader.ReadBoolean();

    public void Write(CborWriter writer, bool value)
        => writer.WriteBoolean(value);
}

/// <summary>
/// <c>string</c> — a CBOR text string, definite or indefinite length.
/// </summary>
/// <remarks>
/// <b>Chunked (indefinite-length) text is accepted on read.</b> Maps, sets and fixed-size arrays
/// already accept an indefinite length in all three runtimes, and <c>partial.golden.json</c>
/// requires it for the <c>Partial</c> map, so a string is the odd one out if it does not — Rust
/// used to refuse it while C# and TypeScript accepted it, which is the worst of the three answers.
/// Leniency costs nothing here: the chunk boundaries carry no meaning and are erased by the
/// decode. Writers always emit a single definite-length chunk, so encoding is unchanged.
/// </remarks>
public sealed class Ion_string_Formatter : IonFormatter<string>
{
    public string Read(CborReader reader)
        => reader.ReadTextString();

    public void Write(CborWriter writer, string value)
        => writer.WriteTextString(value);
}

public sealed class Ion_bigint_Formatter : IonFormatter<BigInteger>
{
    public BigInteger Read(CborReader reader)
        => reader.ReadBigInteger();

    public void Write(CborWriter writer, BigInteger value)
        => writer.WriteBigInteger(value);
}

public sealed class Ion_guid_Formatter : IonFormatter<Guid>
{
    public Guid Read(CborReader reader)
    {
        var bytes = reader.ReadByteString();
        if (bytes.Length != 16)
            throw new CborContentException("Expected 16-byte GUID");

        return FromBigEndianBytes(bytes);
    }

    public void Write(CborWriter writer, Guid value)
    {
        Span<byte> buf = stackalloc byte[16];
        ToBigEndianBytes(value, buf);
        writer.WriteByteString(buf);
    }

    private static Guid FromBigEndianBytes(ReadOnlySpan<byte> bytes)
    {
        var a = BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]);
        var b = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(4, 2));
        var c = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(6, 2));

        Span<byte> tmp = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp[..4], a);
        BinaryPrimitives.WriteUInt16LittleEndian(tmp.Slice(4, 2), b);
        BinaryPrimitives.WriteUInt16LittleEndian(tmp.Slice(6, 2), c);
        bytes[8..].CopyTo(tmp[8..]);

        return new Guid(tmp);
    }

    private static void ToBigEndianBytes(Guid value, Span<byte> dest)
    {
        Span<byte> tmp = stackalloc byte[16];
        value.TryWriteBytes(tmp);

        var a = BinaryPrimitives.ReadUInt32LittleEndian(tmp[..4]);
        var b = BinaryPrimitives.ReadUInt16LittleEndian(tmp.Slice(4, 2));
        var c = BinaryPrimitives.ReadUInt16LittleEndian(tmp.Slice(6, 2));

        BinaryPrimitives.WriteUInt32BigEndian(dest[..4], a);
        BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(4, 2), b);
        BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(6, 2), c);
        tmp[8..].CopyTo(dest[8..]);
    }
}

// Ion_datetime_offset_Formatter and Ion_datetime_Formatter moved to formatter.datetime.cs, which
// documents the tag-0 / RFC 3339 / 7-fractional-digit wire rule and the three-runtime defect it
// fixes. They used to live here as CborWriter.WriteDateTimeOffset + CborReader.ReadDateTimeOffset,
// which parsed the offset and then discarded it via .UtcDateTime.

public sealed class Ion_dateonly_Formatter : IonFormatter<DateOnly>
{
    public DateOnly Read(CborReader reader)
    {
        reader.ReadStartArray();
        var i1 = reader.ReadInt32();
        var i2 = reader.ReadInt32();
        var i3 = reader.ReadInt32();
        var i4 = reader.ReadInt32(); // calendar reserved
        reader.ReadEndArray();
        return new DateOnly(i1, i2, i3);
    }

    public void Write(CborWriter writer, DateOnly value)
    {
        writer.WriteStartArray(4);
        writer.WriteInt32(value.Year);
        writer.WriteInt32(value.Month);
        writer.WriteInt32(value.Day);
        writer.WriteInt32(0); // calendar reserved
        writer.WriteEndArray();
    }
}

public sealed class Ion_timeonly_Formatter : IonFormatter<TimeOnly>
{
    public TimeOnly Read(CborReader reader)
    {
        reader.ReadStartArray();
        var h = reader.ReadInt32();
        var m = reader.ReadInt32();
        var s = reader.ReadInt32();
        var ms = reader.ReadInt32();
        var ns = reader.ReadInt32();
        reader.ReadEndArray();

        return new TimeOnly(h, m, s, ms, ns);
    }

    public void Write(CborWriter writer, TimeOnly value)
    {
        writer.WriteStartArray(5);
        writer.WriteInt32(value.Hour);
        writer.WriteInt32(value.Minute);
        writer.WriteInt32(value.Second);
        writer.WriteInt32(value.Millisecond);
        writer.WriteInt32(value.Microsecond);
        writer.WriteEndArray();
    }
}

public sealed class Ion_duration_Formatter : IonFormatter<TimeSpan>
{
    public TimeSpan Read(CborReader reader)
        => TimeSpan.FromTicks(reader.ReadInt64());

    public void Write(CborWriter writer, TimeSpan value)
        => writer.WriteInt64(value.Ticks);
}

public sealed class Ion_bytes_Formatter : IonFormatter<IonBytes>
{
    public IonBytes Read(CborReader reader) 
        => new(reader.ReadDefiniteLengthByteString());

    public void Write(CborWriter writer, IonBytes value)
        => writer.WriteByteString(value.Span);
}