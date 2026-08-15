//! Cross-runtime golden vectors for Ion's three float widths.
//!
//! `/tests/golden/float.golden.json` is also consumed by
//!   `src/tests/IonTestClientServer/FloatGoldenTests.cs`   (C#)
//!   `packages/ion.webcore.js/test/float.golden.test.ts`   (TypeScript)
//! All three runtimes must produce byte-identical CBOR for the same value.

use ion_rustcore::formatter::IonFormat;
use ion_rustcore::std_formatters::IonF16;
use minicbor::{Decoder, Encoder};

// ── golden file ─────────────────────────────────────────────────────────────

const GOLDEN_PATH: &str = concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../../tests/golden/float.golden.json"
);

struct Vector {
    name: String,
    ty: String,
    bits: String,
    repr: String,
    hex: String,
    notes: String,
}

struct CrossWidth {
    name: String,
    ty: String,
    hex: String,
    reencoded_hex: String,
    notes: String,
}

fn golden() -> serde_json::Value {
    let raw = std::fs::read_to_string(GOLDEN_PATH)
        .unwrap_or_else(|e| panic!("cannot read {GOLDEN_PATH}: {e}"));
    serde_json::from_str(&raw).expect("golden file is not valid JSON")
}

fn vectors() -> Vec<Vector> {
    golden()["vectors"]
        .as_array()
        .expect("golden file has no 'vectors' array")
        .iter()
        .map(|v| Vector {
            name: v["name"].as_str().unwrap().to_owned(),
            ty: v["type"].as_str().unwrap().to_owned(),
            bits: v["bits"].as_str().unwrap().to_owned(),
            repr: v["repr"].as_str().unwrap_or("").to_owned(),
            hex: v["hex"].as_str().unwrap().to_owned(),
            notes: v["notes"].as_str().unwrap_or("").to_owned(),
        })
        .collect()
}

fn cross_width() -> Vec<CrossWidth> {
    golden()["crossWidth"]
        .as_array()
        .expect("golden file has no 'crossWidth' array")
        .iter()
        .map(|v| CrossWidth {
            name: v["name"].as_str().unwrap().to_owned(),
            ty: v["type"].as_str().unwrap().to_owned(),
            hex: v["hex"].as_str().unwrap().to_owned(),
            reencoded_hex: v["reencodedHex"].as_str().unwrap().to_owned(),
            notes: v["notes"].as_str().unwrap_or("").to_owned(),
        })
        .collect()
}

// ── helpers ─────────────────────────────────────────────────────────────────

fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn from_hex(hex: &str) -> Vec<u8> {
    (0..hex.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap())
        .collect()
}

fn enc<T: IonFormat>(v: &T) -> String {
    let mut e = Encoder::new(Vec::new());
    v.ion_write(&mut e).expect("encode failed");
    to_hex(&e.into_writer())
}

fn dec<T: IonFormat>(hex: &str) -> T {
    let bytes = from_hex(hex);
    let mut d = Decoder::new(&bytes);
    T::ion_read(&mut d).unwrap_or_else(|e| panic!("decode of '{hex}' failed: {e}"))
}

/// Builds the value for a vector's IEEE bit pattern and encodes it.
///
/// `IonF16` stores an `f32`, so an `f2` bit pattern is widened through `half::f16` first — the
/// same lossless path the decoder uses.
fn encode_bits(ty: &str, bits: &str) -> String {
    match ty {
        "f2" => enc(&IonF16(
            half::f16::from_bits(u16::from_str_radix(bits, 16).unwrap()).to_f32(),
        )),
        "f4" => enc(&f32::from_bits(u32::from_str_radix(bits, 16).unwrap())),
        "f8" => enc(&f64::from_bits(u64::from_str_radix(bits, 16).unwrap())),
        other => panic!("unknown ion float type '{other}'"),
    }
}

/// Decodes `hex` as `ty` and re-encodes it.
fn reencode(ty: &str, hex: &str) -> String {
    match ty {
        "f2" => enc(&dec::<IonF16>(hex)),
        "f4" => enc(&dec::<f32>(hex)),
        "f8" => enc(&dec::<f64>(hex)),
        other => panic!("unknown ion float type '{other}'"),
    }
}

fn payload_bytes(ty: &str) -> usize {
    match ty {
        "f2" => 2,
        "f4" => 4,
        "f8" => 8,
        other => panic!("unknown ion float type '{other}'"),
    }
}

// ── tests ───────────────────────────────────────────────────────────────────

#[test]
fn golden_file_is_self_consistent() {
    for v in vectors() {
        let header = match v.ty.as_str() {
            "f2" => "f9",
            "f4" => "fa",
            "f8" => "fb",
            other => panic!("unknown ion float type '{other}'"),
        };
        assert_eq!(v.bits.len(), payload_bytes(&v.ty) * 2, "vector '{}'", v.name);
        assert_eq!(v.hex, format!("{header}{}", v.bits), "vector '{}'", v.name);
    }
}

