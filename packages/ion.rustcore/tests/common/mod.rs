//! Shared plumbing for the cross-runtime golden suites.
//!
//! The files under `/tests/golden` are the contract; these tests are one of its three readers.
//! The C# and TypeScript readers live at
//!   `src/tests/IonTestClientServer/*GoldenTests.cs`
//!   `packages/ion.webcore.js/test/*.golden.test.ts`

#![allow(dead_code)]

use ion_rustcore::formatter::IonFormat;
use minicbor::{Decoder, Encoder};
use serde_json::Value;

pub const GOLDEN_DIR: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/../../tests/golden/");

pub fn golden(file: &str) -> Value {
    let path = format!("{GOLDEN_DIR}{file}");
    let raw = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("cannot read {path}: {e}"));
    serde_json::from_str(&raw).unwrap_or_else(|e| panic!("{path} is not valid JSON: {e}"))
}

pub fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

pub fn from_hex(hex: &str) -> Vec<u8> {
    (0..hex.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap())
        .collect()
}

pub fn enc<T: IonFormat>(value: &T) -> String {
    let mut e = Encoder::new(Vec::new());
    value.ion_write(&mut e).expect("encode failed");
    to_hex(&e.into_writer())
}

pub fn dec<T: IonFormat>(hex: &str) -> T {
    let bytes = from_hex(hex);
    let mut d = Decoder::new(&bytes);
    T::ion_read(&mut d).unwrap_or_else(|e| panic!("decode of '{hex}' failed: {e}"))
}

pub fn s(v: &Value, key: &str) -> String {
    v[key].as_str().unwrap_or("").to_owned()
}

pub fn req(v: &Value, key: &str) -> String {
    v[key]
        .as_str()
        .unwrap_or_else(|| panic!("missing string property '{key}' in {v}"))
        .to_owned()
}

// ── scalar conversion ───────────────────────────────────────────────────────
// Wide integers appear as JSON strings in the golden files so no consumer's JSON parser loses
// precision; every converter therefore accepts both a JSON number and a JSON string.

pub fn as_i64(v: &Value) -> i64 {
    match v {
        Value::String(s) => s.parse().unwrap(),
        _ => v.as_i64().unwrap(),
    }
}

pub fn as_i32(v: &Value) -> i32 {
    as_i64(v) as i32
}

pub fn as_u32(v: &Value) -> u32 {
    match v {
        Value::String(s) => s.parse().unwrap(),
        _ => v.as_u64().unwrap() as u32,
    }
}

pub fn as_u8(v: &Value) -> u8 {
    as_u32(v) as u8
}

pub fn as_string(v: &Value) -> String {
    v.as_str().unwrap().to_owned()
}

pub fn as_bool(v: &Value) -> bool {
    v.as_bool().unwrap()
}

pub fn as_uuid(v: &Value) -> uuid::Uuid {
    uuid::Uuid::parse_str(v.as_str().unwrap()).unwrap()
}

/// Length in hex characters of the single CBOR item at the head of `hex`.
pub fn next_item_hex_len(hex: &str) -> usize {
    let bytes = from_hex(hex);
    let mut d = Decoder::new(&bytes);
    d.skip().expect("skip failed");
    d.position() * 2
}
