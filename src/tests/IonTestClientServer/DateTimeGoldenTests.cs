namespace IonTestClientServer;

using ion.runtime;
using System.Formats.Cbor;
using System.Text.Json;

/// <summary>
/// Cross-runtime golden vectors for Ion's <c>datetime</c> primitive.
/// <para>
/// <c>/tests/golden/datetime.golden.json</c> is also consumed by
/// <c>packages/ion.webcore.js/test/datetime.golden.test.ts</c> and
/// <c>packages/ion.rustcore/tests/datetime_golden.rs</c>. All three runtimes must produce
/// byte-identical CBOR for the same instant and offset — which, before this change, none of them
/// did: C# discarded the offset on read, Rust wrote a completely different shape, and TypeScript
/// truncated to milliseconds.
/// </para>
/// </summary>
public class DateTimeGoldenTests
{
    public sealed record Vector(
        string Name, string Iso, long UnixTicks, int OffsetMinutes, string Hex, string Notes);

    public sealed record DecodeOnly(string Name, string Hex, string ReencodedHex, string Notes);

    public sealed record Malformed(string Name, string Hex, string Notes);

    // ── encode ──

    [Test]
    [TestCaseSource(nameof(VectorNames))]
    public void Golden_Encode(string name)
    {
        var v = Get(name);
        Assert.That(Encode(Build(v)), Is.EqualTo(v.Hex),
            $"golden datetime vector '{name}' ({v.Iso}): {v.Notes}");
    }

    // ── decode ──

    [Test]
    [TestCaseSource(nameof(VectorNames))]
    public void Golden_Decode(string name)
    {
        var v = Get(name);
        Assert.That(Reencode(v.Hex), Is.EqualTo(v.Hex),
            $"golden datetime vector '{name}' ({v.Iso}): {v.Notes}");
    }