#[test]
fn golden_encode() {
    for v in vectors() {
        assert_eq!(
            encode_bits(&v.ty, &v.bits),
            v.hex,
            "golden float vector '{}' ({}): {}",
            v.name,
            v.repr,
            v.notes
        );
    }
}

#[test]
fn golden_decode() {
    for v in vectors() {
        assert_eq!(
            reencode(&v.ty, &v.hex),
            v.hex,
            "golden float vector '{}' ({}): {}",
            v.name,
            v.repr,
            v.notes
        );
    }
}

/// The declared width is honoured regardless of value — an `f8` field holding 1.5 is still
/// 9 wire bytes, not the 3 a shortest-form writer would emit.
#[test]
fn golden_length_is_always_the_declared_width() {
    for v in vectors() {
        let expected = (1 + payload_bytes(&v.ty)) * 2;
        assert_eq!(v.hex.len(), expected, "vector '{}'", v.name);
        assert_eq!(
            encode_bits(&v.ty, &v.bits).len(),
            expected,
            "vector '{}'",
            v.name
        );
    }
}

/// Readers accept every wire width for every declared width, in both directions — including the
/// shrunken payloads the previous C# release wrote. minicbor's own `Decoder::f32` rejects `0xFB`
/// and `Decoder::f16` rejects both `0xFA` and `0xFB`, so this is the guard on `read_any_width`.
#[test]
fn golden_cross_width_reads() {
    for v in cross_width() {
        assert_eq!(
            reencode(&v.ty, &v.hex),
            v.reencoded_hex,
            "cross-width vector '{}': {}",
            v.name,
            v.notes
        );
    }
}

/// `Encoder::f16`/`f32`/`f64` write a NaN's raw bits, which preserves its sign. .NET's
/// `float.NaN` is `ffc00000` and JS's `Math.sqrt(-1)` is too, so without canonicalisation NaN
/// would be the one value on which the three runtimes still disagreed.
#[test]
fn nan_is_canonicalised_regardless_of_sign_or_payload() {
    for nan in [
        f32::NAN,
        -f32::NAN,
        f32::from_bits(0xffc0_0001),
        f32::from_bits(0x7f80_0001),
        0.0f32 / 0.0f32,
    ] {
        assert_eq!(enc(&nan), "fa7fc00000", "f32 NaN bits {:08x}", nan.to_bits());
        assert_eq!(enc(&IonF16(nan)), "f97e00", "f16 NaN bits {:08x}", nan.to_bits());
    }

    for nan in [
        f64::NAN,
        -f64::NAN,
        f64::from_bits(0xfff8_0000_0000_0001),
        0.0f64 / 0.0f64,
    ] {
        assert_eq!(
            enc(&nan),
            "fb7ff8000000000000",
            "f64 NaN bits {:016x}",
            nan.to_bits()
        );
    }
}

/// `-0.0` is the counterpart to the NaN rule: it must NOT be canonicalised.
#[test]
fn negative_zero_is_preserved_and_distinct_from_positive_zero() {
    assert_eq!(enc(&-0.0f32), "fa80000000");
    assert_eq!(enc(&0.0f32), "fa00000000");
    assert_eq!(enc(&-0.0f64), "fb8000000000000000");
    assert_eq!(enc(&0.0f64), "fb0000000000000000");
    assert_eq!(enc(&IonF16(-0.0f32)), "f98000");

    assert_eq!(dec::<f32>("fa80000000").to_bits(), 0x8000_0000);
    assert_eq!(dec::<f64>("fb8000000000000000").to_bits(), 0x8000_0000_0000_0000);
    // Survives a legacy shrunken payload too.
    assert_eq!(dec::<f32>("f98000").to_bits(), 0x8000_0000);
}

/// Half (`f2`) was already correct and must stay that way: `0xF9` + 2 bytes.
#[test]
fn half_still_writes_two_payload_bytes() {
    for v in vectors().into_iter().filter(|v| v.ty == "f2") {
        let hex = encode_bits("f2", &v.bits);
        assert!(hex.starts_with("f9"), "f2 vector '{}': {hex}", v.name);
        assert_eq!(hex.len(), 6, "f2 vector '{}'", v.name);
    }
}

/// A float written inside a container still counts as exactly one item.
#[test]
fn floats_nest_in_containers_correctly() {
    let mut e = Encoder::new(Vec::new());
    e.array(3).unwrap();
    IonF16(1.5).ion_write(&mut e).unwrap();
    1.5f32.ion_write(&mut e).unwrap();
    1.5f64.ion_write(&mut e).unwrap();

    assert_eq!(to_hex(&e.into_writer()), "83f93e00fa3fc00000fb3ff8000000000000");
}
