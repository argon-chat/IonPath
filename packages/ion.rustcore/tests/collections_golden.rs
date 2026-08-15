//! Cross-runtime golden vectors for Ion's three container types: `Map<K,V>`, `Set<T>` and the
//! fixed-size array `T[N]`.
//!
//! `/tests/golden/collections.golden.json` is also consumed by
//!   `src/tests/IonTestClientServer/CollectionGoldenTests.cs`    (C#)
//!   `packages/ion.webcore.js/test/collections.golden.test.ts`   (TypeScript)

mod common;

use common::*;
use ion_rustcore::formatter::{
    canonical_cbor_cmp, read_fixed_array, read_map, read_set, write_fixed_array, write_map,
    write_set, IonFormat,
};
use ion_rustcore::IonError;
use minicbor::{Decoder, Encoder};
use serde_json::Value;
use std::collections::{HashMap, HashSet};
use std::hash::Hash;

// ── generic helpers ─────────────────────────────────────────────────────────

fn map_hex<K, V>(entries: &[Value], reverse: bool, k: fn(&Value) -> K, v: fn(&Value) -> V) -> String
where
    K: IonFormat + Eq + Hash,
    V: IonFormat,
{
    let mut items: Vec<(K, V)> =
        entries.iter().map(|e| (k(&e["key"]), v(&e["value"]))).collect();
    if reverse {
        items.reverse();
    }
    let map: HashMap<K, V> = items.into_iter().collect();

    let mut e = Encoder::new(Vec::new());
    write_map(&mut e, map.iter()).expect("write_map failed");
    to_hex(&e.into_writer())
}

fn map_reencode<K, V>(hex: &str) -> Result<String, IonError>
where
    K: IonFormat + Eq + Hash,
    V: IonFormat,
{
    let bytes = from_hex(hex);
    let mut d = Decoder::new(&bytes);
    let map: HashMap<K, V> = read_map::<K, V, HashMap<K, V>>(&mut d)?;

    let mut e = Encoder::new(Vec::new());
    write_map(&mut e, map.iter())?;
    Ok(to_hex(&e.into_writer()))
}

fn set_hex<T>(elements: &[Value], reverse: bool, f: fn(&Value) -> T) -> String
where
    T: IonFormat + Eq + Hash,
{
    let mut items: Vec<T> = elements.iter().map(f).collect();
    if reverse {
        items.reverse();
    }
    let set: HashSet<T> = items.into_iter().collect();

    let mut e = Encoder::new(Vec::new());
    write_set(&mut e, set.iter()).expect("write_set failed");
    to_hex(&e.into_writer())
}

fn set_reencode<T>(hex: &str) -> Result<String, IonError>
where
    T: IonFormat + Eq + Hash,
{
    let bytes = from_hex(hex);
    let mut d = Decoder::new(&bytes);
    let set: HashSet<T> = read_set::<T, HashSet<T>>(&mut d)?;

    let mut e = Encoder::new(Vec::new());
    write_set(&mut e, set.iter())?;
    Ok(to_hex(&e.into_writer()))
}

fn fixed_hex<T: IonFormat>(elements: &[Value], n: usize, f: fn(&Value) -> T) -> String {
    let items: Vec<T> = elements.iter().map(f).collect();
    let mut e = Encoder::new(Vec::new());
    write_fixed_array(&mut e, &items, n).expect("write_fixed_array failed");
    to_hex(&e.into_writer())
}

fn fixed_reencode<T: IonFormat>(hex: &str, n: usize) -> Result<String, IonError> {
    let bytes = from_hex(hex);
    let mut d = Decoder::new(&bytes);
    let items = read_fixed_array::<T>(&mut d, n)?;

    let mut e = Encoder::new(Vec::new());
    write_fixed_array(&mut e, &items, n)?;
    Ok(to_hex(&e.into_writer()))
}

// ── per-type dispatch ───────────────────────────────────────────────────────

