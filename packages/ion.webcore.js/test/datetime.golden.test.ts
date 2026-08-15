import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  CborReader,
  CborWriter,
  IonDateTime,
  IonDateTimeFormatError,
  IonDecodeError,
  IonFormatterStorage,
  IonMalformedValueError,
  IonUnexpectedTagError,
} from "../src";
import "../src/index";

// ─── shared cross-runtime golden vectors ────────────────────────────────────
// /tests/golden/datetime.golden.json is also consumed by
//   src/tests/IonTestClientServer/DateTimeGoldenTests.cs   (C#)
//   packages/ion.rustcore/tests/datetime_golden.rs         (Rust)
//
// This is a WIRE-FORMAT CHANGE and a correction: this runtime used to write
// `value.date.toISOString()`, i.e. millisecond resolution, silently truncating the 100ns ticks a
// C# peer routinely sends. C# discarded the offset on read, and Rust wrote a completely different
// shape (a bare `[ticks, offsetMinutes]` array) and could not interoperate at all.

interface DateTimeVector {
  name: string;
  iso: string;
  /** Signed 100ns intervals since 1970-01-01T00:00:00Z, as a string so no precision is lost. */
  unixTicks: string;
  offsetMinutes: number;
  hex: string;
  notes?: string;
}

interface DecodeOnlyVector {
  name: string;
  hex: string;
  reencodedHex: string;
  notes?: string;
}

interface MalformedVector {
  name: string;
  hex: string;
  notes?: string;
}

const goldenPath = fileURLToPath(
  new URL("../../../tests/golden/datetime.golden.json", import.meta.url)
);
const golden = JSON.parse(readFileSync(goldenPath, "utf8")) as {
  vectors: DateTimeVector[];
  decodeOnly: DecodeOnlyVector[];
  malformed: MalformedVector[];
};

const toHex = (bytes: Uint8Array) =>
  Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");

const fromHex = (hex: string) =>
  new Uint8Array((hex.match(/../g) ?? []).map((b) => Number.parseInt(b, 16)));

const formatter = () => IonFormatterStorage.get<IonDateTime>("datetime");

export const encode = (value: IonDateTime) => {
  const writer = new CborWriter();
  formatter().write(writer, value);
  return toHex(writer.data);
};

const decode = (hex: string) => formatter().read(new CborReader(fromHex(hex)));

const reencode = (hex: string) => encode(decode(hex));

/**
 * Builds the value from `unixTicks` + `offsetMinutes` — the same two numbers the C# and Rust
 * harnesses start from, so all three construct the instant independently rather than by
 * re-parsing the expected text.
 */
export const build = (v: DateTimeVector) =>
  new IonDateTime(BigInt(v.unixTicks), v.offsetMinutes);

describe("datetime golden vectors", () => {
  for (const v of golden.vectors) {
    it(`encodes '${v.name}' (${v.iso})`, () => {
      expect(encode(build(v)), v.notes).toEqual(v.hex);
    });

    it(`decodes '${v.name}' (${v.iso})`, () => {
      expect(reencode(v.hex), v.notes).toEqual(v.hex);
    });

    it(`preserves the instant AND the offset of '${v.name}'`, () => {
      // The offset is part of the value, not decoration. C#'s old reader was
      // ReadDateTimeOffset().UtcDateTime, which parsed it and threw it away.
      const decoded = decode(v.hex);
      expect(decoded.unixTicks).toEqual(BigInt(v.unixTicks));
      expect(decoded.offsetMinutes).toEqual(v.offsetMinutes);
      expect(decoded.toString()).toEqual(v.iso);
    });

    it(`writes '${v.name}' as exactly 36 bytes`, () => {
      // Tag 0 + a 33-character text string: c0 78 21 + 33.
      expect(v.iso).toHaveLength(33);
      expect(v.hex).toHaveLength(36 * 2);
      expect(v.hex.startsWith("c07821")).toBe(true);
      expect(encode(build(v))).toHaveLength(36 * 2);
    });
  }

  for (const v of golden.decodeOnly) {
    it(`reads leniently: '${v.name}'`, () => {
      expect(reencode(v.hex), v.notes).toEqual(v.reencodedHex);
    });
  }

  for (const v of golden.malformed) {
    it(`rejects '${v.name}' with a TYPED error`, () => {
      // Never an opaque throw: a caller must be able to branch on the failure without
      // string-matching a message.
      expect(() => decode(v.hex), v.notes).toThrow(IonDecodeError);
    });
  }

  it("malformed payloads get specifically-typed errors", () => {
    const byName = (n: string) => golden.malformed.find((v) => v.name === n)!.hex;
    expect(() => decode(byName("wrong-tag"))).toThrow(IonUnexpectedTagError);
    expect(() => decode(byName("missing-tag"))).toThrow(IonMalformedValueError);
    expect(() => decode(byName("not-a-date"))).toThrow(IonDateTimeFormatError);
    expect(() => decode(byName("missing-offset"))).toThrow(IonDateTimeFormatError);
  });
});

