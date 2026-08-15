import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { CborReader, CborWriter, IonFormatterStorage } from "../src";
import "../src/index";

// ─── shared cross-runtime golden vectors ────────────────────────────────────
// /tests/golden/float.golden.json is also consumed by
//   src/tests/IonTestClientServer/FloatGoldenTests.cs  (C#)
//   packages/ion.rustcore/tests/float_golden.rs        (Rust)
// All three runtimes must produce byte-identical CBOR for the same value.

type IonFloatType = "f2" | "f4" | "f8";

interface FloatVector {
  name: string;
  type: IonFloatType;
  /** IEEE-754 bit pattern, big-endian hex, `payloadBytes` wide. */
  bits: string;
  repr?: string;
  hex: string;
  notes?: string;
}

interface CrossWidthVector {
  name: string;
  type: IonFloatType;
  hex: string;
  reencodedHex: string;
  notes?: string;
}

const goldenPath = fileURLToPath(
  new URL("../../../tests/golden/float.golden.json", import.meta.url)
);
const golden = JSON.parse(readFileSync(goldenPath, "utf8")) as {
  vectors: FloatVector[];
  crossWidth: CrossWidthVector[];
};

const PAYLOAD_BYTES: Record<IonFloatType, number> = { f2: 2, f4: 4, f8: 8 };

const toHex = (bytes: Uint8Array) =>
  Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");

const fromHex = (hex: string) =>
  new Uint8Array((hex.match(/../g) ?? []).map((b) => Number.parseInt(b, 16)));

const encode = (type: IonFloatType, value: number) => {
  const writer = new CborWriter();
  IonFormatterStorage.get<number>(type).write(writer, value);
  return toHex(writer.data);
};

const decode = (type: IonFloatType, hex: string) =>
  IonFormatterStorage.get<number>(type).read(new CborReader(fromHex(hex)));

/**
 * Builds the JS number for a vector's IEEE bit pattern.
 *
 * f4/f8 go through a DataView, which is independent of the code under test. JS has no native
 * half type, so f2 is materialised by handing the shared CBOR reader an `f9` + payload item —
 * the reader is a separate code path from the writer being asserted, and the expected bytes are
 * pinned in a file that C# and Rust check independently.
 */
const valueFromBits = (type: IonFloatType, bits: string): number => {
  const view = new DataView(new ArrayBuffer(8));
  switch (type) {
    case "f2":
      return new CborReader(fromHex("f9" + bits)).readHalf();
    case "f4":
      view.setUint32(0, Number.parseInt(bits, 16));
      return view.getFloat32(0);
    case "f8":
      view.setBigUint64(0, BigInt("0x" + bits));
      return view.getFloat64(0);
  }
};

describe("float golden vectors", () => {
  it("the golden file is self-consistent (hex === header + bits)", () => {
    const headers: Record<IonFloatType, string> = { f2: "f9", f4: "fa", f8: "fb" };
    for (const v of golden.vectors) {
      expect(v.bits, v.name).toHaveLength(PAYLOAD_BYTES[v.type] * 2);
      expect(v.hex, v.name).toEqual(headers[v.type] + v.bits);
    }
  });

  for (const v of golden.vectors) {
    it(`encodes '${v.name}' (${v.repr})`, () => {
      expect(encode(v.type, valueFromBits(v.type, v.bits)), v.notes).toEqual(v.hex);
    });

    it(`decodes '${v.name}' (${v.repr})`, () => {
      expect(encode(v.type, decode(v.type, v.hex)), v.notes).toEqual(v.hex);
    });

    it(`writes '${v.name}' at the declared width`, () => {
      // The whole point of the rule: an f8 field holding 1.5 must still be 9 wire bytes.
      expect(encode(v.type, valueFromBits(v.type, v.bits))).toHaveLength(
        (1 + PAYLOAD_BYTES[v.type]) * 2
      );
    });
  }

  // Readers accept every wire width for every declared width, in both directions — including
  // the shrunken payloads the previous C# release wrote.
  for (const v of golden.crossWidth) {
    it(`reads cross-width '${v.name}'`, () => {
      expect(encode(v.type, decode(v.type, v.hex)), v.notes).toEqual(v.reencodedHex);
    });
  }
});

describe("float special values", () => {
  // A JS engine does not guarantee a canonical NaN: Math.sqrt(-1) yields a sign-set NaN that
  // survives a Float32Array store as 0xffc00000. Without canonicalisation the runtimes would
  // still disagree on NaN — the one value the width fix alone does not settle.
  it("canonicalises every NaN to the positive quiet NaN", () => {
    for (const nan of [NaN, Math.sqrt(-1), 0 / 0, Infinity - Infinity, -NaN]) {
      expect(encode("f2", nan)).toEqual("f97e00");
      expect(encode("f4", nan)).toEqual("fa7fc00000");
      expect(encode("f8", nan)).toEqual("fb7ff8000000000000");
    }
  });

  it("proves Math.sqrt(-1) really is a sign-set NaN (so the guard above is load-bearing)", () => {
    const view = new DataView(new ArrayBuffer(4));
    view.setFloat32(0, Math.sqrt(-1));
    expect(view.getUint32(0)).toEqual(0xffc00000);
  });

  it("preserves -0 and keeps it distinct from +0", () => {
    expect(encode("f2", -0)).toEqual("f98000");
    expect(encode("f4", -0)).toEqual("fa80000000");
    expect(encode("f8", -0)).toEqual("fb8000000000000000");

    expect(encode("f2", 0)).toEqual("f90000");
    expect(encode("f4", 0)).toEqual("fa00000000");
    expect(encode("f8", 0)).toEqual("fb0000000000000000");

    expect(Object.is(decode("f4", "fa80000000"), -0)).toBe(true);
    expect(Object.is(decode("f8", "fb8000000000000000"), -0)).toBe(true);
  });

  it("round-trips infinities", () => {
    expect(encode("f4", Infinity)).toEqual("fa7f800000");
    expect(encode("f4", -Infinity)).toEqual("faff800000");
    expect(decode("f4", "fa7f800000")).toEqual(Infinity);
    expect(decode("f8", "fbfff0000000000000")).toEqual(-Infinity);
  });

  it("keeps f2 at two payload bytes", () => {
    for (const v of golden.vectors.filter((x) => x.type === "f2")) {
      const hex = encode("f2", valueFromBits("f2", v.bits));
      expect(hex.startsWith("f9"), v.name).toBe(true);
      expect(hex, v.name).toHaveLength(6);
    }
  });

  it("nests in containers as exactly one item each", () => {
    const writer = new CborWriter();
    writer.writeStartArray(3);
    IonFormatterStorage.get<number>("f2").write(writer, 1.5);
    IonFormatterStorage.get<number>("f4").write(writer, 1.5);
    IonFormatterStorage.get<number>("f8").write(writer, 1.5);
    writer.writeEndArray();

    expect(toHex(writer.data)).toEqual("83f93e00fa3fc00000fb3ff8000000000000");
  });
});
