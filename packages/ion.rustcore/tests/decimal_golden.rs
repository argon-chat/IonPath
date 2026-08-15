//! Cross-runtime golden vectors for Ion's `decimal` primitive (CBOR tag 4).
//!
//! `/tests/golden/decimal.golden.json` is also consumed by
//!   `src/tests/IonTestClientServer/DecimalGoldenTests.cs`   (C#)
//!   `packages/ion.webcore.js/test/decimal.golden.test.ts`   (TypeScript)
//!
//! [`IonDecimal`] is dependency-free — no `rust_decimal` — and stores the mantissa in an `i128`,
//! which spans every value C#'s `System.Decimal` can hold (its unscaled magnitude tops out at
//! 2^96 - 1) with three orders of magnitude to spare. So unlike C#, this runtime decodes the
//! `inCSharpDecimalRange: false` vectors exactly.

mod common;

use common::*;
use ion_rustcore::{IonDecimal, IonError};
use std::str::FromStr;

struct Vector {
    name: String,
    exponent: i32,
    mantissa: i128,
    canonical_exponent: i32,
    canonical_mantissa: i128,
    value: String,
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
    golden("decimal.golden.json")["vectors"]
        .as_array()
        .unwrap()
        .iter()
        .map(|v| Vector {
            name: req(v, "name"),
            exponent: v["exponent"].as_i64().unwrap() as i32,
            mantissa: req(v, "mantissa").parse().unwrap(),
            canonical_exponent: v["canonicalExponent"].as_i64().unwrap() as i32,
            canonical_mantissa: req(v, "canonicalMantissa").parse().unwrap(),
            value: s(v, "value"),
            hex: req(v, "hex"),
            notes: s(v, "notes"),
        })
        .collect()
}

fn decode_only() -> Vec<DecodeOnly> {
    golden("decimal.golden.json")["decodeOnly"]
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
    golden("decimal.golden.json")["malformed"]
        .as_array()
        .unwrap()
        .iter()
        .map(|v| Malformed { name: req(v, "name"), hex: req(v, "hex"), notes: s(v, "notes") })
        .collect()
}

fn try_decode(hex: &str) -> Result<IonDecimal, IonError> {
    let bytes = from_hex(hex);
    let mut d = minicbor::Decoder::new(&bytes);
    <IonDecimal as ion_rustcore::formatter::IonFormat>::ion_read(&mut d)
}

// ── tests ───────────────────────────────────────────────────────────────────

#[test]
fn golden_encode() {
    for v in vectors() {
        // Built from the AUTHORED exponent/mantissa; the formatter canonicalises.
        assert_eq!(
            enc(&IonDecimal::new(v.exponent, v.mantissa)),
            v.hex,
            "golden decimal vector '{}' ({}): {}",
            v.name,
            v.value,
            v.notes
        );
    }
}

#[test]
fn golden_decode() {
    for v in vectors() {
        assert_eq!(
            enc(&dec::<IonDecimal>(&v.hex)),
            v.hex,
            "golden decimal vector '{}' ({}): {}",
            v.name,
            v.value,
            v.notes
        );
    }
}

#[test]
fn golden_decode_to_canonical_parts() {
    for v in vectors() {
        let decoded = dec::<IonDecimal>(&v.hex);
        assert_eq!(decoded.exponent, v.canonical_exponent, "vector '{}' exponent", v.name);
        assert_eq!(decoded.mantissa, v.canonical_mantissa, "vector '{}' mantissa", v.name);
    }
}

#[test]
fn golden_decode_only_is_lenient() {
    for v in decode_only() {
        assert_eq!(
            enc(&dec::<IonDecimal>(&v.hex)),
            v.reencoded_hex,
            "decode-only vector '{}': {}",
            v.name,
            v.notes
        );
    }
}

#[test]
fn golden_malformed_raises_typed_errors() {
    for v in malformed() {
        let err = try_decode(&v.hex)
            .err()
            .unwrap_or_else(|| panic!("malformed vector '{}' decoded successfully: {}", v.name, v.notes));

        let matched = match v.name.as_str() {
            // Tag 5 is bigfloat — the same array shape with a base-2 exponent.
            "wrong-tag" => matches!(err, IonError::UnexpectedTag { .. }),
            "wrong-arity" | "not-an-array" => matches!(err, IonError::MalformedValue { .. }),
            other => panic!("unhandled malformed vector '{other}'"),
        };
        assert!(matched, "malformed vector '{}' produced {err:?}: {}", v.name, v.notes);
    }
}

