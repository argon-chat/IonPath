namespace ion.runtime;

using ion.runtime.network;
#pragma warning disable CA2255
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
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
    public static void ReadEndArrayAndSkip(this CborReader reader, int skipCount)
    {
        for (var i = 0; i < Math.Abs(skipCount); i++)
            reader.SkipValue();
        reader.ReadEndArray();
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
    public static T? ReadNullable<T>(this CborReader reader, _StructTag<T> _ = default)
        where T : struct
    {
        var state = reader.PeekState();
        if (state != CborReaderState.Null)
            return IonFormatterStorage<T>.Read(reader);

        reader.ReadNull();
        return null;
    }

    public static T ReadNullable<T>(this CborReader reader, _ClassTag<T> _ = default)
        where T : class
    {
        var state = reader.PeekState();
        if (state != CborReaderState.Null)
            return IonFormatterStorage<T>.Read(reader);

        reader.ReadNull();
        return null!;
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

    public static T Read(CborReader reader)
    {
        if (Value is null)
            throw new InvalidOperationException($"Ion Formatter for type '{typeof(T).FullName}' is not registered");
        return Value.Read(reader);
    }

    public static void Write(CborWriter writer, T value)
    {
        if (Value is null)
            throw new InvalidOperationException($"Ion Formatter for type '{typeof(T).FullName}' is not registered");
        Value.Write(writer, value);
    }

    public static T? ReadNullable(CborReader reader)
    {
        var state = reader.PeekState();
        if (state != CborReaderState.Null)
            return Read(reader);
        reader.ReadNull();
        return default;
    }

    public static IonMaybe<T> ReadMaybe(CborReader reader)
    {
        var state = reader.PeekState();
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


    public static IonArray<T> ReadArray(CborReader reader)
    {
        var size = reader.ReadStartArray();
        if (size is null) throw new InvalidOperationException();

        using var span = MemoryPool<T>.Shared.Rent(size.Value);

        for (var i = 0; i < size.Value; i++)
            span.Memory.Span[i] = Read(reader);

        reader.ReadEndArray();

        return new IonArray<T>(span.Memory.Span);
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
        IonFormatterStorage<IonProtocolError>.Value = new IonProtocolErrorFormatter();
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

public sealed class Ion_datetime_offset_Formatter : IonFormatter<DateTimeOffset>
{
    public DateTimeOffset Read(CborReader reader)
        => reader.ReadDateTimeOffset();

    public void Write(CborWriter writer, DateTimeOffset value)
        => writer.WriteDateTimeOffset(value);
}

public sealed class Ion_datetime_Formatter : IonFormatter<DateTime>
{
    public DateTime Read(CborReader reader)
        => reader.ReadDateTimeOffset().UtcDateTime;

    public void Write(CborWriter writer, DateTime value)
        => writer.WriteDateTimeOffset(value);
}

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