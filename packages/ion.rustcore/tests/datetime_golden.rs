//! Cross-runtime golden vectors for Ion's `datetime` primitive.
//!
//! `/tests/golden/datetime.golden.json` is also consumed by
//!   `src/tests/IonTestClientServer/DateTimeGoldenTests.cs`   (C#)
//!   `packages/ion.webcore.js/test/datetime.golden.test.ts`   (TypeScript)
//!
//! **THIS IS A WIRE-FORMAT CHANGE FOR RUST, and it is a correction.** This runtime used to write a
//! bare CBOR array `[i64 .NET-ticks, i32 offset_minutes]`: not tag 0, not text, not even the same
//! major type as the other two runtimes. A Rust client and a C# server could not exchange a
//! `datetime` *at all*. The old encoding has no compatibility vectors here because there is
//! nothing to stay compatible with.

mod common;

use chrono::{DateTime, FixedOffset};
use common::*;
use ion_rustcore::std_formatters::{format_ion_datetime, parse_ion_datetime};
use ion_rustcore::IonError;

struct Vector {
    name: String,
    iso: String,
    unix_ticks: i64,
    offset_minutes: i32,
    hex: String,
    notes: String,
}

struct DecodeOnly {
    name: String,
    hex: String,
    reencoded_hex: String,
    notes: String,
}

struct Malformed {
    name: String,
    hex: String,
    notes: String,
}

fn vectors() -> Vec<Vector> {
    golden("datetime.golden.json")["vectors"]
        .as_array()
        .unwrap()
        .iter()
        .map(|v| Vector {
            name: req(v, "name"),
            iso: req(v, "iso"),
            unix_ticks: req(v, "unixTicks").parse().unwrap(),
            offset_minutes: v["offsetMinutes"].as_i64().unwrap() as i32,
            hex: req(v, "hex"),
            notes: s(v, "notes"),
        })
        .collect()
}

fn decode_only() -> Vec<DecodeOnly> {
    golden("datetime.golden.json")["decodeOnly"]
        .as_array()
        .unwrap()
        .iter()
        .map(|v| DecodeOnly {
            name: req(v, "name"),
            hex: req(v, "hex"),
            reencoded_hex: req(v, "reencodedHex"),
            notes: s(v, "notes"),
        })
        .collect()
}

fn malformed() -> Vec<Malformed> {
    golden("datetime.golden.json")["malformed"]
        .as_array()
        .unwrap()
        .iter()
        .map(|v| Malformed { name: req(v, "name"), hex: req(v, "hex"), notes: s(v, "notes") })
        .collect()
}

/// Builds the value from `unixTicks` + `offsetMinutes` — the same two numbers the C# and
/// TypeScript harnesses start from, so all three construct the instant independently rather than
/// by re-parsing the expected text.
pub fn build(unix_ticks: i64, offset_minutes: i32) -> DateTime<FixedOffset> {
    let offset = FixedOffset::east_opt(offset_minutes * 60).expect("offset out of range");
    let secs = unix_ticks.div_euclid(10_000_000);
    let ticks = unix_ticks.rem_euclid(10_000_000) as u32;
    chrono::DateTime::from_timestamp(secs, ticks * 100)
        .expect("instant out of range")
        .with_timezone(&offset)
}

fn reencode(hex: &str) -> String {
    enc(&dec::<DateTime<FixedOffset>>(hex))
}

fn try_decode(hex: &str) -> Result<DateTime<FixedOffset>, IonError> {
    let bytes = from_hex(hex);
    let mut d = minicbor::Decoder::new(&bytes);
    <DateTime<FixedOffset> as ion_rustcore::formatter::IonFormat>::ion_read(&mut d)
}

// ── tests ───────────────────────────────────────────────────────────────────

#[test]
fn golden_encode() {
    for v in vectors() {
        assert_eq!(
            enc(&build(v.unix_ticks, v.offset_minutes)),
            v.hex,
            "golden datetime vector '{}' ({}): {}",
            v.name,
            v.iso,
            v.notes
        );
    }
}

#[test]
fn golden_decode() {
    for v in vectors() {
        assert_eq!(
            reencode(&v.hex),
            v.hex,
            "golden datetime vector '{}' ({}): {}",
            v.name,
            v.iso,
            v.notes
        );
    }
}

/// The offset is part of the value, not decoration.
#[test]
fn golden_offset_and_instant_both_survive() {
    for v in vectors() {
        let decoded = dec::<DateTime<FixedOffset>>(&v.hex);

        assert_eq!(
            decoded.offset().local_minus_utc() / 60,
            v.offset_minutes,
            "vector '{}': offset must survive",
            v.name
        );
        assert_eq!(
            format_ion_datetime(&decoded),
            v.iso,
            "vector '{}': the local reading must survive",
            v.name
        );
        assert_eq!(
            decoded.timestamp() * 10_000_000 + (decoded.timestamp_subsec_nanos() as i64) / 100,
            v.unix_ticks,
            "vector '{}': the instant must survive to the tick",
            v.name
        );
    }
}

