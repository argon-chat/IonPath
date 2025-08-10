namespace ion.runtime;

public sealed class Ion_i1_Formatter : IonFormatter<sbyte>
{
    public sbyte Read(CborReader reader)
        => (sbyte)reader.ReadInt32();

    public void Write(CborWriter writer,sbyte value)
        => writer.WriteInt32(value);
}

public sealed class Ion_i2_Formatter : IonFormatter<short>
{
    public short Read(CborReader reader)
        => (short)reader.ReadInt32();

    public void Write(CborWriter writer,short value)
        => writer.WriteInt32(value);
}

public sealed class Ion_i4_Formatter : IonFormatter<int>
{
    public int Read(CborReader reader)
        => reader.ReadInt32();

    public void Write(CborWriter writer,int value)
        => writer.WriteInt32(value);
}

public sealed class Ion_i8_Formatter : IonFormatter<long>
{
    public long Read(CborReader reader)
        => reader.ReadInt64();

    public void Write(CborWriter writer,long value)
        => writer.WriteInt64(value);
}

public sealed class Ion_i16_Formatter : IonFormatter<Int128>
{
    public Int128 Read(CborReader reader)
        => (Int128)reader.ReadBigInteger();

    public void Write(CborWriter writer,Int128 value)
        => writer.WriteBigInteger(value);
}
