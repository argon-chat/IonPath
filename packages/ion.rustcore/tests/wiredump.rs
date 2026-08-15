//! Emits this runtime's half of the cross-runtime byte-equality proof.
//!
//! Asserting each runtime against the golden JSON already implies the three agree. This goes one
//! step further and writes out what each runtime's *real formatters actually produced*, so the
//! claim can be checked by literally diffing three files rather than by trusting three separate
//! assertion suites:
//!
//! ```text
//!   tests/golden/.dump/cs.txt     <- src/tests/IonTestClientServer/WireDumpTests.cs
//!   tests/golden/.dump/ts.txt     <- packages/ion.webcore.js/test/wiredump.test.ts
//!   tests/golden/.dump/rust.txt   <- this test
//!   diff cs.txt ts.txt && diff cs.txt rust.txt
//! ```
//!
//! Nothing here is copied from a golden file's `hex` field — every line is produced by encoding a
//! value through the real `IonFormat` impls. Format: `section/name TAB hex`, in golden-file order.

mod common;

use chrono::{DateTime, FixedOffset};
use common::*;
use ion_rustcore::formatter::{
    read_fixed_array, read_map, read_set, write_fixed_array, write_map, write_set, IonFormat,
};
use ion_rustcore::std_formatters::IonF16;
use ion_rustcore::IonDecimal;
use minicbor::{Decoder, Encoder};
use serde_json::Value;
use std::collections::{HashMap, HashSet};
use std::hash::Hash;

fn build_datetime(unix_ticks: i64, offset_minutes: i32) -> DateTime<FixedOffset> {
    let offset = FixedOffset::east_opt(offset_minutes * 60).unwrap();
    let secs = unix_ticks.div_euclid(10_000_000);
    let ticks = unix_ticks.rem_euclid(10_000_000) as u32;
    chrono::DateTime::from_timestamp(secs, ticks * 100).unwrap().with_timezone(&offset)
}

// Containers are dumped by round-tripping the vector through the real formatters: read the pinned
// bytes, write them back. Because the writer sorts, the output is the encoder's own opinion of
// canonical order, not a copy of the input.
fn map_line<K, V>(hex: &str) -> String
where
    K: IonFormat + Eq + Hash,
    V: IonFormat,
{
    let bytes = from_hex(hex);
    let mut d = Decoder::new(&bytes);
    let map: HashMap<K, V> = read_map::<K, V, HashMap<K, V>>(&mut d).unwrap();
    let mut e = Encoder::new(Vec::new());
    write_map(&mut e, map.iter()).unwrap();
    to_hex(&e.into_writer())
}

fn set_line<T: IonFormat + Eq + Hash>(hex: &str) -> String {
    let bytes = from_hex(hex);
    let mut d = Decoder::new(&bytes);
    let set: HashSet<T> = read_set::<T, HashSet<T>>(&mut d).unwrap();
    let mut e = Encoder::new(Vec::new());
    write_set(&mut e, set.iter()).unwrap();
    to_hex(&e.into_writer())
}

fn fixed_line<T: IonFormat>(hex: &str, n: usize) -> String {
    let bytes = from_hex(hex);
    let mut d = Decoder::new(&bytes);
    let items = read_fixed_array::<T>(&mut d, n).unwrap();
    let mut e = Encoder::new(Vec::new());
    write_fixed_array(&mut e, &items, n).unwrap();
    to_hex(&e.into_writer())
}

fn section(name: &str, part: &str) -> Vec<Value> {
    golden("collections.golden.json")[name][part].as_array().unwrap().clone()
}

#[test]
fn emit_wire_dump() {
    let mut lines: Vec<String> = Vec::new();

    // ── float: the precedent this whole exercise follows ──
    lines.push(format!("float/f2-1.5\t{}", enc(&IonF16(1.5))));
    lines.push(format!("float/f4-1.5\t{}", enc(&1.5f32)));
    lines.push(format!("float/f8-1.5\t{}", enc(&1.5f64)));
    lines.push(format!("float/f4-nan\t{}", enc(&f32::NAN)));

    // ── datetime ──
    for v in golden("datetime.golden.json")["vectors"].as_array().unwrap() {
        let value = build_datetime(
            req(v, "unixTicks").parse().unwrap(),
            v["offsetMinutes"].as_i64().unwrap() as i32,
        );
        lines.push(format!("datetime/{}\t{}", req(v, "name"), enc(&value)));
    }

    // ── decimal ── built from the AUTHORED parts; the formatter canonicalises
    for v in golden("decimal.golden.json")["vectors"].as_array().unwrap() {
        let value = IonDecimal::new(
            v["exponent"].as_i64().unwrap() as i32,
            req(v, "mantissa").parse().unwrap(),
        );
        lines.push(format!("decimal/{}\t{}", req(v, "name"), enc(&value)));
    }

    // ── map / set / fixed array ──
    for v in section("map", "vectors") {
        let hex = req(&v, "hex");
        let line = match (req(&v, "keyType").as_str(), req(&v, "valueType").as_str()) {
            ("string", "i4") => map_line::<String, i32>(&hex),
            ("string", "string") => map_line::<String, String>(&hex),
            ("i4", "i4") => map_line::<i32, i32>(&hex),
            ("u4", "i4") => map_line::<u32, i32>(&hex),
            ("i8", "i4") => map_line::<i64, i32>(&hex),
            ("guid", "i4") => map_line::<uuid::Uuid, i32>(&hex),
            ("bool", "i4") => map_line::<bool, i32>(&hex),
            other => panic!("unsupported map type {other:?}"),
        };
        lines.push(format!("map/{}\t{line}", req(&v, "name")));
    }

    for v in section("set", "vectors") {
        let hex = req(&v, "hex");
        let line = match req(&v, "elementType").as_str() {
            "i4" => set_line::<i32>(&hex),
            "string" => set_line::<String>(&hex),
            "guid" => set_line::<uuid::Uuid>(&hex),
            other => panic!("unsupported set element type '{other}'"),
        };
        lines.push(format!("set/{}\t{line}", req(&v, "name")));
    }

    for v in section("fixedArray", "vectors") {
        let hex = req(&v, "hex");
        let n = v["length"].as_u64().unwrap() as usize;
        let line = match req(&v, "elementType").as_str() {
            "i4" => fixed_line::<i32>(&hex, n),
            "u1" => fixed_line::<u8>(&hex, n),
            "string" => fixed_line::<String>(&hex, n),
            "guid" => fixed_line::<uuid::Uuid>(&hex, n),
            other => panic!("unsupported fixed-array element type '{other}'"),
        };
        lines.push(format!("fixed/{}\t{line}", req(&v, "name")));
    }

    for line in &lines {
        let hex = line.split('\t').nth(1).unwrap();
        assert!(
            !hex.is_empty() && hex.bytes().all(|b| b.is_ascii_hexdigit()),
            "malformed dump line: {line}"
        );
    }

    // `\n` explicitly: the three runtimes must produce byte-identical FILES.
    let dir = format!("{GOLDEN_DIR}.dump");
    std::fs::create_dir_all(&dir).expect("cannot create the dump directory");
    std::fs::write(format!("{dir}/rust.txt"), lines.join("\n") + "\n")
        .expect("cannot write the dump");
}
