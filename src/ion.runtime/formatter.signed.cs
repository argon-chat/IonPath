namespace ion.runtime;

using System.Numerics;

// ── Ion's signed integers ───────────────────────────────────────────────────────────────────────
//
// CBOR integers carry no width. `i1`, `i2` and `i4` are therefore *narrower* than what the wire can
// hold, and the reader is the only place the declared width can be enforced. These used to be
// truncating casts — `(sbyte)reader.ReadInt32()` — which turned an out-of-range value into a
// different, in-range one and reported nothing. See IonInteger and IonIntegerRangeException.
//
// Writers are untouched: an in-range value produces exactly the bytes it did before.

public sealed class Ion_i1_Formatter : IonFormatter<sbyte>
{
    public sbyte Read(CborReader reader)
        => (sbyte)IonInteger.ReadSigned(reader, sbyte.MinValue, sbyte.MaxValue, "i1");

    public void Write(CborWriter writer, sbyte value)
        => writer.WriteInt32(value);
}

public sealed class Ion_i2_Formatter : IonFormatter<short>
{
    public short Read(CborReader reader)
        => (short)IonInteger.ReadSigned(reader, short.MinValue, short.MaxValue, "i2");

    public void Write(CborWriter writer, short value)
        => writer.WriteInt32(value);
}

public sealed class Ion_i4_Formatter : IonFormatter<int>
{
    public int Read(CborReader reader)
        => (int)IonInteger.ReadSigned(reader, int.MinValue, int.MaxValue, "i4");

    public void Write(CborWriter writer, int value)
        => writer.WriteInt32(value);
}

public sealed class Ion_i8_Formatter : IonFormatter<long>
{
    public long Read(CborReader reader)
        => IonInteger.ReadSigned(reader, long.MinValue, long.MaxValue, "i8");

    public void Write(CborWriter writer, long value)
        => writer.WriteInt64(value);
}

public sealed class Ion_i16_Formatter : IonFormatter<Int128>
{
    private static readonly BigInteger Min = Int128.MinValue;
    private static readonly BigInteger Max = Int128.MaxValue;

    public Int128 Read(CborReader reader)
        => (Int128)IonInteger.ReadBig(reader, Min, Max, "i16");

    public void Write(CborWriter writer, Int128 value)
        => writer.WriteBigInteger(value);
}
