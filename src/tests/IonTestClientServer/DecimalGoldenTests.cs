namespace IonTestClientServer;

using ion.runtime;
using System.Formats.Cbor;
using System.Numerics;

/// <summary>
/// Cross-runtime golden vectors for Ion's <c>decimal</c> primitive (CBOR tag 4).
/// <para>
/// <c>/tests/golden/decimal.golden.json</c> is also consumed by
/// <c>packages/ion.webcore.js/test/decimal.golden.test.ts</c> and
/// <c>packages/ion.rustcore/tests/decimal_golden.rs</c>.
/// </para>
/// <para>
/// C# is the runtime with the narrow type: <see cref="decimal"/> caps out at a 96-bit unscaled
/// magnitude and a scale of 28, while tag 4 and the other two runtimes have no such limit. So the
/// vectors split in two — those inside <see cref="decimal"/>'s range round-trip as a
/// <see cref="decimal"/>, and those outside it must still <b>encode</b> byte-identically (via the
/// exponent/mantissa API) while <b>decoding</b> to a typed range error.
/// </para>
/// </summary>
public class DecimalGoldenTests
{
    public sealed record Vector(
        string Name, int Exponent, BigInteger Mantissa,
        int CanonicalExponent, BigInteger CanonicalMantissa,
        string Value, bool InRange, string Hex, string Notes);

    public sealed record DecodeOnly(string Name, string Hex, string ReencodedHex, string Notes);

    public sealed record Malformed(string Name, string Hex, string Notes);

    // ── encode from the authored exponent/mantissa, canonicalising ──

    /// <summary>
    /// Encoding goes through the parts API, so it covers the out-of-range vectors too: C# cannot
    /// <i>hold</i> 2^96 in a <see cref="decimal"/>, but it must still be able to write the same
    /// bytes for it as TypeScript and Rust do.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(VectorNames))]
    public void Golden_EncodeFromParts(string name)
    {
        var v = Get(name);
        var w = new CborWriter();
        w.WriteIonDecimalParts(v.Exponent, v.Mantissa);

        Assert.That(GoldenFile.Hex(w), Is.EqualTo(v.Hex),
            $"golden decimal vector '{name}' ({v.Value}): {v.Notes}");
    }

    /// <summary>Values inside <see cref="decimal"/>'s range also encode from a real decimal.</summary>
    [Test]
    [TestCaseSource(nameof(InRangeVectorNames))]
    public void Golden_EncodeFromDecimal(string name)
    {
        var v = Get(name);
        var value = IonDecimalWire.ToDecimal(v.Exponent, v.Mantissa);

        var w = new CborWriter();
        IonFormatterStorage<decimal>.Write(w, value);

        Assert.That(GoldenFile.Hex(w), Is.EqualTo(v.Hex),
            $"golden decimal vector '{name}' ({v.Value}): {v.Notes}");
    }

    [Test]
    [TestCaseSource(nameof(InRangeVectorNames))]
    public void Golden_Decode(string name)
    {
        var v = Get(name);
        Assert.That(Reencode(v.Hex), Is.EqualTo(v.Hex),
            $"golden decimal vector '{name}' ({v.Value}): {v.Notes}");
    }

    /// <summary>The canonical parts the file declares are the ones the reader produces.</summary>
    [Test]
    [TestCaseSource(nameof(VectorNames))]
    public void Golden_DecodeToCanonicalParts(string name)
    {
        var v = Get(name);
        var (exponent, mantissa) = new CborReader(GoldenFile.Bytes(v.Hex)).ReadIonDecimalParts();

        Assert.Multiple(() =>
        {
            Assert.That(exponent, Is.EqualTo(v.CanonicalExponent), $"'{name}' exponent");
            Assert.That(mantissa, Is.EqualTo(v.CanonicalMantissa), $"'{name}' mantissa");
        });
    }

    /// <summary>
    /// OUT-OF-RANGE GUARD. Decoding a value <see cref="decimal"/> cannot hold must raise
    /// <see cref="IonDecimalRangeException"/> — never an <see cref="OverflowException"/> escaping
    /// from a conversion, and never a silently rounded result.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(OutOfRangeVectorNames))]
    public void Golden_OutOfRangeDecodeRaisesTypedError(string name)
    {
        var v = Get(name);

        var e = Assert.Throws<IonDecimalRangeException>(() => Reencode(v.Hex),
            $"golden decimal vector '{name}' ({v.Value}): {v.Notes}")!;

        Assert.Multiple(() =>
        {
            Assert.That(e, Is.InstanceOf<IonDecodeException>(), "must be part of the typed decode hierarchy");
            Assert.That(e.Mantissa, Is.EqualTo(v.CanonicalMantissa), "the error carries the rejected mantissa");
            Assert.That(e.Exponent, Is.EqualTo(v.CanonicalExponent), "the error carries the rejected exponent");
        });
    }