fn encode_map(key_type: &str, value_type: &str, entries: &[Value], reverse: bool) -> String {
    match (key_type, value_type) {
        ("string", "i4") => map_hex::<String, i32>(entries, reverse, as_string, as_i32),
        ("string", "string") => map_hex::<String, String>(entries, reverse, as_string, as_string),
        ("i4", "i4") => map_hex::<i32, i32>(entries, reverse, as_i32, as_i32),
        ("u4", "i4") => map_hex::<u32, i32>(entries, reverse, as_u32, as_i32),
        ("i8", "i4") => map_hex::<i64, i32>(entries, reverse, as_i64, as_i32),
        ("guid", "i4") => map_hex::<uuid::Uuid, i32>(entries, reverse, as_uuid, as_i32),
        ("bool", "i4") => map_hex::<bool, i32>(entries, reverse, as_bool, as_i32),
        other => panic!("unsupported map type {other:?}"),
    }
}

fn reencode_map(key_type: &str, value_type: &str, hex: &str) -> Result<String, IonError> {
    match (key_type, value_type) {
        ("string", "i4") => map_reencode::<String, i32>(hex),
        ("string", "string") => map_reencode::<String, String>(hex),
        ("i4", "i4") => map_reencode::<i32, i32>(hex),
        ("u4", "i4") => map_reencode::<u32, i32>(hex),
        ("i8", "i4") => map_reencode::<i64, i32>(hex),
        ("guid", "i4") => map_reencode::<uuid::Uuid, i32>(hex),
        ("bool", "i4") => map_reencode::<bool, i32>(hex),
        other => panic!("unsupported map type {other:?}"),
    }
}

fn encode_set(element_type: &str, elements: &[Value], reverse: bool) -> String {
    match element_type {
        "i4" => set_hex::<i32>(elements, reverse, as_i32),
        "string" => set_hex::<String>(elements, reverse, as_string),
        "guid" => set_hex::<uuid::Uuid>(elements, reverse, as_uuid),
        other => panic!("unsupported set element type '{other}'"),
    }
}

fn reencode_set(element_type: &str, hex: &str) -> Result<String, IonError> {
    match element_type {
        "i4" => set_reencode::<i32>(hex),
        "string" => set_reencode::<String>(hex),
        "guid" => set_reencode::<uuid::Uuid>(hex),
        other => panic!("unsupported set element type '{other}'"),
    }
}

fn encode_fixed(element_type: &str, elements: &[Value], n: usize) -> String {
    match element_type {
        "i4" => fixed_hex::<i32>(elements, n, as_i32),
        "u1" => fixed_hex::<u8>(elements, n, as_u8),
        "string" => fixed_hex::<String>(elements, n, as_string),
        "guid" => fixed_hex::<uuid::Uuid>(elements, n, as_uuid),
        other => panic!("unsupported fixed-array element type '{other}'"),
    }
}

fn reencode_fixed(element_type: &str, hex: &str, n: usize) -> Result<String, IonError> {
    match element_type {
        "i4" => fixed_reencode::<i32>(hex, n),
        "u1" => fixed_reencode::<u8>(hex, n),
        "string" => fixed_reencode::<String>(hex, n),
        "guid" => fixed_reencode::<uuid::Uuid>(hex, n),
        other => panic!("unsupported fixed-array element type '{other}'"),
    }
}

fn section(name: &str, part: &str) -> Vec<Value> {
    golden("collections.golden.json")[name][part].as_array().unwrap().clone()
}

// ═══════════════════════════════════════════════════════════════════════════
//  Map<K,V>
// ═══════════════════════════════════════════════════════════════════════════

#[test]
fn map_golden_encode() {
    for v in section("map", "vectors") {
        let entries = v["entries"].as_array().unwrap();
        assert_eq!(
            encode_map(&req(&v, "keyType"), &req(&v, "valueType"), entries, false),
            req(&v, "hex"),
            "map vector '{}': {}",
            req(&v, "name"),
            s(&v, "notes")
        );
    }
}