/// Every canonical datetime is the same 36 wire bytes: `c0 78 21` + 33 ASCII.
#[test]
fn golden_is_always_thirty_six_bytes() {
    for v in vectors() {
        assert_eq!(v.iso.len(), 33, "vector '{}'", v.name);
        assert_eq!(v.hex.len(), 36 * 2, "vector '{}'", v.name);
        assert!(v.hex.starts_with("c07821"), "vector '{}': {}", v.name, v.hex);
        assert_eq!(enc(&build(v.unix_ticks, v.offset_minutes)).len(), 36 * 2, "vector '{}'", v.name);
    }
}

#[test]
fn golden_decode_only_is_lenient() {
    for v in decode_only() {
        assert_eq!(
            reencode(&v.hex),
            v.reencoded_hex,
            "decode-only vector '{}': {}",
            v.name,
            v.notes
        );
    }
}

/// A malformed payload must never surface as an opaque error — every one of these is a specific
/// [`IonError`] variant a caller can match on.
#[test]
fn golden_malformed_raises_typed_errors() {
    for v in malformed() {
        let err = try_decode(&v.hex)
            .err()
            .unwrap_or_else(|| panic!("malformed vector '{}' decoded successfully: {}", v.name, v.notes));

        let matched = match v.name.as_str() {
            "wrong-tag" => matches!(err, IonError::UnexpectedTag { .. }),
            // A bare text string is exactly how Ion's `string` encodes, so tag 0 is required.
            "missing-tag" => matches!(err, IonError::MalformedValue { .. }),
            "not-a-date" | "missing-offset" => matches!(err, IonError::DateTimeFormat { .. }),
            other => panic!("unhandled malformed vector '{other}'"),
        };
        assert!(matched, "malformed vector '{}' produced {err:?}: {}", v.name, v.notes);
    }
}

#[test]
fn parse_round_trips_every_vector() {
    for v in vectors() {
        let parsed = parse_ion_datetime(&v.iso)
            .unwrap_or_else(|e| panic!("vector '{}' failed to parse: {e}", v.name));
        assert_eq!(format_ion_datetime(&parsed), v.iso, "vector '{}'", v.name);
    }
}

/// Fractional digits past the seventh are truncated, never rounded: rounding `.99999995` would
/// carry into the next second and no two runtimes would agree on the carry at every boundary.
#[test]
fn excess_fractional_digits_are_truncated_not_rounded() {
    let v = parse_ion_datetime("2024-03-01T12:34:56.99999999+00:00").unwrap();
    assert_eq!(format_ion_datetime(&v), "2024-03-01T12:34:56.9999999+00:00");

    let v = parse_ion_datetime("2024-03-01T12:34:56.789123456+00:00").unwrap();
    assert_eq!(format_ion_datetime(&v), "2024-03-01T12:34:56.7891234+00:00");
}

/// `Z` and `+00:00` are the same instant but different bytes, so exactly one has to be canonical.
#[test]
fn writer_never_emits_z() {
    for v in vectors() {
        assert!(!v.iso.ends_with('Z'), "vector '{}'", v.name);
        let formatted = format_ion_datetime(&build(v.unix_ticks, v.offset_minutes));
        assert!(
            formatted.ends_with("+00:00")
                || formatted[formatted.len() - 6..].starts_with('+')
                || formatted[formatted.len() - 6..].starts_with('-'),
            "vector '{}': {formatted}",
            v.name
        );
    }
}

#[test]
fn reader_rejects_an_offset_beyond_fourteen_hours() {
    assert!(parse_ion_datetime("2024-01-01T00:00:00.0000000+15:00").is_err());
    assert!(parse_ion_datetime("2024-01-01T00:00:00.0000000-15:00").is_err());
    assert!(parse_ion_datetime("2024-01-01T00:00:00.0000000+14:00").is_ok());
}

#[test]
fn reader_accepts_lowercase_separators() {
    let a = parse_ion_datetime("2024-03-01t12:34:56.7891234z").unwrap();
    let b = parse_ion_datetime("2024-03-01 12:34:56.7891234Z").unwrap();
    let c = parse_ion_datetime("2024-03-01T12:34:56.7891234+00:00").unwrap();
    assert_eq!(a, c);
    assert_eq!(b, c);
}

#[test]
fn reader_rejects_garbage_without_panicking() {
    for bad in ["", "hello", "2024-03-01", "2024-13-01T00:00:00Z", "2024-02-30T00:00:00Z", "\u{4e2d}"] {
        assert!(parse_ion_datetime(bad).is_err(), "'{bad}' should not parse");
    }
}

/// A datetime written inside a container still counts as exactly one item.
#[test]
fn datetimes_nest_in_containers_correctly() {
    let epoch_hex = vectors().into_iter().find(|v| v.name == "epoch-utc").unwrap().hex;

    let mut e = minicbor::Encoder::new(Vec::new());
    e.array(2).unwrap();
    ion_rustcore::formatter::IonFormat::ion_write(&build(0, 0), &mut e).unwrap();
    e.i32(7).unwrap();

    assert_eq!(to_hex(&e.into_writer()), format!("82{epoch_hex}07"));
}
