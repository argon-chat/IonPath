namespace ion.runtime;

using System.Formats.Cbor;
using System.Numerics;

/// <summary>
/// Wire encoding for Ion's <c>decimal</c> primitive — exact-precision decimal arithmetic, as
/// distinct from the binary floating point of <c>f2</c>/<c>f4</c>/<c>f8</c>.
/// <para>
/// <b>Rule: CBOR tag 4 (decimal fraction, RFC 8949 §3.4.4) wrapping a definite-length 2-element
/// array <c>[exponent, mantissa]</c>, whose value is <c>mantissa × 10^exponent</c>.</b> The
/// exponent is always a plain CBOR integer. The mantissa is a plain CBOR integer when it fits the
/// i64/u64 range and a tag 2 / tag 3 bignum with a minimal-length big-endian magnitude when it
/// does not.
/// </para>
/// <para>
/// <b>Canonical form: the mantissa is normalised on write</b> — trailing decimal zeros are
/// stripped and the exponent raised to compensate — and zero is always <c>[0, 0]</c>. This is
/// required for byte identity, not cosmetic. <c>1.50</c> and <c>1.5</c> are the same number, but
/// <see cref="decimal"/> is the only one of the three runtime types that <i>remembers</i> a
/// trailing-zero scale: <c>1.50m</c> is unscaled 150 with scale 2, while a TypeScript
/// <c>IonDecimal(-1, 15n)</c> or a Rust <c>IonDecimal { exponent: -1, mantissa: 15 }</c> has no
/// way to express the difference. Leaving the mantissa "as authored" would therefore mean C#
/// alone could emit a form the other two runtimes can never reproduce — the same class of defect
/// as shortest-form floats.
/// </para>
/// <para>
/// <b>Negative zero.</b> <see cref="decimal"/> can hold <c>-0.0m</c>. It normalises to a mantissa
/// of zero, and CBOR has no negative zero integer, so <c>-0.0m</c> and <c>0m</c> are the same four
/// bytes. Documented, not accidental.
/// </para>
/// <para>
/// <b>Range.</b> Tag 4 permits an arbitrary mantissa and exponent; <see cref="decimal"/> permits a
/// 96-bit unscaled magnitude and a scale of 0..28. The gap is reachable from the TypeScript and
/// Rust runtimes, which have no such limit, so decoding an out-of-range value raises
/// <see cref="IonDecimalRangeException"/> — never an <see cref="OverflowException"/> leaking out
/// of a conversion, and never a silently rounded result. Encoding is unaffected:
/// <see cref="WriteIonDecimalParts"/> writes any exponent/mantissa pair, so C# can still produce
/// byte-identical output for values it cannot itself hold.
/// </para>
/// <para>
/// <b>The tag is required on read.</b> Unlike the leniencies below, accepting an untagged
/// <c>[exponent, mantissa]</c> array would not be free: that is exactly the encoding of an
/// ordinary <c>i8[2]</c> field, so <c>decimal</c> and a two-element integer array would become
/// indistinguishable in a capture. Tag 5 (bigfloat) is the same array shape with a base-2
/// exponent and is rejected outright rather than misread as base 10.
/// </para>
/// <para>
/// <b>Readers are lenient</b> about everything that cannot be confused with another type: an
/// indefinite-length inner array, a non-normalised mantissa, and a bignum holding a value that
/// would have fitted a plain integer are all accepted and re-encoded canonically. Golden vectors:
/// <c>/tests/golden/decimal.golden.json</c>.
/// </para>
/// </summary>
public static class IonDecimalWire
{
    /// <summary>CBOR tag 4: decimal fraction.</summary>
    public const ulong DecimalFractionTag = 4;

    /// <summary>Largest unscaled magnitude a <see cref="decimal"/> can hold: 2^96 - 1.</summary>
    public static readonly BigInteger MaxUnscaled = (BigInteger.One << 96) - 1;

    /// <summary>Largest scale a <see cref="decimal"/> can hold.</summary>
    public const int MaxScale = 28;

