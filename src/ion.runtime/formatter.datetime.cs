namespace ion.runtime;

using System.Formats.Cbor;
using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// Wire encoding for Ion's <c>datetime</c> primitive.
/// <para>
/// <b>Rule: CBOR tag 0 wrapping an RFC 3339 date-time, always with an explicit numeric offset and
/// always with exactly seven fractional-second digits.</b> The canonical text is
/// <c>YYYY-MM-DDTHH:MM:SS.fffffff±HH:MM</c> — 33 ASCII characters, so a datetime is always
/// 36 wire bytes: <c>c0 78 21 …</c>.
/// </para>
/// <para>
/// <b>THIS IS A WIRE-FORMAT CHANGE, and it is a correction.</b> Before this, the three runtimes
/// wrote three different shapes and no two of them fully interoperated:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>C#</b> wrote tag 0 + RFC 3339 text, but the reader was
///     <c>reader.ReadDateTimeOffset().UtcDateTime</c> — it parsed the offset and then threw it
///     away. A value authored at <c>+05:30</c> came back as UTC.
///   </description></item>
///   <item><description>
///     <b>Rust</b> wrote a bare CBOR array <c>[i64 .NET-ticks, i32 offset_minutes]</c>: not tag 0,
///     not text, not even the same major type. A Rust client and a C# server could not exchange a
///     <c>datetime</c> <i>at all</i>.
///   </description></item>
///   <item><description>
///     <b>TypeScript</b> wrote tag 0 + <c>Date.toISOString()</c>, which is millisecond-resolution,
///     so 100ns ticks authored in C# were silently truncated on every round trip.
///   </description></item>
/// </list>
/// <para>
/// The format has no users and one of its three runtimes was already unable to interoperate, so
/// breaking it is strictly cheaper than preserving it. Golden vectors:
/// <c>/tests/golden/datetime.golden.json</c>.
/// </para>
/// <para>
/// <b>Seven digits, not fewer and not more.</b> 100ns is the resolution of the most precise
/// runtime type in play — the .NET tick. Fewer digits would lose C# precision on the wire; more
/// would be unrepresentable in C# and so could never be written back byte-identically.
/// </para>
/// <para>
/// <b>Numeric offset, never <c>Z</c>.</b> <c>Z</c> and <c>+00:00</c> denote the same instant but
/// are different bytes, so exactly one of them has to be canonical. Writers pick <c>+00:00</c>
/// because it removes the special case entirely — every offset is formatted the same way.
/// </para>
/// <para>
/// <b>Readers are lenient.</b> Any fractional precision from 0 to 9 digits is accepted (digits
/// past the seventh are <i>truncated</i>, never rounded — rounding <c>.99999995</c> would carry
/// into the next second and is not reproducible in a fixed-width integer runtime), <c>Z</c> is
/// accepted alongside a numeric offset, the <c>T</c> separator may be lower case or a space, and
/// an untagged text string is accepted because the declared field type already says
/// <c>datetime</c>. A different tag, a missing offset and unparseable text are rejected with
/// <see cref="IonDateTimeFormatException"/> — never an opaque throw.
/// </para>
/// </summary>
public static partial class IonDateTimeWire
{
    /// <summary>CBOR tag 0: "standard date/time string" (RFC 8949 §3.4.1).</summary>
    public const ulong DateTimeStringTag = 0;

    /// <summary>
    /// The canonical .NET format string. Every literal is quoted so the pattern cannot be
    /// reinterpreted by a culture: <c>T</c>, <c>-</c>, <c>:</c> and <c>.</c> are text, <c>zzz</c>
    /// renders the signed offset as <c>±HH:MM</c>, and <c>fffffff</c> renders all seven tick
    /// digits including trailing zeros.
    /// </summary>
    public const string CanonicalFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffffzzz";

    /// <summary>Length of the canonical text: <c>2024-03-01T12:34:56.7891234+05:30</c>.</summary>
    public const int CanonicalLength = 33;