    /// <summary>Reading the raw parts still works for out-of-range values; only the narrowing fails.</summary>
    [Test]
    [TestCaseSource(nameof(OutOfRangeVectorNames))]
    public void Golden_OutOfRangePartsStillDecode(string name)
    {
        var v = Get(name);
        var (exponent, mantissa) = new CborReader(GoldenFile.Bytes(v.Hex)).ReadIonDecimalParts();

        Assert.Multiple(() =>
        {
            Assert.That(exponent, Is.EqualTo(v.CanonicalExponent));
            Assert.That(mantissa, Is.EqualTo(v.CanonicalMantissa));
        });
    }

    [Test]
    [TestCaseSource(nameof(DecodeOnlyNames))]
    public void Golden_DecodeOnly(string name)
    {
        var v = GetDecodeOnly(name);
        var (exponent, mantissa) = new CborReader(GoldenFile.Bytes(v.Hex)).ReadIonDecimalParts();

        var w = new CborWriter();
        w.WriteIonDecimalParts(exponent, mantissa);

        Assert.That(GoldenFile.Hex(w), Is.EqualTo(v.ReencodedHex), $"decode-only vector '{name}': {v.Notes}");
    }

    [Test]
    [TestCaseSource(nameof(MalformedNames))]
    public void Golden_MalformedRaisesTypedError(string name)
    {
        var v = GetMalformed(name);
        Assert.That(() => new CborReader(GoldenFile.Bytes(v.Hex)).ReadIonDecimalParts(),
            Throws.InstanceOf<IonDecodeException>(), $"malformed vector '{name}': {v.Notes}");
    }

    // ── the specific claims the golden file makes ──