/// THE CANONICAL-FORM GUARD. `1.50` and `1.5` are the same number, so they must be the same bytes.
#[test]
fn trailing_zeros_are_normalised_away() {
    assert_eq!(enc(&IonDecimal::new(-2, 150)), enc(&IonDecimal::new(-1, 15)));
    assert_eq!(enc(&IonDecimal::new(-6, 1_500_000)), enc(&IonDecimal::new(-1, 15)));
    assert_eq!(enc(&IonDecimal::new(-2, 150)), "c482200f");

    // …while the in-memory value keeps the authored scale, so Display is faithful.
    assert_eq!(IonDecimal::new(-2, 150).to_string(), "1.50");
    assert_eq!(IonDecimal::new(-1, 15).to_string(), "1.5");
    assert_ne!(IonDecimal::new(-2, 150), IonDecimal::new(-1, 15));
    assert!(IonDecimal::new(-2, 150).eq_numeric(&IonDecimal::new(-1, 15)));
}

#[test]
fn every_zero_collapses_to_the_same_four_bytes() {
    for d in [IonDecimal::ZERO, IonDecimal::new(-2, 0), IonDecimal::new(5, 0)] {
        assert_eq!(enc(&d), "c4820000", "{d:?}");
    }
}

/// The mantissa is a plain CBOR integer across the whole i64/u64 window and a bignum only beyond
/// it — the same boundary C# and TypeScript draw.
#[test]
fn mantissa_switches_to_bignum_at_the_i64_u64_boundary() {
    assert!(enc(&IonDecimal::new(0, i64::MIN as i128)).starts_with("c482003b"));
    assert!(enc(&IonDecimal::new(0, u64::MAX as i128)).starts_with("c482001b"));
    assert!(enc(&IonDecimal::new(0, u64::MAX as i128 + 1)).starts_with("c48200c2"));
    assert!(enc(&IonDecimal::new(0, i64::MIN as i128 - 1)).starts_with("c48200c3"));
}

/// A mantissa beyond `i128` is a typed error, not a truncation. It is reachable only from the
/// TypeScript runtime, whose mantissa is a native `bigint`.
#[test]
fn mantissa_beyond_i128_is_a_typed_range_error() {
    // tag 4, [0, tag 2 <17 bytes of 0xff>] — a 136-bit magnitude.
    let hex = format!("c48200c25100{}", "ff".repeat(17));
    assert!(matches!(try_decode(&hex), Err(IonError::DecimalRange { .. })), "got {:?}", try_decode(&hex));
}

#[test]
fn display_and_from_str_round_trip() {
    for v in vectors() {
        let canonical = IonDecimal::new(v.canonical_exponent, v.canonical_mantissa);
        let text = canonical.to_string();
        let reparsed = IonDecimal::from_str(&text)
            .unwrap_or_else(|e| panic!("vector '{}' text '{text}' failed to parse: {e}", v.name));
        assert!(reparsed.eq_numeric(&canonical), "vector '{}' ('{text}')", v.name);
    }
}

#[test]
fn from_str_parses_scientific_notation() {
    assert!(IonDecimal::from_str("1e-28").unwrap().eq_numeric(&IonDecimal::new(-28, 1)));
    assert!(IonDecimal::from_str("+3.14E+2").unwrap().eq_numeric(&IonDecimal::new(0, 314)));
    assert!(IonDecimal::from_str("-1.5").unwrap().eq_numeric(&IonDecimal::new(-1, -15)));
    assert_eq!(IonDecimal::from_str("1.50").unwrap(), IonDecimal::new(-2, 150));
}

#[test]
fn from_str_rejects_non_numbers() {
    for bad in ["", "abc", "1.2.3", "--1", "1e", "0x10", ".5"] {
        assert!(IonDecimal::from_str(bad).is_err(), "'{bad}' should not parse");
    }
}

/// `i128::MIN` has no positive counterpart; neither `Display` nor the bignum writer may overflow.
#[test]
fn i128_min_mantissa_does_not_overflow() {
    let d = IonDecimal::new(0, i128::MIN);
    assert_eq!(d.to_string(), i128::MIN.to_string());
    let hex = enc(&d);
    assert!(hex.starts_with("c48200c3"), "{hex}");
    assert_eq!(dec::<IonDecimal>(&hex), d);
}

/// A decimal written inside a container still counts as exactly one item.
#[test]
fn decimals_nest_in_containers_correctly() {
    let mut e = minicbor::Encoder::new(Vec::new());
    e.array(2).unwrap();
    ion_rustcore::formatter::IonFormat::ion_write(&IonDecimal::new(-1, 15), &mut e).unwrap();
    e.i32(7).unwrap();

    assert_eq!(to_hex(&e.into_writer()), "82c482200f07");
}
