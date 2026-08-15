//! Cross-runtime golden vectors for `Partial<T>`.
//!
//! `/tests/golden/partial.golden.json` is also consumed by
//!   `src/tests/IonTestClientServer/TestTypes.cs`            (C#)
//!   `packages/ion.webcore.js/test/partial.golden.test.ts`   (TypeScript)
//! All three must produce byte-identical CBOR for the same patch.

use ion_rustcore::{
    decode_partial, encode_partial, ion_partial, IonPartial, IonPartialField, IonPartialFields,
    IonPartialState,
};

// msg GoldenPatchTarget { n: i4; f: f4; s: string; items: i4[]; note: string?; }
#[derive(Debug, Clone, Default, PartialEq)]
pub struct GoldenPatchTarget {
    pub n: i32,
    pub f: f32,
    pub s: String,
    pub items: Vec<i32>,
    pub note: Option<String>,
}

// Exactly what codegen will emit for `GoldenPatchTarget~`.
ion_partial! {
    /// Sparse patch over [`GoldenPatchTarget`].
    pub struct GoldenPatchTargetPatch for GoldenPatchTarget {
        n: i32,
        f: f32,
        s: String,
        items: Vec<i32>,
        note: Option<String>,
    }
}

// ── golden file ─────────────────────────────────────────────────────────────

const GOLDEN_PATH: &str = concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../../tests/golden/partial.golden.json"
);

struct Vector {
    name: String,
    direction: String,
    hex: String,
    reencoded_hex: Option<String>,
    notes: String,
}

fn golden_vectors() -> Vec<Vector> {
    let raw = std::fs::read_to_string(GOLDEN_PATH)
        .unwrap_or_else(|e| panic!("cannot read {GOLDEN_PATH}: {e}"));
    let doc: serde_json::Value = serde_json::from_str(&raw).expect("golden file is not valid JSON");

    doc["vectors"]
        .as_array()
        .expect("golden file has no 'vectors' array")
        .iter()
        .map(|v| Vector {
            name: v["name"].as_str().unwrap().to_owned(),
            direction: v["direction"].as_str().unwrap().to_owned(),
            hex: v["hex"].as_str().unwrap().to_owned(),
            reencoded_hex: v["reencodedHex"].as_str().map(str::to_owned),
            notes: v["notes"].as_str().unwrap_or("").to_owned(),
        })
        .collect()
}

fn vector(name: &str) -> Vector {
    golden_vectors()
        .into_iter()
        .find(|v| v.name == name)
        .unwrap_or_else(|| panic!("golden vector '{name}' not found"))
}

fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn from_hex(hex: &str) -> Vec<u8> {
    (0..hex.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap())
        .collect()
}

// ── patch builders, one per golden vector ───────────────────────────────────

fn build(name: &str) -> Option<GoldenPatchTargetPatch> {
    let mut p = GoldenPatchTargetPatch::default();
    match name {
        "empty" => {}
        "modified-scalar-int" => p.n = IonPartialField::Modified(7),
        "modified-scalar-float" => p.f = IonPartialField::Modified(1.1),
        "modified-scalar-float-half-representable" => p.f = IonPartialField::Modified(1.5),
        "cleared-scalar-float" => p.f = IonPartialField::Removed,
        "cleared-scalar-reference" => p.s = IonPartialField::Removed,
        "modified-array" => p.items = IonPartialField::Modified(vec![1, 2, 3]),
        "cleared-array" => p.items = IonPartialField::Removed,
        "modified-optional-some" => p.note = IonPartialField::Modified(Some("hi".into())),
        "cleared-optional" => p.note = IonPartialField::Removed,
        "modified-optional-none" => p.note = IonPartialField::Modified(None),
        "all-fields" => {
            p.note = IonPartialField::Modified(Some("hi".into()));
            p.items = IonPartialField::Modified(vec![1, 2, 3]);
            p.s = IonPartialField::Modified("ab".into());
            p.f = IonPartialField::Removed;
            p.n = IonPartialField::Modified(7);
        }
        _ => return None,
    }
    Some(p)
}

// ── tests ───────────────────────────────────────────────────────────────────