/// THE POINT OF SORTING. A Rust `HashMap`, a C# `Dictionary` and a JavaScript `Map` have three
/// different iteration orders — a `HashMap` does not even have a stable one between runs — so the
/// same entries must produce the same bytes regardless of how they went in.
#[test]
fn map_encode_is_independent_of_insertion_order() {
    for v in section("map", "vectors") {
        let entries = v["entries"].as_array().unwrap();
        let key_type = req(&v, "keyType");
        let value_type = req(&v, "valueType");
        assert_eq!(
            encode_map(&key_type, &value_type, entries, true),
            req(&v, "hex"),
            "map vector '{}' must not depend on insertion order",
            req(&v, "name")
        );
    }
}

#[test]
fn map_golden_decode() {
    for v in section("map", "vectors") {
        assert_eq!(
            reencode_map(&req(&v, "keyType"), &req(&v, "valueType"), &req(&v, "hex")).unwrap(),
            req(&v, "hex"),
            "map vector '{}': {}",
            req(&v, "name"),
            s(&v, "notes")
        );
    }
}

#[test]
fn map_keys_are_in_canonical_order() {
    for v in section("map", "vectors") {
        let encoded = encode_map(
            &req(&v, "keyType"),
            &req(&v, "valueType"),
            v["entries"].as_array().unwrap(),
            false,
        );
        // The map header is one byte for every vector here (fewer than 24 entries).
        let mut body = encoded[2..].to_owned();

        for key in v["canonicalKeyOrder"].as_array().unwrap() {
            let key = key.as_str().unwrap();
            assert!(
                body.starts_with(key),
                "map vector '{}': next key must be {key}, body is {body}",
                req(&v, "name")
            );
            body = body[key.len()..].to_owned();
            body = body[next_item_hex_len(&body)..].to_owned(); // skip the value
        }
        assert!(body.is_empty(), "map vector '{}': trailing bytes {body}", req(&v, "name"));
    }
}

#[test]
fn map_decode_only_is_lenient() {
    for v in section("map", "decodeOnly") {
        assert_eq!(
            reencode_map(&req(&v, "keyType"), &req(&v, "valueType"), &req(&v, "hex")).unwrap(),
            req(&v, "reencodedHex"),
            "map decode-only vector '{}': {}",
            req(&v, "name"),
            s(&v, "notes")
        );
    }
}

