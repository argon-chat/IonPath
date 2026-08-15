import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  CborReader,
  CborWriter,
  IonDecimal,
  IonDecodeError,
  IonFormatterStorage,
  IonMalformedValueError,
  IonUnexpectedTagError,
} from "../src";
import "../src/index";

// ─── shared cross-runtime golden vectors ────────────────────────────────────
// /tests/golden/decimal.golden.json is also consumed by
//   src/tests/IonTestClientServer/DecimalGoldenTests.cs   (C#)
//   packages/ion.rustcore/tests/decimal_golden.rs         (Rust)
//
// TypeScript is the runtime with no range limit: the mantissa is a native bigint, so this suite
// decodes every vector — including the ones C# must reject as outside System.Decimal's range.

interface DecimalVector {
  name: string;
  exponent: number;
  mantissa: string;
  canonicalExponent: number;
  canonicalMantissa: string;
  value: string;
  inCSharpDecimalRange: boolean;
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
  new URL("../../../tests/golden/decimal.golden.json", import.meta.url)
);
const golden = JSON.parse(readFileSync(goldenPath, "utf8")) as {
  vectors: DecimalVector[];
  decodeOnly: DecodeOnlyVector[];
  malformed: MalformedVector[];
};

const toHex = (bytes: Uint8Array) =>
  Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");

const fromHex = (hex: string) =>
  new Uint8Array((hex.match(/../g) ?? []).map((b) => Number.parseInt(b, 16)));

const formatter = () => IonFormatterStorage.get<IonDecimal>("decimal");

export const encode = (value: IonDecimal) => {
  const writer = new CborWriter();
  formatter().write(writer, value);
  return toHex(writer.data);
};

const decode = (hex: string) => formatter().read(new CborReader(fromHex(hex)));

export const build = (v: DecimalVector) => new IonDecimal(v.exponent, BigInt(v.mantissa));

describe("decimal golden vectors", () => {
  for (const v of golden.vectors) {
    it(`encodes '${v.name}' (${v.value})`, () => {
      // Built from the AUTHORED exponent/mantissa; the formatter canonicalises.
      expect(encode(build(v)), v.notes).toEqual(v.hex);
    });

    it(`decodes '${v.name}' (${v.value})`, () => {
      expect(encode(decode(v.hex)), v.notes).toEqual(v.hex);
    });

    it(`decodes '${v.name}' to the declared canonical parts`, () => {
      const decoded = decode(v.hex);
      expect(decoded.exponent, v.name).toEqual(v.canonicalExponent);
      expect(decoded.mantissa, v.name).toEqual(BigInt(v.canonicalMantissa));
    });

    it(`renders '${v.name}' as ${v.value}`, () => {
      expect(decode(v.hex).toString(), v.name).toEqual(
        new IonDecimal(v.canonicalExponent, BigInt(v.canonicalMantissa)).toString()
      );
    });
  }

  // Values C# cannot hold still decode here, exactly. That asymmetry is the point of
  // `inCSharpDecimalRange`: the bytes are identical in all three runtimes, but only C# has to
  // refuse to narrow them.
  for (const v of golden.vectors.filter((x) => !x.inCSharpDecimalRange)) {
    it(`decodes '${v.name}' exactly, where C# must raise a range error`, () => {
      const decoded = decode(v.hex);
      expect(decoded.mantissa).toEqual(BigInt(v.canonicalMantissa));
      expect(decoded.exponent).toEqual(v.canonicalExponent);
    });
  }

  for (const v of golden.decodeOnly) {
    it(`reads leniently: '${v.name}'`, () => {
      expect(encode(decode(v.hex)), v.notes).toEqual(v.reencodedHex);
    });
  }

  for (const v of golden.malformed) {
    it(`rejects '${v.name}' with a TYPED error`, () => {
      expect(() => decode(v.hex), v.notes).toThrow(IonDecodeError);
    });
  }

  it("malformed payloads get specifically-typed errors", () => {
    const byName = (n: string) => golden.malformed.find((v) => v.name === n)!.hex;
    // Tag 5 is bigfloat — the same array shape with a base-2 exponent.
    expect(() => decode(byName("wrong-tag"))).toThrow(IonUnexpectedTagError);
    expect(() => decode(byName("wrong-arity"))).toThrow(IonMalformedValueError);
    expect(() => decode(byName("not-an-array"))).toThrow(IonMalformedValueError);
  });
});

