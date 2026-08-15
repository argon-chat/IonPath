use crate::formatter::IonFormat;
use crate::types::*;
use minicbor::{Decoder, Encoder};

// ═══════════════════════════════════════════════════════════════════
// bool
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for bool {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.bool()?)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.bool(*self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// String
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for String {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.str()?.to_owned())
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.str(self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonBytes (bytes)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for IonBytes {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        let data = d.bytes()?.to_vec();
        Ok(IonBytes::new(data))
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.bytes(self.as_slice())?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// uuid::Uuid (guid) — stored as 16-byte CBOR binary
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for uuid::Uuid {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        let bytes = d.bytes()?;
        if bytes.len() != 16 {
            return Err(IonError::Decode(format!(
                "Expected 16 bytes for UUID, got {}",
                bytes.len()
            )));
        }
        Ok(uuid::Uuid::from_bytes(bytes.try_into().unwrap()))
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.bytes(self.as_bytes())?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonDateOnly — CBOR array [year, month, day]
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for IonDateOnly {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        d.array()?;
        let year = d.i32()?;
        let month = d.u32()?;
        let day = d.u32()?;
        Ok(IonDateOnly { year, month, day })
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.array(3)?;
        e.i32(self.year)?;
        e.u32(self.month)?;
        e.u32(self.day)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonTimeOnly — CBOR array [hour, minute, second, millisecond, microsecond]
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for IonTimeOnly {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        d.array()?;
        let hour = d.u32()?;
        let minute = d.u32()?;
        let second = d.u32()?;
        let millisecond = d.u32()?;
        let microsecond = d.u32()?;
        Ok(IonTimeOnly { hour, minute, second, millisecond, microsecond })
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.array(5)?;
        e.u32(self.hour)?;
        e.u32(self.minute)?;
        e.u32(self.second)?;
        e.u32(self.millisecond)?;
        e.u32(self.microsecond)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonDuration — CBOR int (ticks as i64)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for IonDuration {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        let ticks = d.i64()?;
        Ok(IonDuration { ticks })
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.i64(self.ticks)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// chrono::DateTime<FixedOffset> (datetime) — CBOR tag 0 + RFC 3339 text
// ═══════════════════════════════════════════════════════════════════

/// CBOR tag 0: "standard date/time string" (RFC 8949 §3.4.1).
pub const DATETIME_TAG: u64 = 0;

/// The canonical wire text is always exactly this long: `2024-03-01T12:34:56.7891234+05:30`.
pub const DATETIME_CANONICAL_LEN: usize = 33;

/// Renders an instant as Ion's canonical `datetime` text:
/// `YYYY-MM-DDTHH:MM:SS.fffffff±HH:MM`, always seven fractional digits and always an explicit
/// numeric offset.
///
/// **Seven digits, not nine.** chrono carries nanoseconds, but Ion's `datetime` is specified at
/// 100ns — the resolution of .NET's `DateTimeOffset`, the most precise representation in the
/// protocol. Sub-tick nanoseconds are **truncated**, never rounded: rounding could carry into the
/// next second, and no two runtimes would agree on the carry at every boundary.
///
/// **`+00:00`, never `Z`.** They denote the same instant but are different bytes, so exactly one
/// has to be canonical; a numeric offset removes the special case entirely.
pub fn format_ion_datetime(value: &chrono::DateTime<chrono::FixedOffset>) -> String {
    use chrono::{Datelike, Timelike};

    let offset_minutes = value.offset().local_minus_utc() / 60;
    let sign = if offset_minutes < 0 { '-' } else { '+' };
    let abs = offset_minutes.abs();

    // `nanosecond()` exceeds 1e9 inside a leap second; clamp so the fraction stays 7 digits.
    let ticks = value.nanosecond().min(999_999_999) / 100;

    format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:07}{}{:02}:{:02}",
        value.year(),
        value.month(),
        value.day(),
        value.hour(),
        value.minute(),
        value.second(),
        ticks,
        sign,
        abs / 60,
        abs % 60
    )
}

fn datetime_err(text: &str, reason: impl Into<String>) -> IonError {
    IonError::DateTimeFormat { text: text.to_owned(), reason: reason.into() }
}

fn ascii_number<T: std::str::FromStr>(text: &str, field: &str, whole: &str) -> Result<T, IonError> {
    if text.is_empty() || !text.bytes().all(|b| b.is_ascii_digit()) {
        return Err(datetime_err(whole, format!("{field} is not a number")));
    }
    text.parse().map_err(|_| datetime_err(whole, format!("{field} is out of range")))
}

/// Parses an RFC 3339 date-time into a fixed-offset instant.
///
/// Hand-rolled rather than delegating to `chrono::DateTime::parse_from_rfc3339` so that the
/// leniencies are *identical* to the C# and TypeScript readers rather than whatever chrono
/// happens to allow: any fractional precision (truncating past the seventh digit), `Z` or `z` as
/// well as a numeric offset, and `T`, `t` or a space as the separator. A missing offset is
/// rejected — RFC 3339 requires one, and guessing UTC would move the instant by up to 14 hours.
pub fn parse_ion_datetime(text: &str) -> Result<chrono::DateTime<chrono::FixedOffset>, IonError> {
    use chrono::TimeZone;

    // Byte-index slicing below is only sound for ASCII, and a non-ASCII date is malformed anyway.
    if !text.is_ascii() {
        return Err(datetime_err(text, "not an RFC 3339 date-time (non-ASCII)"));
    }

    let b = text.as_bytes();
    if b.len() < 20 {
        return Err(datetime_err(text, "too short for an RFC 3339 date-time"));
    }
    if b[4] != b'-' || b[7] != b'-' || b[13] != b':' || b[16] != b':' {
        return Err(datetime_err(text, "not an RFC 3339 date-time"));
    }
    if !matches!(b[10], b'T' | b't' | b' ') {
        return Err(datetime_err(text, "expected 'T' between the date and the time"));
    }

    let year: i32 = ascii_number(&text[0..4], "year", text)?;
    let month: u32 = ascii_number(&text[5..7], "month", text)?;
    let day: u32 = ascii_number(&text[8..10], "day", text)?;
    let hour: u32 = ascii_number(&text[11..13], "hour", text)?;
    let minute: u32 = ascii_number(&text[14..16], "minute", text)?;
    let second: u32 = ascii_number(&text[17..19], "second", text)?;

    let mut i = 19;
    let mut nanos: u32 = 0;
    if b.get(i) == Some(&b'.') {
        i += 1;
        let start = i;
        while i < b.len() && b[i].is_ascii_digit() {
            i += 1;
        }
        if i == start {
            return Err(datetime_err(text, "'.' must be followed by fractional digits"));
        }

        // Truncate past the seventh digit; right-pad shorter fractions.
        let digits = &text[start..i];
        let ticks: u32 = if digits.len() >= 7 {
            ascii_number(&digits[..7], "fraction", text)?
        } else {
            ascii_number(&format!("{digits:0<7}"), "fraction", text)?
        };
        nanos = ticks * 100;
    }

    let rest = &text[i..];
    let offset_seconds: i32 = if rest == "Z" || rest == "z" {
        0
    } else {
        let rb = rest.as_bytes();
        if rb.len() != 6 || !matches!(rb[0], b'+' | b'-') || rb[3] != b':' {
            return Err(datetime_err(
                text,
                "RFC 3339 requires an explicit offset; a local time without one is ambiguous and is not assumed to be UTC",
            ));
        }
        let oh: i32 = ascii_number(&rest[1..3], "offset hours", text)?;
        let om: i32 = ascii_number(&rest[4..6], "offset minutes", text)?;
        if om > 59 {
            return Err(datetime_err(text, "offset minutes out of range"));
        }
        let seconds = oh * 3600 + om * 60;
        if rb[0] == b'-' { -seconds } else { seconds }
    };

    // ±14:00 is the limit .NET's DateTimeOffset accepts; chrono would take far more, so the check
    // is explicit here to keep all three runtimes rejecting the same payloads.
    if offset_seconds.abs() > 14 * 3600 {
        return Err(datetime_err(text, "offset is beyond ±14:00"));
    }

    let offset = chrono::FixedOffset::east_opt(offset_seconds)
        .ok_or_else(|| datetime_err(text, "invalid UTC offset"))?;

    let naive = chrono::NaiveDate::from_ymd_opt(year, month, day)
        .ok_or_else(|| datetime_err(text, "day out of range for that month"))?
        .and_hms_nano_opt(hour, minute, second, nanos)
        .ok_or_else(|| datetime_err(text, "time-of-day out of range"))?;

    offset
        .from_local_datetime(&naive)
        .single()
        .ok_or_else(|| datetime_err(text, "instant is not representable"))
}

/// `datetime` — CBOR tag 0 wrapping an RFC 3339 date-time.
///
/// **THIS IS A WIRE-FORMAT CHANGE FOR RUST, and it is a correction.** This runtime used to write
/// a bare CBOR array `[i64 .NET-ticks, i32 offset_minutes]`: not tag 0, not text, not even the
/// same major type as the other two runtimes. A Rust client and a C# server could not exchange a
/// `datetime` *at all*. C# wrote tag 0 + RFC 3339 but discarded the offset on read, and
/// TypeScript wrote tag 0 + `Date.toISOString()` at millisecond resolution. All three now write
/// the same 36 bytes. See `/tests/golden/datetime.golden.json`.
///
/// Tag 0 is **required** on read: a bare text string is exactly how Ion's `string` type encodes,
/// so accepting one would make `datetime` and `string` indistinguishable in a capture.
impl IonFormat for chrono::DateTime<chrono::FixedOffset> {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        if d.datatype()? != minicbor::data::Type::Tag {
            return Err(IonError::MalformedValue {
                ion_type: "datetime",
                reason: format!("expected CBOR tag {DATETIME_TAG}, got {}", d.datatype()?),
            });
        }

        let tag = d.tag()?.as_u64();
        if tag != DATETIME_TAG {
            return Err(IonError::UnexpectedTag {
                expected: DATETIME_TAG,
                actual: tag,
                ion_type: "datetime",
            });
        }

        let text = d.str().map_err(|e| IonError::MalformedValue {
            ion_type: "datetime",
            reason: format!("tag {DATETIME_TAG} must wrap a text string: {e}"),
        })?;

        parse_ion_datetime(text)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.tag(minicbor::data::Tag::new(DATETIME_TAG))?;
        e.str(&format_ion_datetime(self))?;
        Ok(())
    }
}