/// Duplicate keys are REJECTED. Last-wins and first-wins both make the decoded value depend on
/// wire order — the very non-determinism sorting exists to remove.
#[test]
fn map_duplicate_keys_raise_a_typed_error() {
    for v in section("map", "malformed") {
        let err = reencode_map(&req(&v, "keyType"), &req(&v, "valueType"), &req(&v, "hex"))
            .err()
            .unwrap_or_else(|| panic!("map malformed vector '{}' decoded", req(&v, "name")));

        assert!(
            matches!(err, IonError::DuplicateMapKey { .. }),
            "map malformed vector '{}' produced {err:?}: {}",
            req(&v, "name"),
            s(&v, "notes")
        );
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  Set<T>
// ═══════════════════════════════════════════════════════════════════════════

#[test]
fn set_golden_encode() {
    for v in section("set", "vectors") {
        assert_eq!(
            encode_set(&req(&v, "elementType"), v["elements"].as_array().unwrap(), false),
            req(&v, "hex"),
            "set vector '{}': {}",
            req(&v, "name"),
            s(&v, "notes")
        );
    }
}

#[test]
fn set_encode_is_independent_of_insertion_order() {
    for v in section("set", "vectors") {
        assert_eq!(
            encode_set(&req(&v, "elementType"), v["elements"].as_array().unwrap(), true),
            req(&v, "hex"),
            "set vector '{}' must not depend on insertion order",
            req(&v, "name")
        );
    }
}

/// ORDER-INDEPENDENCE, stated as the golden file states it.
#[test]
fn two_insertion_orders_produce_identical_bytes() {
    let vectors = section("set", "vectors");
    let a = vectors.iter().find(|v| req(v, "name") == "insertion-order-a").unwrap();
    let b = vectors.iter().find(|v| req(v, "name") == "insertion-order-b").unwrap();

    assert_eq!(req(a, "hex"), req(b, "hex"), "the golden file itself must pin them together");
    assert_eq!(
        encode_set("i4", a["elements"].as_array().unwrap(), false),
        encode_set("i4", b["elements"].as_array().unwrap(), false)
    );
    assert_eq!(encode_set("i4", a["elements"].as_array().unwrap(), false), "d9010283010203");
}

#[test]
fn set_golden_decode() {
    for v in section("set", "vectors") {
        assert_eq!(
            reencode_set(&req(&v, "elementType"), &req(&v, "hex")).unwrap(),
            req(&v, "hex"),
            "set vector '{}': {}",
            req(&v, "name"),
            s(&v, "notes")
        );
    }
}

/// The tag is what distinguishes `Set<T>` from `Array<T>` on the wire.
#[test]
fn every_set_is_tagged_258() {
    for v in section("set", "vectors") {
        assert!(req(&v, "hex").starts_with("d90102"), "set vector '{}'", req(&v, "name"));
        assert!(encode_set(&req(&v, "elementType"), v["elements"].as_array().unwrap(), false)
            .starts_with("d90102"));
    }
}

#[test]
fn set_decode_only_is_lenient() {
    for v in section("set", "decodeOnly") {
        assert_eq!(
            reencode_set(&req(&v, "elementType"), &req(&v, "hex")).unwrap(),
            req(&v, "reencodedHex"),
            "set decode-only vector '{}': {}",
            req(&v, "name"),
            s(&v, "notes")
        );
    }
}

#[test]
fn set_malformed_raises_typed_errors() {
    for v in section("set", "malformed") {
        let name = req(&v, "name");
        let err = reencode_set(&req(&v, "elementType"), &req(&v, "hex"))
            .err()
            .unwrap_or_else(|| panic!("set malformed vector '{name}' decoded"));

        let matched = match name.as_str() {
            "duplicate-elements" => matches!(err, IonError::DuplicateSetElement { .. }),
            "wrong-tag" => matches!(err, IonError::UnexpectedTag { .. }),
            // An untagged array is Array<T>, not Set<T>: rejected rather than accepted as a
            // courtesy, because accepting it would erase the distinction at the only point it
            // can still be checked.
            "missing-tag" => matches!(err, IonError::MalformedValue { .. }),
            other => panic!("unhandled set malformed vector '{other}'"),
        };
        assert!(matched, "set malformed vector '{name}' produced {err:?}: {}", s(&v, "notes"));
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  T[N]
// ═══════════════════════════════════════════════════════════════════════════

#[test]
fn fixed_golden_encode() {
    for v in section("fixedArray", "vectors") {
        let n = v["length"].as_u64().unwrap() as usize;
        assert_eq!(
            encode_fixed(&req(&v, "elementType"), v["elements"].as_array().unwrap(), n),
            req(&v, "hex"),
            "fixed-array vector '{}': {}",
            req(&v, "name"),
            s(&v, "notes")
        );
    }
}

#[test]
fn fixed_golden_decode() {
    for v in section("fixedArray", "vectors") {
        let n = v["length"].as_u64().unwrap() as usize;
        assert_eq!(
            reencode_fixed(&req(&v, "elementType"), &req(&v, "hex"), n).unwrap(),
            req(&v, "hex"),
            "fixed-array vector '{}': {}",
            req(&v, "name"),
            s(&v, "notes")
        );
    }
}

/// NO-BYTE-STRING GUARD. `u1[4]` is an array of four integers, not a 4-byte CBOR byte string.
#[test]
fn u1_fixed_array_is_integers_not_a_byte_string() {
    let vectors = section("fixedArray", "vectors");
    let v = vectors.iter().find(|v| req(v, "name") == "u1-n4").unwrap();

    assert_eq!(req(v, "hex"), "8401020304");
    assert!(!req(v, "hex").starts_with("44"));
    assert_eq!(encode_fixed("u1", v["elements"].as_array().unwrap(), 4), "8401020304");
}

#[test]
fn fixed_decode_only_is_lenient() {
    for v in section("fixedArray", "decodeOnly") {
        let n = v["length"].as_u64().unwrap() as usize;
        assert_eq!(
            reencode_fixed(&req(&v, "elementType"), &req(&v, "hex"), n).unwrap(),
            req(&v, "reencodedHex"),
            "fixed-array decode-only vector '{}': {}",
            req(&v, "name"),
            s(&v, "notes")
        );
    }
}

/// THE ENTIRE POINT OF THE FEATURE: the error names BOTH lengths.
#[test]
fn fixed_wrong_length_raises_a_typed_error_naming_both_lengths() {
    for v in section("fixedArray", "malformed") {
        let n = v["length"].as_u64().unwrap() as usize;
        let expected_actual = v["actualLength"].as_u64().unwrap() as usize;

        let err = reencode_fixed(&req(&v, "elementType"), &req(&v, "hex"), n)
            .err()
            .unwrap_or_else(|| panic!("fixed malformed vector '{}' decoded", req(&v, "name")));

        match err {
            IonError::FixedArrayLength { expected, actual } => {
                assert_eq!(expected, n, "vector '{}': declared N", req(&v, "name"));
                assert_eq!(actual, expected_actual, "vector '{}': received length", req(&v, "name"));
                let text = IonError::FixedArrayLength { expected, actual }.to_string();
                assert!(text.contains(&n.to_string()) && text.contains(&expected_actual.to_string()));
            }
            other => panic!(
                "fixed malformed vector '{}' produced {other:?}: {}",
                req(&v, "name"),
                s(&v, "notes")
            ),
        }
    }
}

/// Writers are exact too: a mismatched slice is rejected before it reaches the wire.
#[test]
fn fixed_write_rejects_a_mismatched_length() {
    let mut e = Encoder::new(Vec::new());
    let err = write_fixed_array(&mut e, &[1i32, 2], 3).unwrap_err();
    assert!(matches!(err, IonError::FixedArrayLength { expected: 3, actual: 2 }), "{err:?}");
}

/// `N` is a parameter, not part of the type — and the const-generic `[T; N]` impl is a convenience
/// layered on the same function, not a second implementation.
#[test]
fn fixed_length_is_a_parameter() {
    let enc_n = |values: &[i32], n: usize| {
        let mut e = Encoder::new(Vec::new());
        write_fixed_array(&mut e, values, n).unwrap();
        to_hex(&e.into_writer())
    };
    assert_eq!(enc_n(&[1], 1), "8101");
    assert_eq!(enc_n(&[1, 2], 2), "820102");
    assert_eq!(enc_n(&[1, 2, 3], 3), "83010203");

    // The const-generic impl agrees.
    assert_eq!(enc(&[1i32, 2, 3]), "83010203");
    assert_eq!(dec::<[i32; 3]>("83010203"), [1, 2, 3]);
}

// ═══════════════════════════════════════════════════════════════════════════
//  cross-cutting
// ═══════════════════════════════════════════════════════════════════════════

/// Length-first ordering is NOT plain bytewise ordering, and this is the case that proves it.
#[test]
fn canonical_order_is_length_first_not_bytewise() {
    let minus_one: &[u8] = &[0x20];
    let thousand: &[u8] = &[0x19, 0x03, 0xe8];

    assert!(canonical_cbor_cmp(minus_one, thousand).is_lt(), "length-first puts -1 first");
    assert!(minus_one > thousand, "…while a plain bytewise comparison says the opposite");

    let vectors = section("map", "vectors");
    let v = vectors.iter().find(|v| req(v, "name") == "i4-keys-length-beats-lexicographic").unwrap();
    assert_eq!(
        encode_map("i4", "i4", v["entries"].as_array().unwrap(), false),
        "a4000420021818031903e801"
    );
}

/// Containers nest: a `HashSet` inside a `HashMap` value is still exactly one item, and both
/// resolve through the blanket `IonFormat` impls with no special-casing.
#[test]
fn containers_nest() {
    let mut map: HashMap<String, HashSet<i32>> = HashMap::new();
    map.insert("a".into(), HashSet::from([2, 1]));

    assert_eq!(enc(&map), "a16161d90102820102");
}
