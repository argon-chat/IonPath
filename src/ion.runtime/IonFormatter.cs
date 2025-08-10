namespace ion.runtime;
#pragma warning disable CA2255
using System.Buffers;
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

public interface IonFormatter<T>
{
    T Read(CborReader reader);

    void Write(CborWriter writer, T value);
}

public static class IonFormatterStorage<T>
{
    public static IonFormatter<T> Value { get; set; } = null!;

    public static T Read(CborReader reader) => Value.Read(reader);
    public static void Write(CborWriter writer, T value) => Value.Write(writer, value);

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
        IonFormatterStorage<string>.Value = new Ion_string_Formatter();
        IonFormatterStorage<BigInteger>.Value = new Ion_bigint_Formatter();
        IonFormatterStorage<Guid>.Value = new Ion_guid_Formatter();
        IonFormatterStorage<DateTimeOffset>.Value = new Ion_datetime_Formatter();
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
    }
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
        return new Guid(bytes);
    }

    public void Write(CborWriter writer, Guid value)
    {
        Span<byte> buf = stackalloc byte[16];
        if (!value.TryWriteBytes(buf))
            throw new InvalidOperationException("Failed to write GUID bytes");
        writer.WriteByteString(buf);
    }
}

public sealed class Ion_datetime_Formatter : IonFormatter<DateTimeOffset>
{
    public DateTimeOffset Read(CborReader reader)
        => reader.ReadDateTimeOffset();

    public void Write(CborWriter writer, DateTimeOffset value)
        => writer.WriteDateTimeOffset(value);
}

public sealed class Ion_dateonly_Formatter : IonFormatter<DateOnly>
{
    public DateOnly Read(CborReader reader)
    {
        var i1 = reader.ReadInt32();
        var i2 = reader.ReadInt32();
        var i3 = reader.ReadInt32();
        var i4 = reader.ReadInt32(); // calendar reserved

        return new DateOnly(i1, i2, i3);
    }

    public void Write(CborWriter writer, DateOnly value)
    {
        writer.WriteInt32(value.Year);
        writer.WriteInt32(value.Month);
        writer.WriteInt32(value.Day);
        writer.WriteInt32(0); // calendar reserved
    }
}

public sealed class Ion_timeonly_Formatter : IonFormatter<TimeOnly>
{
    public TimeOnly Read(CborReader reader)
    {
        var h = reader.ReadInt32();
        var m = reader.ReadInt32();
        var s = reader.ReadInt32();
        var ms = reader.ReadInt32();
        var ns = reader.ReadInt32();

        return new TimeOnly(h, m, s, ms, ns);
    }

    public void Write(CborWriter writer, TimeOnly value)
    {
        writer.WriteInt32(value.Hour);
        writer.WriteInt32(value.Minute);
        writer.WriteInt32(value.Second);
        writer.WriteInt32(value.Millisecond);
        writer.WriteInt32(value.Microsecond);
    }
}

public sealed class Ion_duration_Formatter : IonFormatter<TimeSpan>
{
    public TimeSpan Read(CborReader reader)
        => TimeSpan.FromTicks(reader.ReadInt64());

    public void Write(CborWriter writer, TimeSpan value)
        => writer.WriteInt64(value.Ticks);
}
