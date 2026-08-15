import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  CborReader,
  CborWriter,
  IonDateTime,
  IonDecimal,
  IonFormatterStorage,
} from "../src";
import "../src/index";

/**
 * Emits this runtime's half of the cross-runtime byte-equality proof.
 *
 * Asserting each runtime against the golden JSON already implies the three agree. This goes one
 * step further and writes out what each runtime's *real formatters actually produced*, so the
 * claim can be checked by literally diffing three files rather than by trusting three separate
 * assertion suites:
 *
 *   tests/golden/.dump/cs.txt     <- src/tests/IonTestClientServer/WireDumpTests.cs
 *   tests/golden/.dump/ts.txt     <- this test
 *   tests/golden/.dump/rust.txt   <- packages/ion.rustcore/tests/wiredump.rs
 *   diff cs.txt ts.txt && diff cs.txt rust.txt
 *
 * Nothing here is copied from a golden file's `hex` field — every line is produced by encoding a
 * value through the registered formatters. Format: `section/name TAB hex`, in golden-file order.
 */

const goldenDir = fileURLToPath(new URL("../../../tests/golden/", import.meta.url));
const load = (name: string) => JSON.parse(readFileSync(goldenDir + name, "utf8"));

const toHex = (bytes: Uint8Array) =>
  Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");

const fromHex = (hex: string) =>
  new Uint8Array((hex.match(/../g) ?? []).map((b) => Number.parseInt(b, 16)));

const write = (fn: (w: CborWriter) => void) => {
  const w = new CborWriter();
  fn(w);
  return toHex(w.data);
};

const scalar = (type: string, v: unknown): any => {
  switch (type) {
    case "i8":
    case "u8":
      return BigInt(v as string | number);
    case "i1":
    case "i2":
    case "i4":
    case "u1":
    case "u2":
    case "u4":
      return typeof v === "string" ? Number(v) : (v as number);
    default:
      return v;
  }
};

describe("cross-runtime wire dump", () => {
  it("emits ts.txt", () => {
    const lines: string[] = [];

    // ── float: the precedent this whole exercise follows ──
    lines.push(`float/f2-1.5\t${write((w) => IonFormatterStorage.get<number>("f2").write(w, 1.5))}`);
    lines.push(`float/f4-1.5\t${write((w) => IonFormatterStorage.get<number>("f4").write(w, 1.5))}`);
    lines.push(`float/f8-1.5\t${write((w) => IonFormatterStorage.get<number>("f8").write(w, 1.5))}`);
    lines.push(`float/f4-nan\t${write((w) => IonFormatterStorage.get<number>("f4").write(w, NaN))}`);

    // ── datetime ──
    const datetime = load("datetime.golden.json");
    const dtFormatter = IonFormatterStorage.get<IonDateTime>("datetime");
    for (const v of datetime.vectors) {
      const value = new IonDateTime(BigInt(v.unixTicks), v.offsetMinutes);
      lines.push(`datetime/${v.name}\t${write((w) => dtFormatter.write(w, value))}`);
    }

    // ── decimal ── built from the AUTHORED parts; the formatter canonicalises
    const dec = load("decimal.golden.json");
    const decFormatter = IonFormatterStorage.get<IonDecimal>("decimal");
    for (const v of dec.vectors) {
      const value = new IonDecimal(v.exponent, BigInt(v.mantissa));
      lines.push(`decimal/${v.name}\t${write((w) => decFormatter.write(w, value))}`);
    }

    // ── map / set / fixed array ──
    // Round-tripped through the real formatters: read the pinned bytes, write them back. Because
    // the writer sorts, the output is the encoder's own opinion of canonical order.
    const coll = load("collections.golden.json");

    for (const v of coll.map.vectors) {
      const map = IonFormatterStorage.readMap(new CborReader(fromHex(v.hex)), v.keyType, v.valueType);
      lines.push(
        `map/${v.name}\t${write((w) => IonFormatterStorage.writeMap(w, map, v.keyType, v.valueType))}`
      );
    }

    for (const v of coll.set.vectors) {
      const set = IonFormatterStorage.readSet(new CborReader(fromHex(v.hex)), v.elementType);
      lines.push(`set/${v.name}\t${write((w) => IonFormatterStorage.writeSet(w, set, v.elementType))}`);
    }

    for (const v of coll.fixedArray.vectors) {
      const array = IonFormatterStorage.readFixedArray(
        new CborReader(fromHex(v.hex)),
        v.elementType,
        v.length
      );
      lines.push(
        `fixed/${v.name}\t${write((w) =>
          IonFormatterStorage.writeFixedArray(w, array, v.elementType, v.length)
        )}`
      );
    }

    // `\n` explicitly: the three runtimes must produce byte-identical FILES.
    mkdirSync(goldenDir + ".dump", { recursive: true });
    writeFileSync(goldenDir + ".dump/ts.txt", lines.join("\n") + "\n");

    expect(lines.length).toBeGreaterThan(0);
    // Guard against a silently empty scalar conversion producing a blank hex column.
    for (const line of lines) expect(line.split("\t")[1], line).toMatch(/^[0-9a-f]+$/);

    // Keep `scalar` referenced even though the container dump round-trips: it documents the
    // JSON -> JS mapping the sibling suites use, and a drift here would be silent otherwise.
    expect(scalar("i8", "1099511627776")).toEqual(1099511627776n);
  });
});
