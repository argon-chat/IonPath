import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  CborReader,
  CborWriter,
  IonDecodeError,
  IonFormatterStorage,
} from "../src";
import "../src/index";
import "./compat/compat.generated";

// ─── shared cross-runtime compatibility vectors ─────────────────────────────
// /tests/golden/compat.golden.json is also consumed by
//   src/tests/IonTestClientServer/CompatGoldenTests.cs   (C#)
//   packages/ion.rustcore/tests/compat_golden.rs         (Rust)
//
// The schemas under test are `ionc`'s own output, vendored at ./compat/compat.generated.ts —
// see src/tests/IonTestClientServer/Compat/README.md. Nothing here re-implements a formatter;
// every read goes through the same generated code a real client would run.
//
// RECORD MODE. `ION_COMPAT_RECORD=1 bun run test` writes what this runtime actually did to
// /tests/golden/.dump/compat.ts.json instead of asserting it. That file is the input to
// tests/golden/compat.record.py, which merges the three runtimes' observations back into the
// `expect` blocks of compat.golden.json. That is how the pins get established: observe first,
// then decide per vector whether the observation is correct behaviour or a defect.

interface Expectation {
  outcome: "ok" | "error" | "untyped-error" | "panic";
  reencodedHex?: string;
  consumed?: number;
  error?: string;
  message?: string;
}

interface Vector {
  name: string;
  reader: string;
  hex?: string;
  build?: { prefix: string; repeat: string; count: number; suffix: string };
  notes?: string;
  verdict?: string;
  shouldBe?: string;
  expect: Record<string, Expectation>;
}

const goldenPath = fileURLToPath(
  new URL("../../../tests/golden/compat.golden.json", import.meta.url)
);
const golden = JSON.parse(readFileSync(goldenPath, "utf8")) as {
  trailerHex: string;
  evolution: Vector[];
  malformed: Vector[];
};

const RECORD = process.env.ION_COMPAT_RECORD === "1";
const recorded: Record<string, Expectation> = {};

const toHex = (bytes: Uint8Array) =>
  Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");

const fromHex = (hex: string) =>
  new Uint8Array((hex.match(/../g) ?? []).map((b) => Number.parseInt(b, 16)));

const vectorHex = (v: Vector): string =>
  v.hex !== undefined
    ? v.hex
    : v.build!.prefix + v.build!.repeat.repeat(v.build!.count) + v.build!.suffix;

/**
 * A re-encoded value longer than this is summarised rather than spelled out, so one 100000-deep
 * recursion vector cannot add 600 KB of hex to a file whose job is to be read. FNV-1a/64 is used
 * rather than a real digest because all three runtimes have to compute it identically and Rust's
 * test crate has no hash dependency.
 */
const DIGEST_ABOVE = 128;

const summarise = (bytes: Uint8Array): string => {
  if (bytes.length <= DIGEST_ABOVE) return toHex(bytes);
  const mask = (1n << 64n) - 1n;
  let h = 0xcbf29ce484222325n;
  for (const b of bytes) {
    h = (h ^ BigInt(b)) & mask;
    h = (h * 0x100000001b3n) & mask;
  }
  return `fnv1a64:${h.toString(16).padStart(16, "0")}:${bytes.length}`;
};

// ═══════════════════════════════════════════════════════════════════════════
//  Reader dispatch
// ═══════════════════════════════════════════════════════════════════════════
// One entry per `reader` spelling in the golden file. Each is exactly the call the generator
// emits for that position — a message field reads through `IonFormatterStorage.get(name)`, a
// `T[]` field through `readArray`, a method-argument envelope through the executor prologue.

interface Codec {
  read(reader: CborReader): unknown;
  write(writer: CborWriter, value: unknown): void;
}

const generic = (spec: string): [string, string[]] | null => {
  const m = /^([A-Za-z]+)<(.+)>$/.exec(spec);
  if (!m) return null;
  return [m[1], m[2].split(",").map((s) => s.trim())];
};