    /// <summary>
    /// The offset is part of the value, not decoration. This is the R-DATETIME regression guard:
    /// the old reader was <c>ReadDateTimeOffset().UtcDateTime</c>, so every non-UTC vector came
    /// back as UTC and re-encoded with <c>+00:00</c>.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(VectorNames))]
    public void Golden_OffsetSurvivesTheRoundTrip(string name)
    {
        var v = Get(name);
        var decoded = IonFormatterStorage<DateTimeOffset>.Read(new CborReader(GoldenFile.Bytes(v.Hex)));

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Offset, Is.EqualTo(TimeSpan.FromMinutes(v.OffsetMinutes)),
                $"'{name}': the offset must survive, not be folded into UTC");
            Assert.That(decoded.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks, Is.EqualTo(v.UnixTicks),
                $"'{name}': the instant must survive to the tick");
        });
    }

    /// <summary>Every canonical datetime is the same 36 wire bytes: <c>c0 78 21</c> + 33 ASCII.</summary>
    [Test]
    [TestCaseSource(nameof(VectorNames))]
    public void Golden_IsAlwaysThirtySixBytes(string name)
    {
        var v = Get(name);
        Assert.Multiple(() =>
        {
            Assert.That(v.Iso, Has.Length.EqualTo(IonDateTimeWire.CanonicalLength));
            Assert.That(v.Hex, Has.Length.EqualTo(36 * 2), $"'{name}' must occupy 36 wire bytes");
            Assert.That(v.Hex, Does.StartWith("c07821"), $"'{name}': tag 0 + a 33-byte text string");
        });
    }

    // ── reader leniency ──

    [Test]
    [TestCaseSource(nameof(DecodeOnlyNames))]
    public void Golden_DecodeOnly(string name)
    {
        var v = GetDecodeOnly(name);
        Assert.That(Reencode(v.Hex), Is.EqualTo(v.ReencodedHex), $"decode-only vector '{name}': {v.Notes}");
    }

    // ── malformed payloads produce TYPED errors ──

    /// <summary>
    /// A malformed payload must never surface as an opaque exception. Each of these must be an
    /// <see cref="IonDecodeException"/>, so a caller can branch on it without string-matching.
    /// </summary>
    [Test]
    [TestCaseSource(nameof(MalformedNames))]
    public void Golden_MalformedRaisesTypedError(string name)
    {
        var v = GetMalformed(name);
        Assert.That(() => Reencode(v.Hex), Throws.InstanceOf<IonDecodeException>(),
            $"malformed vector '{name}': {v.Notes}");
    }

    [Test]
    public void MalformedErrorsAreSpecificallyTyped()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => Reencode(GetMalformed("wrong-tag").Hex),
                Throws.InstanceOf<IonUnexpectedTagException>());
            Assert.That(() => Reencode(GetMalformed("missing-tag").Hex),
                Throws.InstanceOf<IonMalformedValueException>());
            Assert.That(() => Reencode(GetMalformed("not-a-date").Hex),
                Throws.InstanceOf<IonDateTimeFormatException>());
            Assert.That(() => Reencode(GetMalformed("missing-offset").Hex),
                Throws.InstanceOf<IonDateTimeFormatException>()
                    .With.Property(nameof(IonDateTimeFormatException.Reason)).Contains("offset"));
        });
    }

    // ── the specific claims the golden file makes about the writer ──

    /// <summary>
    /// Writers never emit <c>Z</c>. It is a legal RFC 3339 offset and readers accept it, but
    /// <c>Z</c> and <c>+00:00</c> are different bytes for the same instant, so exactly one of them
    /// has to be canonical.
    /// </summary>
    [Test]
    public void WriterNeverEmitsZ()
    {
        foreach (var v in All)
        {
            Assert.That(v.Iso, Does.Not.EndWith("Z"), $"vector '{v.Name}'");
            Assert.That(IonDateTimeWire.Format(Build(v)), Does.Match(@"[+-]\d{2}:\d{2}$"),
                $"vector '{v.Name}'");
        }
    }

    /// <summary>Fractional digits past the seventh are truncated, never rounded.</summary>
    [Test]
    public void ExcessFractionalDigitsAreTruncatedNotRounded()
    {
        Assert.Multiple(() =>
        {
            // .9999999|9 would round up into the next second; it must not.
            Assert.That(IonDateTimeWire.Parse("2024-03-01T12:34:56.99999999+00:00").Ticks % TimeSpan.TicksPerSecond,
                Is.EqualTo(9_999_999));
            Assert.That(IonDateTimeWire.Parse("2024-03-01T12:34:56.99999999+00:00").Second, Is.EqualTo(56));
            Assert.That(IonDateTimeWire.Parse("2024-03-01T12:34:56.789123456+00:00").Ticks % TimeSpan.TicksPerSecond,
                Is.EqualTo(7_891_234));
        });
    }

    /// <summary>
    /// <see cref="DateTime"/> still round-trips through the legacy formatter, but LOSES the
    /// offset — which is exactly why <c>datetime</c> must map to <see cref="DateTimeOffset"/>.
    /// This test pins the lossy behaviour rather than pretending it is fine.
    /// </summary>
    [Test]
    public void LegacyDateTimeFormatterIsOffsetLossy()
    {
        var v = Get("non-utc-negative-offset"); // 2023-12-31T19:00:00.0000000-05:00
        var asDateTime = IonFormatterStorage<DateTime>.Read(new CborReader(GoldenFile.Bytes(v.Hex)));

        Assert.Multiple(() =>
        {
            Assert.That(asDateTime.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(asDateTime, Is.EqualTo(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                "the instant is right; the authored offset is gone");

            var w = new CborWriter();
            IonFormatterStorage<DateTime>.Write(w, asDateTime);
            Assert.That(GoldenFile.Hex(w), Is.Not.EqualTo(v.Hex),
                "re-encoding a DateTime cannot reproduce the original bytes — the asymmetry DateTimeOffset fixes");
        });
    }

    /// <summary>
    /// The legacy <see cref="DateTime"/> writer must not depend on the host's time zone: a Local
    /// value is converted to UTC rather than stamped with the machine's offset, so the same value
    /// produces the same bytes everywhere.
    /// </summary>
    [Test]
    public void LegacyDateTimeWriterIsMachineIndependent()
    {
        var utc = new DateTime(2024, 3, 1, 12, 34, 56, DateTimeKind.Utc);

        var fromUtc = new CborWriter();
        IonFormatterStorage<DateTime>.Write(fromUtc, utc);

        var fromLocal = new CborWriter();
        IonFormatterStorage<DateTime>.Write(fromLocal, utc.ToLocalTime());

        var fromUnspecified = new CborWriter();
        IonFormatterStorage<DateTime>.Write(fromUnspecified, DateTime.SpecifyKind(utc, DateTimeKind.Unspecified));

        Assert.Multiple(() =>
        {
            Assert.That(GoldenFile.Hex(fromLocal), Is.EqualTo(GoldenFile.Hex(fromUtc)),
                "a Local DateTime must normalise to UTC, not adopt the host's offset");
            Assert.That(GoldenFile.Hex(fromUnspecified), Is.EqualTo(GoldenFile.Hex(fromUtc)),
                "an Unspecified DateTime is treated as UTC");
        });
    }

    /// <summary>A datetime written inside a container still counts as exactly one item.</summary>
    [Test]
    public void DateTimesNestInContainersCorrectly()
    {
        var epoch = Build(Get("epoch-utc"));
        var w = new CborWriter();
        w.WriteStartArray(2);
        IonFormatterStorage<DateTimeOffset>.Write(w, epoch);
        w.WriteInt32(7);
        w.WriteEndArray();

        Assert.That(GoldenFile.Hex(w), Is.EqualTo("82" + Get("epoch-utc").Hex + "07"));
    }

    // ── helpers ──

    /// <summary>
    /// Builds the vector's value from <c>unixTicks</c> + <c>offsetMinutes</c> — the same two
    /// numbers the TypeScript and Rust harnesses start from, so all three build the same instant
    /// independently rather than by re-parsing the expected text.
    /// </summary>
    internal static DateTimeOffset Build(Vector v)
        => new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks + v.UnixTicks, TimeSpan.Zero)
            .ToOffset(TimeSpan.FromMinutes(v.OffsetMinutes));

    private static string Encode(DateTimeOffset value)
    {
        var w = new CborWriter();
        IonFormatterStorage<DateTimeOffset>.Write(w, value);
        return GoldenFile.Hex(w);
    }

    private static string Reencode(string hex)
        => Encode(IonFormatterStorage<DateTimeOffset>.Read(new CborReader(GoldenFile.Bytes(hex))));

    private static readonly Lazy<(Vector[] V, DecodeOnly[] D, Malformed[] M)> loaded = new(Load);

    internal static Vector[] All => loaded.Value.V;

    internal static Vector Get(string name)
        => All.FirstOrDefault(v => v.Name == name)
           ?? throw new InvalidOperationException($"datetime vector '{name}' not found");

    private static DecodeOnly GetDecodeOnly(string name)
        => loaded.Value.D.FirstOrDefault(v => v.Name == name)
           ?? throw new InvalidOperationException($"datetime decode-only vector '{name}' not found");

    private static Malformed GetMalformed(string name)
        => loaded.Value.M.FirstOrDefault(v => v.Name == name)
           ?? throw new InvalidOperationException($"datetime malformed vector '{name}' not found");

    private static (Vector[], DecodeOnly[], Malformed[]) Load()
    {
        var root = GoldenFile.Load("datetime.golden.json");

        var vectors = root.GetProperty("vectors").EnumerateArray().Select(v => new Vector(
            v.GetProperty("name").GetString()!,
            v.GetProperty("iso").GetString()!,
            long.Parse(v.GetProperty("unixTicks").GetString()!),
            v.GetProperty("offsetMinutes").GetInt32(),
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

    private static IEnumerable<string> DecodeOnlyNames() => loaded.Value.D.Select(v => v.Name);

    private static IEnumerable<string> MalformedNames() => loaded.Value.M.Select(v => v.Name);
}