describe("IonDateTime", () => {
  // The whole reason this type exists: `Date` cannot hold what the wire carries.
  it("holds sub-millisecond precision a Date cannot", () => {
    const v = golden.vectors.find((x) => x.name === "sub-millisecond")!;
    const value = build(v);

    expect(value.toString()).toEqual("2024-03-01T12:34:56.7891234+00:00");
    expect(value.fraction).toEqual(7_891_234);

    // Round-tripping through a Date loses exactly the digits the old formatter lost.
    const viaDate = IonDateTime.fromDate(value.toDate(), value.offsetMinutes);
    expect(viaDate.toString()).toEqual("2024-03-01T12:34:56.7890000+00:00");
    expect(viaDate.unixTicks).not.toEqual(value.unixTicks);
    expect(value.unixTicks - viaDate.unixTicks).toEqual(1234n);
  });

  it("proves the old encoding was lossy (Date.toISOString has 3 fractional digits)", () => {
    const value = build(golden.vectors.find((x) => x.name === "sub-millisecond")!);
    expect(value.toDate().toISOString()).toEqual("2024-03-01T12:34:56.789Z");
    expect(value.toDate().toISOString()).not.toContain("7891234");
  });

  it("keeps the offset distinct from the instant", () => {
    const utc = build(golden.vectors.find((x) => x.name === "sub-millisecond")!);
    const shifted = utc.toOffset(330);

    expect(shifted.unixTicks).toEqual(utc.unixTicks); // same instant
    expect(shifted.toString()).toEqual("2024-03-01T18:04:56.7891234+05:30"); // different reading
    expect(shifted.equals(utc)).toBe(false);
  });

  it("round-trips through fromString for every vector", () => {
    for (const v of golden.vectors) {
      const parsed = IonDateTime.fromString(v.iso);
      expect(parsed.unixTicks, v.name).toEqual(BigInt(v.unixTicks));
      expect(parsed.offsetMinutes, v.name).toEqual(v.offsetMinutes);
      expect(parsed.toString(), v.name).toEqual(v.iso);
    }
  });

  it("truncates excess fractional digits instead of rounding", () => {
    // .9999999|9 would round up into the next second; it must not.
    expect(IonDateTime.fromString("2024-03-01T12:34:56.99999999+00:00").toString()).toEqual(
      "2024-03-01T12:34:56.9999999+00:00"
    );
    expect(IonDateTime.fromString("2024-03-01T12:34:56.789123456+00:00").fraction).toEqual(7_891_234);
  });

  it("handles negative unixTicks (before the epoch) with floor semantics", () => {
    const v = golden.vectors.find((x) => x.name === "epoch-minus-one-tick")!;
    const value = build(v);
    expect(value.unixTicks).toEqual(-1n);
    expect(value.toString()).toEqual("1969-12-31T23:59:59.9999999+00:00");
    expect(value.fraction).toEqual(9_999_999);
  });

  it("never writes 'Z'", () => {
    for (const v of golden.vectors) expect(v.iso.endsWith("Z"), v.name).toBe(false);
    expect(IonDateTime.epoch.toString()).toEqual("1970-01-01T00:00:00.0000000+00:00");
  });

  it("rejects an offset beyond ±14:00", () => {
    expect(() => new IonDateTime(0n, 841)).toThrow(RangeError);
    expect(() => new IonDateTime(0n, -841)).toThrow(RangeError);
    expect(new IonDateTime(0n, 840).offsetMinutes).toEqual(840);
  });

  it("nests in containers as exactly one item", () => {
    const v = golden.vectors.find((x) => x.name === "epoch-utc")!;
    const writer = new CborWriter();
    writer.writeStartArray(2);
    formatter().write(writer, build(v));
    writer.writeInt32(7);
    writer.writeEndArray();

    expect(toHex(writer.data)).toEqual("82" + v.hex + "07");
  });
});
