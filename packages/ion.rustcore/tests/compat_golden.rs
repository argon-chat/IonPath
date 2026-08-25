//! Cross-runtime backward/forward-compatibility vectors — `/tests/golden/compat.golden.json`.
//!
//! The same file is read by
//!   `src/tests/IonTestClientServer/CompatGoldenTests.cs`      (C#)
//!   `packages/ion.webcore.js/test/compat.golden.test.ts`      (TypeScript)
//!
//! The schemas under test are `ionc`'s own output, vendored at `tests/compat_schemas/mod.rs` —
//! see `src/tests/IonTestClientServer/Compat/README.md`. Nothing here re-implements a formatter,
//! so every read goes through the code a real generated client would run.
//!
//! **Read a vector's `verdict` before its `expect`.** An `expect` block records what this runtime
//! does *today*. Where `verdict` is `defect` or `divergence` the assertion pins wrong behaviour on
//! purpose: fixing the runtime turns the test red and forces the pin to be updated, which is the
//! only way the gap stays visible.
//!
//! **Record mode.** `ION_COMPAT_RECORD=1 cargo test --test compat_golden` writes what this runtime
//! actually did to `/tests/golden/.dump/compat.rust.json` instead of asserting it;
//! `tests/golden/compat.record.py` merges the three runtimes' observations back into the golden
//! file.

mod common;
mod compat_schemas;

use common::{from_hex, golden, to_hex};
use compat_schemas::*;
use ion_rustcore::formatter::{
    read_array, read_fixed_array, read_map, read_maybe, read_set, skip_remaining, write_array,
    write_fixed_array, write_map, write_maybe, write_set, IonFormat,
};
use ion_rustcore::IonError;
use minicbor::{Decoder, Encoder};
use serde_json::{json, Map, Value};
use std::collections::{HashMap, HashSet};
use std::panic::{self, AssertUnwindSafe};

/// A re-encoded value longer than this is summarised rather than spelled out, so one 100000-deep
/// recursion vector cannot add hundreds of kilobytes of hex to a file whose job is to be read.
/// FNV-1a/64 rather than a real digest because all three runtimes have to compute it identically
/// and this crate has no hash dependency.
const DIGEST_ABOVE: usize = 128;

/// Every observation runs on a thread with a 64 MB stack. Two of the vectors are deep recursion
/// probes, and a Rust stack overflow aborts the process rather than unwinding — it would take the
/// whole test binary with it. The large stack is what makes the depth reachable as a *result*
/// rather than as a crash; that the depth limit has to come from the test is itself a finding.
const PROBE_STACK: usize = 64 * 1024 * 1024;

// ═══════════════════════════════════════════════════════════════════════════
//  Observation
// ═══════════════════════════════════════════════════════════════════════════

#[derive(Debug, Clone, PartialEq)]
struct Observation {
    outcome: &'static str,
    reencoded_hex: Option<String>,
    consumed: Option<usize>,
    error: Option<String>,
    message: Option<String>,
}

impl Observation {
    fn to_json(&self) -> Value {
        let mut m = Map::new();
        m.insert("outcome".into(), json!(self.outcome));
        if let Some(v) = &self.reencoded_hex {
            m.insert("reencodedHex".into(), json!(v));
        }
        if let Some(v) = self.consumed {
            m.insert("consumed".into(), json!(v));
        }
        if let Some(v) = &self.error {
            m.insert("error".into(), json!(v));
        }
        if let Some(v) = &self.message {
            m.insert("message".into(), json!(v));
        }
        Value::Object(m)
    }

    fn from_json(v: &Value) -> Observation {
        Observation {
            outcome: match v["outcome"].as_str().unwrap_or("") {
                "ok" => "ok",
                "error" => "error",
                "untyped-error" => "untyped-error",
                "panic" => "panic",
                other => panic!("unknown outcome '{other}' in the golden file"),
            },
            reencoded_hex: v.get("reencodedHex").and_then(|x| x.as_str()).map(str::to_owned),
            consumed: v.get("consumed").and_then(|x| x.as_u64()).map(|x| x as usize),
            error: v.get("error").and_then(|x| x.as_str()).map(str::to_owned),
            message: v.get("message").and_then(|x| x.as_str()).map(str::to_owned),
        }
    }
}