    /// <summary>
    /// Reduces an exponent/mantissa pair to Ion's canonical form: no trailing zeros in the
    /// mantissa, and zero represented as exactly <c>(0, 0)</c>.
    /// </summary>
    public static (int Exponent, BigInteger Mantissa) Canonicalize(int exponent, BigInteger mantissa)
    {
        if (mantissa.IsZero)
            return (0, BigInteger.Zero);

        var e = (long)exponent;
        while (true)
        {
            var q = BigInteger.DivRem(mantissa, 10, out var r);
            if (!r.IsZero)
                break;
            mantissa = q;
            e++;
        }

        // An exponent can only ever be raised by normalisation, and only by as many steps as the
        // mantissa has digits, so int overflow is unreachable for any mantissa a peer could send.
        return (checked((int)e), mantissa);
    }

    /// <summary>Decomposes a <see cref="decimal"/> into its canonical exponent and mantissa.</summary>
    public static (int Exponent, BigInteger Mantissa) Canonicalize(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);

        var scale = (bits[3] >> 16) & 0xFF;
        var isNegative = (bits[3] & int.MinValue) != 0;

        var unscaled = ((BigInteger)(uint)bits[2] << 64)
                       | ((BigInteger)(uint)bits[1] << 32)
                       | (uint)bits[0];