describe("IonDecimal", () => {
  // THE CANONICAL-FORM GUARD. 1.50 and 1.5 are the same number, so they must be the same bytes.
  it("normalises trailing zeros away on the wire", () => {
    expect(encode(IonDecimal.fromString("1.50"))).toEqual(encode(IonDecimal.fromString("1.5")));
    expect(encode(IonDecimal.fromString("1.500000"))).toEqual(encode(IonDecimal.fromString("1.5")));
    expect(encode(IonDecimal.fromString("1.50"))).toEqual("c482200f");

    // …while the in-memory value keeps the authored scale, so toString() is faithful.
    expect(IonDecimal.fromString("1.50").toString()).toEqual("1.50");
    expect(IonDecimal.fromString("1.5").toString()).toEqual("1.5");
    expect(IonDecimal.fromString("1.50").isIdenticalTo(IonDecimal.fromString("1.5"))).toBe(false);
    expect(IonDecimal.fromString("1.50").equals(IonDecimal.fromString("1.5"))).toBe(true);
  });

  it("collapses every zero into [0, 0]", () => {
    for (const text of ["0", "0.00", "-0", "-0.000", "0E+5"]) {
      expect(encode(IonDecimal.fromString(text)), text).toEqual("c4820000");
    }
  });

  it("is exact where a number is not — which is the whole reason it is not a number", () => {
    expect(0.1 + 0.2).not.toEqual(0.3);

    const a = IonDecimal.fromString("0.1");
    const b = IonDecimal.fromString("0.2");
    // Same scale, so adding the mantissas is exact.
    expect(new IonDecimal(-1, a.mantissa + b.mantissa).equals(IonDecimal.fromString("0.3"))).toBe(true);

    const pi = "3.1415926535897932384626433832";
    expect(IonDecimal.fromString(pi).toString()).toEqual(pi);
    expect(decode(encode(IonDecimal.fromString(pi))).toString()).toEqual(pi);
  });

  it("round-trips fromString/toString for every vector's canonical form", () => {
    for (const v of golden.vectors) {
      const canonical = new IonDecimal(v.canonicalExponent, BigInt(v.canonicalMantissa));
      const reparsed = IonDecimal.fromString(canonical.toString());
      expect(reparsed.equals(canonical), `${v.name} (${canonical.toString()})`).toBe(true);
    }
  });

  it("parses scientific notation", () => {
    expect(IonDecimal.fromString("1e-28").equals(new IonDecimal(-28, 1n))).toBe(true);
    expect(IonDecimal.fromString("+3.14E+2").equals(new IonDecimal(0, 314n))).toBe(true);
    expect(IonDecimal.fromString("-1.5").equals(new IonDecimal(-1, -15n))).toBe(true);
  });

  it("rejects non-numbers", () => {
    for (const bad of ["", "abc", "1.2.3", "--1", "1e", "0x10"]) {
      expect(() => IonDecimal.fromString(bad), bad).toThrow(RangeError);
    }
  });

  // The mantissa is a plain CBOR integer across the whole i64/u64 window and a bignum only
  // beyond it — the same boundary C# and Rust draw.
  it("switches to a bignum mantissa exactly at the i64/u64 boundary", () => {
    expect(encode(new IonDecimal(0, -(2n ** 63n)))).toMatch(/^c482003b/); // plain negative int
    expect(encode(new IonDecimal(0, 2n ** 64n - 1n))).toMatch(/^c482001b/); // plain unsigned int
    expect(encode(new IonDecimal(0, 2n ** 64n))).toMatch(/^c48200c2/); // tag 2 bignum
    expect(encode(new IonDecimal(0, -(2n ** 63n) - 1n))).toMatch(/^c48200c3/); // tag 3 bignum
  });

  it("nests in containers as exactly one item", () => {
    const writer = new CborWriter();
    writer.writeStartArray(2);
    formatter().write(writer, IonDecimal.fromString("1.5"));
    writer.writeInt32(7);
    writer.writeEndArray();

    expect(toHex(writer.data)).toEqual("82c482200f07");
  });
});