fn summarise(bytes: &[u8]) -> String {
    if bytes.len() <= DIGEST_ABOVE {
        return to_hex(bytes);
    }
    let mut h: u64 = 0xcbf2_9ce4_8422_2325;
    for b in bytes {
        h ^= *b as u64;
        h = h.wrapping_mul(0x100_0000_01b3);
    }
    format!("fnv1a64:{h:016x}:{}", bytes.len())
}

/// The IonError variant name, so the pin does not depend on the message text.
fn variant(e: &IonError) -> String {
    let s = format!("{e:?}");
    s.split(['(', ' ', '{']).next().unwrap_or("IonError").to_owned()
}

/// Reads `bytes` with `read`, then writes the result back with `write`.
///
/// Panics are caught and reported as their own outcome: a decode failure has to be a `Result`,
/// so a panic is never acceptable behaviour, only observed behaviour.
fn probe<V, R, W>(bytes: &[u8], read: R, write: W) -> Observation
where
    R: FnOnce(&mut Decoder<'_>) -> Result<V, IonError>,
    W: FnOnce(&mut Encoder<Vec<u8>>, &V) -> Result<(), IonError>,
{
    let outcome = panic::catch_unwind(AssertUnwindSafe(|| {
        let mut d = Decoder::new(bytes);
        let value = match read(&mut d) {
            Ok(v) => v,
            Err(e) => {
                return Observation {
                    outcome: "error",
                    reencoded_hex: None,
                    consumed: None,
                    error: Some(variant(&e)),
                    message: None,
                }
            }
        };
        let consumed = d.position();
        let mut e = Encoder::new(Vec::new());
        let reencoded = match write(&mut e, &value) {
            // The decode "succeeded" and produced something the same schema cannot write back.
            // That is not a decode failure; the un-writable result is the finding.
            Err(err) => format!("<re-encode failed: {}>", variant(&err)),
            Ok(()) => summarise(&e.into_writer()),
        };
        Observation {
            outcome: "ok",
            reencoded_hex: Some(reencoded),
            consumed: Some(consumed),
            error: None,
            message: None,
        }
    }));

    match outcome {
        Ok(o) => o,
        Err(payload) => {
            let msg = payload
                .downcast_ref::<String>()
                .cloned()
                .or_else(|| payload.downcast_ref::<&str>().map(|s| (*s).to_owned()))
                .unwrap_or_else(|| "<non-string panic payload>".to_owned());
            Observation {
                outcome: "panic",
                reencoded_hex: None,
                consumed: None,
                error: Some("panic".into()),
                message: Some(msg.chars().take(120).collect()),
            }
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  Reader dispatch
// ═══════════════════════════════════════════════════════════════════════════
// One arm per `reader` spelling in the golden file. Each is the call the generator emits for that
// position: a message field goes through the type's own `IonFormat`, a `T[]` field through
// `read_array`, and so on. `Args<...>` is the argument envelope — Rust has no generated server, so
// this reproduces the C#/TypeScript executor prologue that the Rust client writes for.

fn args_probe<V, R, W>(bytes: &[u8], argc: u64, read: R, write: W) -> Observation
where
    R: FnOnce(&mut Decoder<'_>) -> Result<V, IonError>,
    W: FnOnce(&mut Encoder<Vec<u8>>, &V) -> Result<(), IonError>,
{
    probe(
        bytes,
        |d| {
            let len = d.array()?.ok_or(IonError::IndefiniteArray)?;
            let v = read(d)?;
            skip_remaining(d, len, argc)?;
            Ok(v)
        },
        |e, v| {
            e.array(argc)?;
            write(e, v)
        },
    )
}

fn observe(spec: &str, bytes: &[u8]) -> Observation {
    macro_rules! msg {
        ($t:ty) => {
            return probe::<$t, _, _>(bytes, <$t as IonFormat>::ion_read, |e, v| v.ion_write(e))
        };
    }
    macro_rules! arr {
        ($t:ty) => {
            return probe::<Vec<$t>, _, _>(bytes, read_array::<$t>, |e, v| write_array(e, v))
        };
    }
    macro_rules! fixed {
        ($t:ty, $n:expr) => {
            return probe::<Vec<$t>, _, _>(
                bytes,
                |d| read_fixed_array::<$t>(d, $n),
                |e, v| write_fixed_array(e, v, $n),
            )
        };
    }
    macro_rules! map {
        ($k:ty, $v:ty) => {
            return probe::<HashMap<$k, $v>, _, _>(
                bytes,
                read_map::<$k, $v, HashMap<$k, $v>>,
                |e, v| write_map(e, v.iter()),
            )
        };
    }
    macro_rules! set {
        ($t:ty) => {
            return probe::<HashSet<$t>, _, _>(bytes, read_set::<$t, HashSet<$t>>, |e, v| {
                write_set(e, v.iter())
            })
        };
    }
    macro_rules! maybe {
        ($t:ty) => {
            return probe::<Option<$t>, _, _>(bytes, read_maybe::<$t>, |e, v| write_maybe(e, v))
        };
    }
    macro_rules! args1 {
        ($t:ty) => {
            return args_probe::<$t, _, _>(bytes, 1, <$t as IonFormat>::ion_read, |e, v| {
                v.ion_write(e)
            })
        };
    }
    macro_rules! args2 {
        ($a:ty, $b:ty) => {
            return args_probe::<($a, $b), _, _>(
                bytes,
                2,
                |d| Ok((<$a as IonFormat>::ion_read(d)?, <$b as IonFormat>::ion_read(d)?)),
                |e, v| {
                    v.0.ion_write(e)?;
                    v.1.ion_write(e)
                },
            )
        };
    }

    match spec {
        "AppendedV1" => msg!(AppendedV1),
        "AppendedV2" => msg!(AppendedV2),
        "ShiftedV1" => msg!(ShiftedV1),
        "ShiftedV2" => msg!(ShiftedV2),
        "SwapV1" => msg!(SwapV1),
        "SwapV2" => msg!(SwapV2),
        "InsertedV1" => msg!(InsertedV1),
        "InsertedV2" => msg!(InsertedV2),
        "RemovedV1" => msg!(RemovedV1),
        "RemovedV2" => msg!(RemovedV2),
        "ReorderedV1" => msg!(ReorderedV1),
        "ReorderedV2" => msg!(ReorderedV2),
        "RetypedV1" => msg!(RetypedV1),
        "RetypedV2" => msg!(RetypedV2),
        "WidenedV1" => msg!(WidenedV1),
        "WidenedV2" => msg!(WidenedV2),
        "OptAddedV1" => msg!(OptAddedV1),
        "OptAddedV2" => msg!(OptAddedV2),
        "OptTightenedV1" => msg!(OptTightenedV1),
        "OptTightenedV2" => msg!(OptTightenedV2),
        "VarToFixedV1" => msg!(VarToFixedV1),
        "VarToFixedV2" => msg!(VarToFixedV2),
        "EnumHolderV1" => msg!(EnumHolderV1),
        "EnumHolderV2" => msg!(EnumHolderV2),
        "UnionHolderV1" => msg!(UnionHolderV1),
        "UnionHolderV2" => msg!(UnionHolderV2),
        "GrowHolderV1" => msg!(GrowHolderV1),
        "GrowHolderV2" => msg!(GrowHolderV2),
        "SetHolderV1" => msg!(SetHolderV1),
        "SetHolderV2" => msg!(SetHolderV2),
        "NestV1" => msg!(NestV1),
        "NestV2" => msg!(NestV2),
        "TreeV1" => msg!(TreeV1),

        "Array<AppendedV1>" => arr!(AppendedV1),
        "Array<AppendedV2>" => arr!(AppendedV2),
        "Array<ShiftedV1>" => arr!(ShiftedV1),
        "Array<TierV1>" => arr!(TierV1),
        "Array<i4>" => arr!(i32),

        "Fixed<AppendedV1,2>" => fixed!(AppendedV1, 2),
        "Fixed<AppendedV2,2>" => fixed!(AppendedV2, 2),
        "Fixed<ShiftedV1,2>" => fixed!(ShiftedV1, 2),
        "Fixed<i4,3>" => fixed!(i32, 3),

        "Map<string,AppendedV1>" => map!(String, AppendedV1),
        "Map<string,AppendedV2>" => map!(String, AppendedV2),
        "Map<string,ShiftedV1>" => map!(String, ShiftedV1),
        "Map<string,i4>" => map!(String, i32),

        "Set<TierV1>" => set!(TierV1),
        "Set<TierV2>" => set!(TierV2),

        "Maybe<AppendedV1>" => maybe!(AppendedV1),
        "Maybe<AppendedV2>" => maybe!(AppendedV2),

        "Union<ShapeV1>" => msg!(ShapeV1),
        "Union<ShapeV2>" => msg!(ShapeV2),
        "Union<GrowV1>" => msg!(GrowV1),
        "Union<GrowV2>" => msg!(GrowV2),

        "Args<AppendedV1>" => args1!(AppendedV1),
        "Args<AppendedV2>" => args1!(AppendedV2),
        "Args<ShiftedV1>" => args1!(ShiftedV1),
        "Args<AppendedV2,i4>" => args2!(AppendedV2, i32),
        "Args<NestV1,i4>" => args2!(NestV1, i32),
        "Args<NestV2,i4>" => args2!(NestV2, i32),

        other => panic!("unknown reader spec '{other}'"),
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  Driver
// ═══════════════════════════════════════════════════════════════════════════

fn vector_hex(v: &Value) -> String {
    if let Some(h) = v.get("hex").and_then(|x| x.as_str()) {
        return h.to_owned();
    }
    let b = &v["build"];
    let count = b["count"].as_u64().unwrap() as usize;
    let mut s = String::from(b["prefix"].as_str().unwrap());
    let repeat = b["repeat"].as_str().unwrap();
    s.reserve(repeat.len() * count);
    for _ in 0..count {
        s.push_str(repeat);
    }
    s.push_str(b["suffix"].as_str().unwrap());
    s
}

fn run_all() -> (Vec<(String, Observation, Option<Observation>, String)>, usize) {
    let g = golden("compat.golden.json");
    let trailer = g["trailerHex"].as_str().unwrap().to_owned();
    let mut out = Vec::new();
    let mut total = 0usize;

    for (section, with_trailer) in [("evolution", true), ("malformed", false)] {
        for v in g[section].as_array().unwrap() {
            total += 1;
            let name = v["name"].as_str().unwrap().to_owned();
            let verdict = v.get("verdict").and_then(|x| x.as_str()).unwrap_or("?").to_owned();
            let mut hex = vector_hex(v);
            if with_trailer {
                hex.push_str(&trailer);
            }
            let bytes = from_hex(&hex);
            let actual = observe(v["reader"].as_str().unwrap(), &bytes);
            let want = v["expect"].get("rust").map(Observation::from_json);
            out.push((name, actual, want, verdict));
        }
    }
    (out, total)
}

/// One test, not one per vector: every observation has to run on the big-stack worker thread, and
/// spawning 140 of them per `cargo test` shard buys nothing. Failures are collected and reported
/// together so one run names every divergence rather than the first.
#[test]
fn compat_vectors() {
    let record = std::env::var("ION_COMPAT_RECORD").as_deref() == Ok("1");

    // The deep-recursion vectors are expected to panic in at least one runtime; the default hook
    // would print a backtrace for each one and bury the actual result.
    let previous = panic::take_hook();
    panic::set_hook(Box::new(|_| {}));

    let handle = std::thread::Builder::new()
        .stack_size(PROBE_STACK)
        .spawn(run_all)
        .expect("spawn probe thread");
    let (results, total) = handle.join().expect("probe thread panicked");

    panic::set_hook(previous);

    if record {
        let mut m = Map::new();
        for (name, actual, _, _) in &results {
            m.insert(name.clone(), actual.to_json());
        }
        let dir = format!("{}.dump", common::GOLDEN_DIR);
        std::fs::create_dir_all(&dir).expect("create .dump");
        std::fs::write(
            format!("{dir}/compat.rust.json"),
            format!("{}\n", serde_json::to_string_pretty(&Value::Object(m)).unwrap()),
        )
        .expect("write recording");
        assert_eq!(results.len(), total);
        return;
    }

    let mut failures = Vec::new();
    for (name, actual, want, verdict) in results {
        match want {
            None => failures.push(format!(
                "{name}: no recorded Rust expectation; re-run with ION_COMPAT_RECORD=1"
            )),
            Some(want) if want != actual => failures.push(format!(
                "{name} [{verdict}]\n     expected {:?}\n     actual   {:?}",
                want.to_json(),
                actual.to_json()
            )),
            Some(_) => {}
        }
    }
    assert!(
        failures.is_empty(),
        "{} of {total} compat vectors diverged from the pinned behaviour:\n  {}",
        failures.len(),
        failures.join("\n  ")
    );
}