const codecFor = (spec: string): Codec => {
  const g = generic(spec);
  if (g === null) {
    // A plain message / enum name: the generated formatter, read at the root.
    const f = IonFormatterStorage.get<any>(spec);
    return { read: (r) => f.read(r), write: (w, v) => f.write(w, v as any) };
  }
  const [kind, args] = g;
  switch (kind) {
    case "Array":
      return {
        read: (r) => IonFormatterStorage.readArray<any>(r, args[0]),
        write: (w, v) => IonFormatterStorage.writeArray<any>(w, v as any[], args[0]),
      };
    case "Fixed": {
      const n = Number(args[1]);
      return {
        read: (r) => IonFormatterStorage.readFixedArray<any>(r, args[0], n),
        write: (w, v) => IonFormatterStorage.writeFixedArray<any>(w, v as any[], args[0], n),
      };
    }
    case "Map":
      return {
        read: (r) => IonFormatterStorage.readMap<any, any>(r, args[0], args[1]),
        write: (w, v) => IonFormatterStorage.writeMap<any, any>(w, v as Map<any, any>, args[0], args[1]),
      };
    case "Set":
      return {
        read: (r) => IonFormatterStorage.readSet<any>(r, args[0]),
        write: (w, v) => IonFormatterStorage.writeSet<any>(w, v as Set<any>, args[0]),
      };
    case "Maybe":
      return {
        read: (r) => IonFormatterStorage.readNullable<any>(r, args[0]),
        write: (w, v) => IonFormatterStorage.writeNullable<any>(w, v as any, args[0]),
      };
    case "Union": {
      // `union U` generates the interface `IU`, and that is the registered formatter name.
      const f = IonFormatterStorage.get<any>("I" + args[0]);
      return { read: (r) => f.read(r), write: (w, v) => f.write(w, v as any) };
    }
    case "Args":
      // The generated executor prologue, verbatim:
      //   var arraySize = reader.ReadStartArray() ?? throw ...
      //   <one read per declared argument>
      //   reader.ReadEndArrayAndSkip(arraySize - argumentSize);
      return {
        read: (r) => {
          const arraySize = r.readStartArray();
          if (arraySize === null) throw new Error("undefined len array not allowed");
          const values = args.map((t) => IonFormatterStorage.get<any>(t).read(r));
          r.readEndArrayAndSkip(arraySize - args.length);
          return values;
        },
        write: (w, v) => {
          const values = v as unknown[];
          w.writeStartArray(args.length);
          args.forEach((t, i) => IonFormatterStorage.get<any>(t).write(w, values[i] as any));
          w.writeEndArray();
        },
      };
    default:
      throw new Error(`unknown reader spec '${spec}'`);
  }
};

const observe = (v: Vector, withTrailer: boolean): Expectation => {
  const bytes = fromHex(vectorHex(v) + (withTrailer ? golden.trailerHex : ""));
  const codec = codecFor(v.reader);
  const reader = new CborReader(bytes);
  let value: unknown;
  try {
    value = codec.read(reader);
  } catch (e) {
    const err = e as Error;
    if (err instanceof IonDecodeError)
      return { outcome: "error", error: err.name };
    return {
      outcome: "untyped-error",
      error: err?.constructor?.name ?? typeof e,
      message: String(err?.message ?? e).slice(0, 120),
    };
  }
  const consumed = reader.position;
  let reencodedHex: string;
  try {
    const w = new CborWriter();
    codec.write(w, value);
    reencodedHex = summarise(w.data);
  } catch (e) {
    // Decoding produced something the same schema cannot write back. That is still a decode
    // that "succeeded", and the un-writable result is the finding.
    reencodedHex = `<re-encode failed: ${(e as Error)?.constructor?.name}>`;
  }
  return { outcome: "ok", reencodedHex, consumed };
};

const check = (v: Vector, withTrailer: boolean) => {
  const actual = observe(v, withTrailer);
  if (RECORD) {
    recorded[v.name] = actual;
    return;
  }
  const want = v.expect?.ts;
  expect(want, `no recorded TS expectation for vector '${v.name}'`).toBeDefined();
  expect(actual, v.name).toEqual(want);
};

describe("compat: schema evolution", () => {
  for (const v of golden.evolution) {
    it(`${v.name} [${v.verdict ?? "?"}]`, () => check(v, true));
  }
});

describe("compat: malformed and hostile input", () => {
  for (const v of golden.malformed) {
    it(`${v.name} [${v.verdict ?? "?"}]`, () => check(v, false));
  }
});

describe("compat: record", () => {
  it("writes the observation file when ION_COMPAT_RECORD=1", () => {
    if (!RECORD) return;
    const out = fileURLToPath(
      new URL("../../../tests/golden/.dump/compat.ts.json", import.meta.url)
    );
    mkdirSync(dirname(out), { recursive: true });
    writeFileSync(out, `${JSON.stringify(recorded, null, 2)}\n`, "utf8");
    expect(Object.keys(recorded).length).toBe(
      golden.evolution.length + golden.malformed.length
    );
  });
});