        return Canonicalize(-scale, isNegative ? -unscaled : unscaled);
    }

    /// <summary>
    /// Materialises an exponent/mantissa pair as a <see cref="decimal"/>.
    /// </summary>
    /// <exception cref="IonDecimalRangeException">
    /// The value needs a scale above 28 or an unscaled magnitude above 2^96 - 1.
    /// </exception>
    public static decimal ToDecimal(int exponent, BigInteger mantissa)
    {
        var (e, m) = Canonicalize(exponent, mantissa);

        if (m.IsZero)
            return decimal.Zero;

        if (e > 0)
        {
            // |value| >= 10^e, and 10^29 already exceeds decimal.MaxValue (~7.92e28), so bail out
            // before BigInteger.Pow is asked to build an astronomically large number.
            if (e > 29)
                throw new IonDecimalRangeException(e, m,
                    $"exponent {e} implies a magnitude of at least 1E{e}, beyond decimal.MaxValue");

            m *= BigInteger.Pow(10, e);
            e = 0;
        }

        var scale = -(long)e;
        if (scale > MaxScale)
            throw new IonDecimalRangeException(e, m,
                $"scale {scale} exceeds decimal's maximum of {MaxScale}");

        var isNegative = m.Sign < 0;
        var magnitude = BigInteger.Abs(m);
        if (magnitude > MaxUnscaled)
            throw new IonDecimalRangeException(e, m,
                $"unscaled magnitude {magnitude} exceeds decimal's maximum of {MaxUnscaled}");

        return new decimal(
            (int)(uint)(magnitude & uint.MaxValue),
            (int)(uint)((magnitude >> 32) & uint.MaxValue),
            (int)(uint)((magnitude >> 64) & uint.MaxValue),
            isNegative,
            (byte)scale);
    }

    /// <summary>Writes <paramref name="value"/> as tag 4 + <c>[exponent, mantissa]</c>, canonicalised.</summary>
    public static void WriteIonDecimal(this CborWriter writer, decimal value)
    {
        var (exponent, mantissa) = Canonicalize(value);
        writer.WriteIonDecimalParts(exponent, mantissa);
    }

    /// <summary>
    /// Writes an arbitrary exponent/mantissa pair as tag 4, canonicalising it first.
    /// </summary>
    /// <remarks>
    /// This is the escape hatch that lets C# emit — and so prove byte-identity for — decimals
    /// whose value it cannot itself hold in a <see cref="decimal"/>.
    /// </remarks>
    public static void WriteIonDecimalParts(this CborWriter writer, int exponent, BigInteger mantissa)
    {
        var (e, m) = Canonicalize(exponent, mantissa);

        writer.WriteTag((CborTag)DecimalFractionTag);
        writer.WriteStartArray(2);
        writer.WriteInt64(e);
        WriteMantissa(writer, m);
        writer.WriteEndArray();
    }

    /// <summary>Reads a tag 4 decimal fraction as a <see cref="decimal"/>.</summary>
    /// <exception cref="IonDecimalRangeException">The value is outside <see cref="decimal"/>'s range.</exception>
    public static decimal ReadIonDecimal(this CborReader reader)
    {
        var (exponent, mantissa) = reader.ReadIonDecimalParts();
        return ToDecimal(exponent, mantissa);
    }

    /// <summary>
    /// Reads a tag 4 decimal fraction as its canonical exponent/mantissa pair, without applying
    /// <see cref="decimal"/>'s range limits.
    /// </summary>
    public static (int Exponent, BigInteger Mantissa) ReadIonDecimalParts(this CborReader reader)
    {
        if (reader.PeekState() != CborReaderState.Tag)
            throw new IonMalformedValueException("decimal",
                $"expected CBOR tag {DecimalFractionTag}, got {reader.PeekState()}");

        var tag = (ulong)reader.ReadTag();
        if (tag != DecimalFractionTag)
            throw new IonUnexpectedTagException(DecimalFractionTag, tag, "decimal");

        if (reader.PeekState() != CborReaderState.StartArray)
            throw new IonMalformedValueException("decimal",
                $"tag {DecimalFractionTag} must wrap an array, got {reader.PeekState()}");

        var length = reader.ReadStartArray();
        if (length is not null && length != 2)
            throw new IonMalformedValueException("decimal",
                $"tag {DecimalFractionTag} requires exactly 2 elements, got {length}");

        var exponent = ReadExponent(reader);
        var mantissa = ReadMantissa(reader);

        // An indefinite-length inner array is accepted, but it must still hold exactly two items.
        if (length is null && reader.PeekState() != CborReaderState.EndArray)
            throw new IonMalformedValueException("decimal",
                $"tag {DecimalFractionTag} requires exactly 2 elements, got more");

        reader.ReadEndArray();
        return Canonicalize(exponent, mantissa);
    }

    private static int ReadExponent(CborReader reader)
    {
        if (reader.PeekState() is not (CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger))
            throw new IonMalformedValueException("decimal",
                $"exponent must be an integer, got {reader.PeekState()}");

        try
        {
            return reader.ReadInt32();
        }
        catch (OverflowException e)
        {
            throw new IonMalformedValueException("decimal", "exponent does not fit an int32: " + e.Message);
        }
    }

    private static BigInteger ReadMantissa(CborReader reader)
    {
        switch (reader.PeekState())
        {
            case CborReaderState.UnsignedInteger:
                return reader.ReadUInt64();

            // Covers the whole CBOR negative range, including -2^64 .. -2^63-1, which ReadInt64
            // would reject with an OverflowException.
            case CborReaderState.NegativeInteger:
                return -1 - (BigInteger)reader.ReadCborNegativeIntegerRepresentation();

            case CborReaderState.Tag:
                var tag = (ulong)reader.PeekTag();
                if (tag is not (2 or 3))
                    throw new IonUnexpectedTagException(2, tag, "decimal mantissa");
                return reader.ReadBigInteger();

            default:
                throw new IonMalformedValueException("decimal",
                    $"mantissa must be an integer or a tag 2/3 bignum, got {reader.PeekState()}");
        }
    }

    /// <summary>
    /// Canonical mantissa encoding: a plain CBOR integer while the value fits i64/u64, a tag 2/3
    /// bignum beyond that. <see cref="CborWriter.WriteBigInteger"/> is <i>always</i> tagged, so it
    /// cannot be used unconditionally without making small mantissas non-canonical.
    /// </summary>
    private static void WriteMantissa(CborWriter writer, BigInteger mantissa)
    {
        if (mantissa >= long.MinValue && mantissa <= long.MaxValue)
            writer.WriteInt64((long)mantissa);
        else if (mantissa.Sign >= 0 && mantissa <= ulong.MaxValue)
            writer.WriteUInt64((ulong)mantissa);
        else
            writer.WriteBigInteger(mantissa);
    }
}

/// <summary><c>decimal</c> ⇒ <see cref="decimal"/>.</summary>
public sealed class Ion_decimal_Formatter : IonFormatter<decimal>
{
    public decimal Read(CborReader reader)
        => reader.ReadIonDecimal();

    public void Write(CborWriter writer, decimal value)
        => writer.WriteIonDecimal(value);
}