#[test]
fn golden_encode() {
    for v in golden_vectors() {
        if v.direction != "encode" && v.direction != "roundtrip" {
            continue;
        }

        let patch = build(&v.name)
            .unwrap_or_else(|| panic!("no Rust builder for golden vector '{}'", v.name));
        let bytes = encode_partial(&patch).expect("encode failed");

        assert_eq!(to_hex(&bytes), v.hex, "golden vector '{}': {}", v.name, v.notes);
    }
}

#[test]
fn golden_decode() {
    for v in golden_vectors() {
        if v.direction != "decode" && v.direction != "roundtrip" {
            continue;
        }

        let decoded: GoldenPatchTargetPatch =
            decode_partial(&from_hex(&v.hex)).expect("decode failed");
        let reencoded = encode_partial(&decoded).expect("re-encode failed");

        assert_eq!(
            to_hex(&reencoded),
            v.reencoded_hex.unwrap_or(v.hex),
            "golden vector '{}': {}",
            v.name,
            v.notes
        );
    }
}

#[test]
fn cleared_and_modified_none_are_the_same_bytes() {
    let mut cleared = GoldenPatchTargetPatch::default();
    cleared.note = IonPartialField::Removed;

    let mut none = GoldenPatchTargetPatch::default();
    none.note = IonPartialField::Modified(None);

    assert_eq!(
        to_hex(&encode_partial(&cleared).unwrap()),
        to_hex(&encode_partial(&none).unwrap())
    );
}

#[test]
fn unknown_keys_are_skipped() {
    let v = vector("unknown-keys-skipped");
    let decoded: GoldenPatchTargetPatch = decode_partial(&from_hex(&v.hex)).unwrap();

    assert_eq!(decoded.n, IonPartialField::Modified(7));
    assert!(decoded.f.is_untouched());
    assert!(decoded.s.is_untouched());
    assert!(decoded.items.is_untouched());
    assert!(decoded.note.is_untouched());
}

#[test]
fn indefinite_length_map_is_read() {
    let v = vector("indefinite-length-map");
    let decoded: GoldenPatchTargetPatch = decode_partial(&from_hex(&v.hex)).unwrap();

    assert_eq!(decoded.n, IonPartialField::Modified(7));
    assert_eq!(decoded.f, IonPartialField::Removed);
}

#[test]
fn field_states_and_order() {
    assert_eq!(
        GoldenPatchTargetPatch::FIELD_NAMES,
        &["n", "f", "s", "items", "note"]
    );

    let mut p = GoldenPatchTargetPatch::default();
    assert_eq!(p.ion_partial_state("n"), IonPartialState::None);

    p.n = IonPartialField::Modified(1);
    assert_eq!(p.ion_partial_state("n"), IonPartialState::Modified);

    p.n = IonPartialField::Removed;
    assert_eq!(p.ion_partial_state("n"), IonPartialState::Removed);

    assert_eq!(p.ion_partial_state("nope"), IonPartialState::None);
}

#[test]
fn ion_partial_alias_resolves_to_the_patch_struct() {
    let patch: IonPartial<GoldenPatchTarget> = GoldenPatchTargetPatch::default();
    assert_eq!(patch, GoldenPatchTargetPatch::default());
}

#[test]
fn apply_patch_to_a_message() {
    let v = vector("all-fields");
    let decoded: GoldenPatchTargetPatch = decode_partial(&from_hex(&v.hex)).unwrap();

    let mut target = GoldenPatchTarget {
        n: 1,
        f: 2.0,
        s: "old".into(),
        items: vec![9],
        note: Some("old".into()),
    };

    decoded.n.apply(&mut target.n);
    decoded.f.apply(&mut target.f);
    decoded.s.apply(&mut target.s);
    decoded.items.apply(&mut target.items);
    decoded.note.apply(&mut target.note);

    assert_eq!(target.n, 7);
    assert_eq!(target.f, 0.0, "cleared field resets to Default");
    assert_eq!(target.s, "ab");
    assert_eq!(target.items, vec![1, 2, 3]);
    assert_eq!(target.note, Some("hi".into()));
}
