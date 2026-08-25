namespace ion.runtime;

using System.Numerics;

// ── Ion's unsigned integers ─────────────────────────────────────────────────────────────────────
//
// The same rule as the signed side, plus the sign itself: a CBOR negative integer is a well-formed
// item that no unsigned Ion type can hold. `(byte)reader.ReadInt32()` used to reinterpret -1 as
// 255 and 256 as 0, both silently — and because `enum E : u1` reads through Ion_u1_Formatter, an
// enum built on a narrow base type inherited exactly that defect: 256 arrived as `Free`.
//
// Writers are untouched.

public sealed class Ion_u1_Formatter : IonFormatter<byte>
{
    public byte Read(CborReader reader)
        => (byte)IonInteger.ReadUnsigned(reader, byte.MaxValue, "u1");

    public void Write(CborWriter writer, byte value)
        => writer.WriteInt32(value);
}

public sealed class Ion_u2_Formatter : IonFormatter<ushort>
{
    public ushort Read(CborReader reader)
        => (ushort)IonInteger.ReadUnsigned(reader, ushort.MaxValue, "u2");

    public void Write(CborWriter writer, ushort value)
        => writer.WriteUInt32(value);
}

public sealed class Ion_u4_Formatter : IonFormatter<uint>
{
    public uint Read(CborReader reader)
        => (uint)IonInteger.ReadUnsigned(reader, uint.MaxValue, "u4");

    public void Write(CborWriter writer, uint value)
        => writer.WriteUInt32(value);
}

public sealed class Ion_u8_Formatter : IonFormatter<ulong>
{
    public ulong Read(CborReader reader)
        => IonInteger.ReadUnsigned(reader, ulong.MaxValue, "u8");

    public void Write(CborWriter writer, ulong value)
        => writer.WriteUInt64(value);
}

public sealed class Ion_u16_Formatter : IonFormatter<UInt128>
{
    private static readonly BigInteger Max = UInt128.MaxValue;

    public UInt128 Read(CborReader reader)
        => (UInt128)IonInteger.ReadBig(reader, BigInteger.Zero, Max, "u16");

    public void Write(CborWriter writer, UInt128 value)
        => writer.WriteBigInteger(value);
}