    /// <summary>
    /// RFC 3339 with the leniencies documented on the class. Group 7 is the optional fraction of
    /// any length; group 8 is the mandatory offset.
    /// </summary>
    [GeneratedRegex(
        @"^(\d{4})-(\d{2})-(\d{2})[Tt ](\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(?:([Zz])|([+-])(\d{2}):(\d{2}))$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339();

    /// <summary>Renders <paramref name="value"/> as the canonical 33-character RFC 3339 text.</summary>
    public static string Format(DateTimeOffset value)
        => value.ToString(CanonicalFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses an RFC 3339 date-time with the leniencies documented on the class.
    /// </summary>
    /// <exception cref="IonDateTimeFormatException">
    /// The text is not RFC 3339, carries no offset, or names an instant outside
    /// <see cref="DateTimeOffset"/>'s range.
    /// </exception>
    public static DateTimeOffset Parse(string text)
    {
        var m = Rfc3339().Match(text);
        if (!m.Success)
        {
            // Distinguish the single most likely authoring mistake from general garbage, because
            // "you forgot the offset" and "this is not a date" call for different fixes.
            var reason = Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}:\d{2}(\.\d+)?$")
                ? "RFC 3339 requires an explicit offset; a local time without one is ambiguous and is not assumed to be UTC"
                : "not an RFC 3339 date-time";
            throw new IonDateTimeFormatException(text, reason);
        }

        var offset = m.Groups[8].Success
            ? TimeSpan.Zero
            : new TimeSpan(
                  hours: int.Parse(m.Groups[10].ValueSpan, CultureInfo.InvariantCulture),
                  minutes: int.Parse(m.Groups[11].ValueSpan, CultureInfo.InvariantCulture),
                  seconds: 0)
              * (m.Groups[9].ValueSpan[0] == '-' ? -1 : 1);

        // Fractional digits past the seventh are TRUNCATED. Rounding could carry into the next
        // second, and no two runtimes would agree on the carry at every boundary.
        var fraction = m.Groups[7].Success ? m.Groups[7].Value : "";
        var ticks = fraction.Length == 0
            ? 0
            : int.Parse(fraction.Length >= 7 ? fraction[..7] : fraction.PadRight(7, '0'),
                CultureInfo.InvariantCulture);

        try
        {
            return new DateTimeOffset(
                int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[4].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[5].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[6].ValueSpan, CultureInfo.InvariantCulture),
                offset).AddTicks(ticks);
        }
        catch (ArgumentException e)
        {
            throw new IonDateTimeFormatException(text, e.Message, e);
        }
    }

    /// <summary>Writes <paramref name="value"/> as tag 0 + the canonical RFC 3339 text.</summary>
    public static void WriteIonDateTime(this CborWriter writer, DateTimeOffset value)
    {
        writer.WriteTag((CborTag)DateTimeStringTag);
        writer.WriteTextString(Format(value));
    }

    /// <summary>
    /// Reads a <c>datetime</c>: tag 0 wrapping an RFC 3339 text string.
    /// </summary>
    /// <remarks>
    /// The tag is <b>required</b>. Every other leniency here is free because the lenient form
    /// cannot be mistaken for another Ion type's encoding, but a bare text string is exactly how
    /// Ion's <c>string</c> encodes — accepting one would make <c>datetime</c> and <c>string</c>
    /// indistinguishable in a capture. The same rule makes tag 4 mandatory for <c>decimal</c> and
    /// tag 258 mandatory for <c>Set&lt;T&gt;</c>.
    /// </remarks>
    /// <exception cref="IonMalformedValueException">The tag was missing, or did not wrap text.</exception>
    /// <exception cref="IonUnexpectedTagException">A tag other than 0 was present.</exception>
    /// <exception cref="IonDateTimeFormatException">The text is not a usable RFC 3339 date-time.</exception>
    public static DateTimeOffset ReadIonDateTime(this CborReader reader)
    {
        if (reader.PeekState() != CborReaderState.Tag)
            throw new IonMalformedValueException("datetime",
                $"expected CBOR tag {DateTimeStringTag}, got {reader.PeekState()}; " +
                "an untagged text string is Ion `string`, not `datetime`");

        var tag = (ulong)reader.ReadTag();
        if (tag != DateTimeStringTag)
            throw new IonUnexpectedTagException(DateTimeStringTag, tag, "datetime");

        if (reader.PeekState() != CborReaderState.TextString)
            throw new IonMalformedValueException("datetime",
                $"expected a definite-length text string, got {reader.PeekState()}");

        return Parse(reader.ReadTextString());
    }
}

/// <summary>
/// <c>datetime</c> ⇒ <see cref="DateTimeOffset"/>. This is the formatter the Ion <c>datetime</c>
/// type maps to: the offset is part of the value and survives the round trip.
/// </summary>
public sealed class Ion_datetime_offset_Formatter : IonFormatter<DateTimeOffset>
{
    public DateTimeOffset Read(CborReader reader)
        => reader.ReadIonDateTime();

    public void Write(CborWriter writer, DateTimeOffset value)
        => writer.WriteIonDateTime(value);
}

/// <summary>
/// <see cref="DateTime"/> over the same wire format — <b>lossy, and retained only for
/// compatibility with code generated before <c>datetime</c> was remapped.</b>
/// <para>
/// <see cref="DateTime"/> has no offset field, so reading through this formatter converts the
/// incoming instant to UTC and discards the offset the peer sent; the value is still the correct
/// instant, but <c>2023-12-31T19:00:00-05:00</c> comes back as <c>2024-01-01T00:00:00Z</c> and
/// re-encodes as such. That round-trip asymmetry is exactly the defect
/// <see cref="Ion_datetime_offset_Formatter"/> exists to fix.
/// </para>
/// <para>
/// Writing is deliberately offset-free rather than machine-dependent: a
/// <see cref="DateTimeKind.Local"/> value is converted to UTC first, and
/// <see cref="DateTimeKind.Unspecified"/> is treated as UTC, so the same
/// <see cref="DateTime"/> produces the same bytes on every machine. Emitting the host's local
/// offset would make the encoding depend on the server's time zone and break byte-identity.
/// </para>
/// </summary>
public sealed class Ion_datetime_Formatter : IonFormatter<DateTime>
{
    public DateTime Read(CborReader reader)
        => reader.ReadIonDateTime().UtcDateTime;

    public void Write(CborWriter writer, DateTime value)
        => writer.WriteIonDateTime(new DateTimeOffset(
            value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
            TimeSpan.Zero));
}
