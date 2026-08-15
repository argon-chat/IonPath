namespace ion.runtime;

using System.Buffers.Binary;

/// <summary>
/// Wire encoding for Ion's three float widths.
/// <para>
/// <b>Rule: every float is written at its declared width.</b> Ion is a schema-first format —
/// the field's width comes from the contract, so the wire honours it:
/// <c>f2</c> → <c>0xF9</c> + 2 bytes, <c>f4</c> → <c>0xFA</c> + 4 bytes, <c>f8</c> → <c>0xFB</c> + 8 bytes,
/// always, regardless of value.
/// </para>
/// <para>
/// <b>Why this class exists.</b> <see cref="CborWriter.WriteSingle"/> and
/// <see cref="CborWriter.WriteDouble"/> shrink a float to the shortest lossless CBOR float in every
/// conformance mode except <see cref="CborConformanceMode.Ctap2Canonical"/> — <c>1.5f</c> became
/// <c>f93e00</c>, <c>4f</c> became <c>f94400</c>, and a <c>double</c> could shrink all the way to
/// float16. packages/ion.webcore.js and packages/ion.rustcore (minicbor) always emit the declared
/// width, so C# produced different bytes for the same message whenever a value happened to fit a
/// narrower encoding. That made golden vectors runtime-dependent and would break any future
/// signing, hashing, dedup or content-addressing of payloads.
/// </para>
/// <para>
/// Switching the writer to <see cref="CborConformanceMode.Ctap2Canonical"/> would fix the floats
/// but also mandate sorted map keys and forbid indefinite lengths, which would break
/// <see cref="IonPartial{T}"/> — its map is deliberately written in Ion declaration order. So the
/// width is forced here instead, via <see cref="CborWriter.WriteEncodedValue"/> with a hand-built
/// header + big-endian payload. That is accepted in all four conformance modes and participates
/// normally in array/map item accounting, so the writer's mode is left alone.
/// </para>
/// <para>
/// <b>NaN is canonicalised</b> to the positive quiet NaN with an empty payload
/// (<c>f2 7e00</c> / <c>f4 7fc00000</c> / <c>f8 7ff8000000000000</c>). This is required, not
/// cosmetic: .NET's <see cref="float.NaN"/> has the sign bit <i>set</i> (<c>ffc00000</c>) while
/// Rust's <c>f32::NAN</c> and JavaScript's <c>NaN</c> are positive (<c>7fc00000</c>), and
/// JavaScript cannot observe or reproduce a NaN payload at all. Emitting raw bits would therefore
/// reintroduce the very divergence this class removes. Every other special value —
/// <c>+0.0</c>, <c>-0.0</c>, subnormals, <c>±Inf</c> — is written bit-for-bit, so <c>-0.0</c>
/// stays <c>-0.0</c> on the wire.
/// </para>
/// <para>
/// <b>Reading accepts every width</b> for every float type, in both directions (an <c>f4</c> field
/// may receive an <c>f8</c>-encoded value and vice versa). <see cref="CborReader.ReadDouble"/> is
/// the only one of the three <c>Read*</c> methods that accepts <c>f9</c>, <c>fa</c> and <c>fb</c>
/// alike — <c>ReadSingle</c> rejects <c>fb</c> and <c>ReadHalf</c> rejects both <c>fa</c> and
/// <c>fb</c> — so all three formatters read through it and narrow afterwards. Widening from a
/// narrower wire width is exact; narrowing from a wider one rounds, which is the intended
/// behaviour for a field whose declared width is smaller than what the peer sent. This is what
/// keeps the change non-wire-breaking: payloads written by the previous release, where C# shrank
/// floats, still decode.
/// </para>
/// </summary>
public static class IonFloatWire
{
    /// <summary>Positive quiet NaN, empty payload — the canonical <c>f2</c> NaN on the Ion wire.</summary>
    public const ushort CanonicalNaNBitsF2 = 0x7E00;

    /// <summary>Positive quiet NaN, empty payload — the canonical <c>f4</c> NaN on the Ion wire.</summary>
    public const uint CanonicalNaNBitsF4 = 0x7FC0_0000u;

    /// <summary>Positive quiet NaN, empty payload — the canonical <c>f8</c> NaN on the Ion wire.</summary>
    public const ulong CanonicalNaNBitsF8 = 0x7FF8_0000_0000_0000ul;

    private const byte HeaderF2 = 0xF9;
    private const byte HeaderF4 = 0xFA;
    private const byte HeaderF8 = 0xFB;

    /// <summary>Writes <paramref name="value"/> as <c>0xF9</c> + 2 big-endian bytes, always.</summary>
    public static void WriteIonHalf(this CborWriter writer, Half value)
    {
        Span<byte> buf = stackalloc byte[3];
        buf[0] = HeaderF2;
        BinaryPrimitives.WriteUInt16BigEndian(buf[1..], Half.IsNaN(value)
            ? CanonicalNaNBitsF2
            : BitConverter.HalfToUInt16Bits(value));
        writer.WriteEncodedValue(buf);
    }

    /// <summary>Writes <paramref name="value"/> as <c>0xFA</c> + 4 big-endian bytes, always.</summary>
    public static void WriteIonSingle(this CborWriter writer, float value)
    {
        Span<byte> buf = stackalloc byte[5];
        buf[0] = HeaderF4;
        BinaryPrimitives.WriteUInt32BigEndian(buf[1..], float.IsNaN(value)
            ? CanonicalNaNBitsF4
            : BitConverter.SingleToUInt32Bits(value));
        writer.WriteEncodedValue(buf);
    }

    /// <summary>Writes <paramref name="value"/> as <c>0xFB</c> + 8 big-endian bytes, always.</summary>
    public static void WriteIonDouble(this CborWriter writer, double value)
    {
        Span<byte> buf = stackalloc byte[9];
        buf[0] = HeaderF8;
        BinaryPrimitives.WriteUInt64BigEndian(buf[1..], double.IsNaN(value)
            ? CanonicalNaNBitsF8
            : BitConverter.DoubleToUInt64Bits(value));
        writer.WriteEncodedValue(buf);
    }

    /// <summary>Reads a float of any wire width (<c>f9</c>/<c>fa</c>/<c>fb</c>) as a <see cref="Half"/>.</summary>
    public static Half ReadIonHalf(this CborReader reader) => (Half)reader.ReadDouble();

    /// <summary>Reads a float of any wire width (<c>f9</c>/<c>fa</c>/<c>fb</c>) as a <see cref="float"/>.</summary>
    public static float ReadIonSingle(this CborReader reader) => (float)reader.ReadDouble();

    /// <summary>Reads a float of any wire width (<c>f9</c>/<c>fa</c>/<c>fb</c>) as a <see cref="double"/>.</summary>
    public static double ReadIonDouble(this CborReader reader) => reader.ReadDouble();
}

public sealed class Ion_f2_Formatter : IonFormatter<Half>
{
    public Half Read(CborReader reader)
        => reader.ReadIonHalf();

    public void Write(CborWriter writer, Half value)
        => writer.WriteIonHalf(value);
}

public sealed class Ion_f4_Formatter : IonFormatter<float>
{
    public float Read(CborReader reader)
        => reader.ReadIonSingle();

    public void Write(CborWriter writer, float value)
        => writer.WriteIonSingle(value);
}

public sealed class Ion_f8_Formatter : IonFormatter<double>
{
    public double Read(CborReader reader)
        => reader.ReadIonDouble();

    public void Write(CborWriter writer, double value)
        => writer.WriteIonDouble(value);
}