    /// <summary>
    /// THE CANONICAL-FORM GUARD. <c>1.50m</c> and <c>1.5m</c> are the same number and must be the
    /// same bytes. C# is the only one of the three runtimes whose decimal type remembers a
    /// trailing-zero scale, so it is the only one that could ever emit the un-normalised form.
    /// </summary>
    [Test]
    public void TrailingZerosAreNormalisedAway()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EncodeDecimal(1.5m), Is.EqualTo(EncodeDecimal(1.50m)));
            Assert.That(EncodeDecimal(1.5m), Is.EqualTo(EncodeDecimal(1.500000m)));
            Assert.That(EncodeDecimal(1.50m), Is.EqualTo("c482200f"));

            // …and the scale really is different before encoding, so the guard is load-bearing.
            Assert.That(decimal.GetBits(1.50m)[3] >> 16 & 0xFF, Is.EqualTo(2));
            Assert.That(decimal.GetBits(1.5m)[3] >> 16 & 0xFF, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// <c>-0.0m</c> is a real, distinct <see cref="decimal"/> bit pattern, and it normalises to the
    /// same four bytes as <c>0m</c> because CBOR has no negative zero integer. Documented, not
    /// accidental — this is the one place the decimal encoding is deliberately not injective.
    /// </summary>
    [Test]
    public void NegativeZeroCollapsesIntoZero()
    {
        Assert.Multiple(() =>
        {
            Assert.That(decimal.GetBits(-0.0m)[3], Is.Not.EqualTo(decimal.GetBits(0.0m)[3]),
                "the two really are different decimals");
            Assert.That(EncodeDecimal(-0.0m), Is.EqualTo("c4820000"));
            Assert.That(EncodeDecimal(0m), Is.EqualTo("c4820000"));
            Assert.That(EncodeDecimal(0.000m), Is.EqualTo("c4820000"));
        });
    }

    /// <summary>
    /// The mantissa is a plain CBOR integer across the whole i64/u64 window and a bignum only
    /// beyond it. <see cref="CborWriter.WriteBigInteger"/> is *always* tagged, so writing every
    /// mantissa through it would make small values non-canonical.
    /// </summary>
    [Test]
    public void MantissaUsesPlainIntegersUntilItCannot()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EncodeParts(0, long.MinValue), Does.StartWith("c48200" + "3b"), "long.MinValue: plain negative int");
            Assert.That(EncodeParts(0, ulong.MaxValue), Does.StartWith("c48200" + "1b"), "ulong.MaxValue: plain unsigned int");
            Assert.That(EncodeParts(0, (BigInteger)ulong.MaxValue + 1), Does.StartWith("c48200" + "c2"), "one past: tag 2 bignum");
            Assert.That(EncodeParts(0, (BigInteger)long.MinValue - 1), Does.StartWith("c48200" + "c3"), "one past: tag 3 bignum");
        });
    }

    /// <summary>An exact decimal is not a double: the classic 0.1 + 0.2 case must be exact.</summary>
    [Test]
    public void DecimalIsExactWhereDoubleIsNot()
    {
        Assert.Multiple(() =>
        {
            Assert.That(0.1d + 0.2d, Is.Not.EqualTo(0.3d));
            Assert.That(RoundTrip(0.1m) + RoundTrip(0.2m), Is.EqualTo(0.3m));
            Assert.That(RoundTrip(3.1415926535897932384626433832m), Is.EqualTo(3.1415926535897932384626433832m));
            Assert.That(RoundTrip(decimal.MaxValue), Is.EqualTo(decimal.MaxValue));
            Assert.That(RoundTrip(decimal.MinValue), Is.EqualTo(decimal.MinValue));
        });
    }

    /// <summary>A decimal written inside a container still counts as exactly one item.</summary>
    [Test]
    public void DecimalsNestInContainersCorrectly()
    {
        var w = new CborWriter();
        w.WriteStartArray(2);
        IonFormatterStorage<decimal>.Write(w, 1.5m);
        w.WriteInt32(7);
        w.WriteEndArray();

        Assert.That(GoldenFile.Hex(w), Is.EqualTo("82c482200f07"));
    }

    // ── helpers ──

    private static string EncodeDecimal(decimal value)
    {
        var w = new CborWriter();
        IonFormatterStorage<decimal>.Write(w, value);
        return GoldenFile.Hex(w);
    }

    private static string EncodeParts(int exponent, BigInteger mantissa)
    {
        var w = new CborWriter();
        w.WriteIonDecimalParts(exponent, mantissa);
        return GoldenFile.Hex(w);
    }

    private static decimal RoundTrip(decimal value)
    {
        var w = new CborWriter();
        IonFormatterStorage<decimal>.Write(w, value);
        return IonFormatterStorage<decimal>.Read(new CborReader(w.Encode()));
    }

    private static string Reencode(string hex)
        => EncodeDecimal(IonFormatterStorage<decimal>.Read(new CborReader(GoldenFile.Bytes(hex))));

    private static readonly Lazy<(Vector[] V, DecodeOnly[] D, Malformed[] M)> loaded = new(Load);

    internal static Vector[] All => loaded.Value.V;

    internal static Vector Get(string name)
        => All.FirstOrDefault(v => v.Name == name)
           ?? throw new InvalidOperationException($"decimal vector '{name}' not found");

    private static DecodeOnly GetDecodeOnly(string name)
        => loaded.Value.D.First(v => v.Name == name);

    private static Malformed GetMalformed(string name)
        => loaded.Value.M.First(v => v.Name == name);

    private static (Vector[], DecodeOnly[], Malformed[]) Load()
    {
        var root = GoldenFile.Load("decimal.golden.json");

        var vectors = root.GetProperty("vectors").EnumerateArray().Select(v => new Vector(
            v.GetProperty("name").GetString()!,
            v.GetProperty("exponent").GetInt32(),
            BigInteger.Parse(v.GetProperty("mantissa").GetString()!),
            v.GetProperty("canonicalExponent").GetInt32(),
            BigInteger.Parse(v.GetProperty("canonicalMantissa").GetString()!),
            v.Str("value"),
            v.GetProperty("inCSharpDecimalRange").GetBoolean(),
            v.GetProperty("hex").GetString()!,
            v.Str("notes"))).ToArray();

        var decodeOnly = root.GetProperty("decodeOnly").EnumerateArray().Select(v => new DecodeOnly(
            v.GetProperty("name").GetString()!,
            v.GetProperty("hex").GetString()!,
            v.GetProperty("reencodedHex").GetString()!,
            v.Str("notes"))).ToArray();

        var malformed = root.GetProperty("malformed").EnumerateArray().Select(v => new Malformed(
            v.GetProperty("name").GetString()!,
            v.GetProperty("hex").GetString()!,
            v.Str("notes"))).ToArray();

        return (vectors, decodeOnly, malformed);
    }

    private static IEnumerable<string> VectorNames() => All.Select(v => v.Name);

    private static IEnumerable<string> InRangeVectorNames() => All.Where(v => v.InRange).Select(v => v.Name);

    private static IEnumerable<string> OutOfRangeVectorNames() => All.Where(v => !v.InRange).Select(v => v.Name);

    private static IEnumerable<string> DecodeOnlyNames() => loaded.Value.D.Select(v => v.Name);

    private static IEnumerable<string> MalformedNames() => loaded.Value.M.Select(v => v.Name);
}
