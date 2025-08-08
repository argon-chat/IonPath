namespace ion.runtime;

public sealed class Ion_u1_Formatter : IonFormatter<byte>
{
    public byte Read(CborReader reader)
        => (byte)reader.ReadInt32();

    public void Write(CborWriter writer, ref byte value)
        => writer.WriteInt32(value);
}

public sealed class Ion_u2_Formatter : IonFormatter<ushort>
{
    public ushort Read(CborReader reader)
        => (ushort)reader.ReadUInt32();

    public void Write(CborWriter writer, ref ushort value)
        => writer.WriteUInt32(value);
}

public sealed class Ion_u4_Formatter : IonFormatter<uint>
{
    public uint Read(CborReader reader)
        => reader.ReadUInt32();

    public void Write(CborWriter writer, ref uint value)
        => writer.WriteUInt32(value);
}

public sealed class Ion_u8_Formatter : IonFormatter<ulong>
{
    public ulong Read(CborReader reader)
        => reader.ReadUInt64();

    public void Write(CborWriter writer, ref ulong value)
        => writer.WriteUInt64(value);
}

public sealed class Ion_u16_Formatter : IonFormatter<UInt128>
{
    public UInt128 Read(CborReader reader)
        => (UInt128)reader.ReadBigInteger();

    public void Write(CborWriter writer, ref UInt128 value)
        => writer.WriteBigInteger(value);
}