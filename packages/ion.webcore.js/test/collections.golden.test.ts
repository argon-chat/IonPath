import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  CborReader,
  CborWriter,
  IonDecodeError,
  IonDuplicateMapKeyError,
  IonDuplicateSetElementError,
  IonFixedArrayLengthError,
  IonFormatterStorage,
  IonMalformedValueError,
  IonUnexpectedTagError,
  ionCanonicalCborCompare,
} from "../src";
import "../src/index";

// ─── shared cross-runtime golden vectors ────────────────────────────────────
// /tests/golden/collections.golden.json is also consumed by
//   src/tests/IonTestClientServer/CollectionGoldenTests.cs   (C#)
//   packages/ion.rustcore/tests/collections_golden.rs        (Rust)

interface MapVector {
  name: string;
  keyType: string;
  valueType: string;
  entries: Array<{ key: unknown; value: unknown }>;
  canonicalKeyOrder: string[];
  hex: string;
  notes?: string;
}

interface MapDecodeVector {
  name: string;
  keyType: string;
  valueType: string;
  hex: string;
  reencodedHex?: string;
  notes?: string;
}

interface SetVector {
  name: string;
  elementType: string;
  elements: unknown[];
  hex: string;
  notes?: string;
}

interface SetDecodeVector {
  name: string;
  elementType: string;
  hex: string;
  reencodedHex?: string;
  notes?: string;
}

interface FixedVector {
  name: string;
  elementType: string;
  length: number;
  elements: unknown[];
  hex: string;
  notes?: string;
}

interface FixedDecodeVector {
  name: string;
  elementType: string;
  length: number;
  hex: string;
  reencodedHex?: string;
  actualLength?: number;
  notes?: string;
}

const goldenPath = fileURLToPath(
  new URL("../../../tests/golden/collections.golden.json", import.meta.url)
);
const golden = JSON.parse(readFileSync(goldenPath, "utf8")) as {
  map: { vectors: MapVector[]; decodeOnly: MapDecodeVector[]; malformed: MapDecodeVector[] };
  set: { vectors: SetVector[]; decodeOnly: SetDecodeVector[]; malformed: SetDecodeVector[] };
  fixedArray: {
    vectors: FixedVector[];
    decodeOnly: FixedDecodeVector[];
    malformed: FixedDecodeVector[];
  };
};

const toHex = (bytes: Uint8Array) =>
  Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");

const fromHex = (hex: string) =>
  new Uint8Array((hex.match(/../g) ?? []).map((b) => Number.parseInt(b, 16)));

/**
 * Converts a JSON scalar to the JS value the Ion type name maps to. Wide integers appear as JSON
 * strings in the golden file so no consumer's JSON parser loses precision, and `i8` is a bigint
 * in this runtime.
 */
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
      return v; // string, guid (a string), bool
  }
};

// ═══════════════════════════════════════════════════════════════════════════
//  Map<K,V>
// ═══════════════════════════════════════════════════════════════════════════

export const encodeMap = (v: MapVector, reverse = false) => {
  const entries = reverse ? [...v.entries].reverse() : v.entries;
  const map = new Map<any, any>();
  for (const e of entries) map.set(scalar(v.keyType, e.key), scalar(v.valueType, e.value));

  const writer = new CborWriter();
  IonFormatterStorage.writeMap(writer, map, v.keyType, v.valueType);
  return toHex(writer.data);
};

const reencodeMap = (keyType: string, valueType: string, hex: string) => {
  const map = IonFormatterStorage.readMap(new CborReader(fromHex(hex)), keyType, valueType);
  const writer = new CborWriter();
  IonFormatterStorage.writeMap(writer, map, keyType, valueType);
  return toHex(writer.data);
};

describe("Map<K,V> golden vectors", () => {
  for (const v of golden.map.vectors) {
    it(`encodes '${v.name}'`, () => {
      expect(encodeMap(v), v.notes).toEqual(v.hex);
    });

    // THE POINT OF SORTING. A JS Map, a C# Dictionary and a Rust HashMap have three different
    // iteration orders; the same entries in the opposite order must still produce the same bytes.
    it(`encodes '${v.name}' independently of insertion order`, () => {
      expect(encodeMap(v, true)).toEqual(v.hex);
    });

    it(`decodes '${v.name}'`, () => {
      expect(reencodeMap(v.keyType, v.valueType, v.hex), v.notes).toEqual(v.hex);
    });

    it(`orders the keys of '${v.name}' length-first`, () => {
      let body = encodeMap(v).slice(2); // the map header is one byte for all these vectors
      for (const key of v.canonicalKeyOrder) {
        expect(body.startsWith(key), `${v.name}: next key must be ${key}`).toBe(true);
        body = body.slice(key.length);
        body = body.slice(nextItemHexLength(body)); // skip the value
      }
      expect(body, `${v.name}: trailing bytes`).toEqual("");
    });

    it(`decodes '${v.name}' to the right number of entries`, () => {
      const map = IonFormatterStorage.readMap(new CborReader(fromHex(v.hex)), v.keyType, v.valueType);
      expect(map.size).toEqual(v.entries.length);
    });
  }

  for (const v of golden.map.decodeOnly) {
    it(`reads leniently: '${v.name}'`, () => {
      expect(reencodeMap(v.keyType, v.valueType, v.hex), v.notes).toEqual(v.reencodedHex);
    });
  }

  // Duplicate keys are REJECTED. Last-wins and first-wins both make the decoded value depend on
  // wire order — the very non-determinism sorting exists to remove.
  for (const v of golden.map.malformed) {
    it(`rejects '${v.name}' with a TYPED error`, () => {
      expect(() => reencodeMap(v.keyType, v.valueType, v.hex), v.notes).toThrow(
        IonDuplicateMapKeyError
      );
      expect(() => reencodeMap(v.keyType, v.valueType, v.hex)).toThrow(IonDecodeError);
    });
  }
});

// ═══════════════════════════════════════════════════════════════════════════
//  Set<T>
// ═══════════════════════════════════════════════════════════════════════════

export const encodeSet = (v: SetVector, reverse = false) => {
  const elements = reverse ? [...v.elements].reverse() : v.elements;
  const set = new Set<any>(elements.map((e) => scalar(v.elementType, e)));

  const writer = new CborWriter();
  IonFormatterStorage.writeSet(writer, set, v.elementType);
  return toHex(writer.data);
};

const reencodeSet = (elementType: string, hex: string) => {
  const set = IonFormatterStorage.readSet(new CborReader(fromHex(hex)), elementType);
  const writer = new CborWriter();
  IonFormatterStorage.writeSet(writer, set, elementType);
  return toHex(writer.data);
};

describe("Set<T> golden vectors", () => {
  for (const v of golden.set.vectors) {
    it(`encodes '${v.name}'`, () => {
      expect(encodeSet(v), v.notes).toEqual(v.hex);
    });

    it(`encodes '${v.name}' independently of insertion order`, () => {
      expect(encodeSet(v, true)).toEqual(v.hex);
    });

    it(`decodes '${v.name}'`, () => {
      expect(reencodeSet(v.elementType, v.hex), v.notes).toEqual(v.hex);
    });

    it(`tags '${v.name}' with 258`, () => {
      // The tag is what distinguishes Set<T> from Array<T> on the wire; they are distinct Ion
      // types with distinct schema-lock entries.
      expect(v.hex.startsWith("d90102")).toBe(true);
      expect(encodeSet(v).startsWith("d90102")).toBe(true);
    });
  }

  it("two different insertion orders produce identical bytes", () => {
    const a = golden.set.vectors.find((x) => x.name === "insertion-order-a")!;
    const b = golden.set.vectors.find((x) => x.name === "insertion-order-b")!;

    expect(a.hex).toEqual(b.hex); // the golden file itself pins them together
    expect(encodeSet(a)).toEqual(encodeSet(b));
    expect(encodeSet(a)).toEqual("d9010283010203");
  });

  for (const v of golden.set.decodeOnly) {
    it(`reads leniently: '${v.name}'`, () => {
      expect(reencodeSet(v.elementType, v.hex), v.notes).toEqual(v.reencodedHex);
    });
  }

  for (const v of golden.set.malformed) {
    it(`rejects '${v.name}' with a TYPED error`, () => {
      expect(() => reencodeSet(v.elementType, v.hex), v.notes).toThrow(IonDecodeError);
    });
  }

  it("set failures are specifically typed", () => {
    const byName = (n: string) => golden.set.malformed.find((v) => v.name === n)!;
    expect(() => reencodeSet("i4", byName("duplicate-elements").hex)).toThrow(
      IonDuplicateSetElementError
    );
    expect(() => reencodeSet("i4", byName("wrong-tag").hex)).toThrow(IonUnexpectedTagError);
    // An untagged array is Array<T>, not Set<T>: rejected rather than accepted as a courtesy,
    // because accepting it would erase the distinction at the only point it can still be checked.
    expect(() => reencodeSet("i4", byName("missing-tag").hex)).toThrow(IonMalformedValueError);
  });
});

// ═══════════════════════════════════════════════════════════════════════════
//  T[N]
// ═══════════════════════════════════════════════════════════════════════════

export const encodeFixed = (v: FixedVector) => {
  const writer = new CborWriter();
  IonFormatterStorage.writeFixedArray(
    writer,
    v.elements.map((e) => scalar(v.elementType, e)),
    v.elementType,
    v.length
  );
  return toHex(writer.data);
};

const reencodeFixed = (elementType: string, length: number, hex: string) => {
  const array = IonFormatterStorage.readFixedArray(new CborReader(fromHex(hex)), elementType, length);
  const writer = new CborWriter();
  IonFormatterStorage.writeFixedArray(writer, array, elementType, length);
  return toHex(writer.data);
};

describe("T[N] golden vectors", () => {
  for (const v of golden.fixedArray.vectors) {
    it(`encodes '${v.name}'`, () => {
      expect(encodeFixed(v), v.notes).toEqual(v.hex);
    });

    it(`decodes '${v.name}'`, () => {
      expect(reencodeFixed(v.elementType, v.length, v.hex), v.notes).toEqual(v.hex);
    });
  }

  // NO-BYTE-STRING GUARD.
  it("u1[4] is an array of four integers, not a byte string", () => {
    const v = golden.fixedArray.vectors.find((x) => x.name === "u1-n4")!;
    expect(v.hex).toEqual("8401020304");
    expect(v.hex.startsWith("44")).toBe(false);
    expect(encodeFixed(v)).toEqual("8401020304");
  });

  for (const v of golden.fixedArray.decodeOnly) {
    it(`reads leniently: '${v.name}'`, () => {
      expect(reencodeFixed(v.elementType, v.length, v.hex), v.notes).toEqual(v.reencodedHex);
    });
  }

  // THE ENTIRE POINT OF THE FEATURE.
  for (const v of golden.fixedArray.malformed) {
    it(`rejects '${v.name}' naming BOTH lengths`, () => {
      let caught: unknown;
      try {
        reencodeFixed(v.elementType, v.length, v.hex);
      } catch (e) {
        caught = e;
      }

      expect(caught, v.notes).toBeInstanceOf(IonFixedArrayLengthError);
      expect(caught).toBeInstanceOf(IonDecodeError);

      const err = caught as IonFixedArrayLengthError;
      expect(err.expectedLength, "the error names the declared N").toEqual(v.length);
      expect(err.actualLength, "the error names the length received").toEqual(v.actualLength);
      expect(err.message).toContain(String(v.length));
      expect(err.message).toContain(String(v.actualLength));
    });
  }

  it("rejects a mismatched length on WRITE too — writers are exact", () => {
    const writer = new CborWriter();
    expect(() => IonFormatterStorage.writeFixedArray(writer, [1, 2], "i4", 3)).toThrow(
      IonFixedArrayLengthError
    );
  });

  it("takes N as a parameter, not as part of the type", () => {
    const enc = (values: number[], n: number) => {
      const w = new CborWriter();
      IonFormatterStorage.writeFixedArray(w, values, "i4", n);
      return toHex(w.data);
    };
    expect(enc([1], 1)).toEqual("8101");
    expect(enc([1, 2], 2)).toEqual("820102");
    expect(enc([1, 2, 3], 3)).toEqual("83010203");
  });
});

// ═══════════════════════════════════════════════════════════════════════════
//  cross-cutting
// ═══════════════════════════════════════════════════════════════════════════

describe("canonical CBOR ordering", () => {
  // Length-first is NOT plain bytewise ordering, and this is the case that proves it.
  it("compares by encoded length before bytes", () => {
    const minusOne = fromHex("20");
    const thousand = fromHex("1903e8");

    expect(ionCanonicalCborCompare(minusOne, thousand)).toBeLessThan(0);
    // …while a plain bytewise comparison says the opposite, which is why this matters.
    expect(minusOne[0]).toBeGreaterThan(thousand[0]);

    const v = golden.map.vectors.find((x) => x.name === "i4-keys-length-beats-lexicographic")!;
    expect(encodeMap(v)).toEqual("a4000420021818031903e801");
  });
});

describe("container nesting", () => {
  it("a Set inside a Map value is still exactly one item", () => {
    IonFormatterStorage.registerSet("Set<i4>", "i4");

    const writer = new CborWriter();
    IonFormatterStorage.writeMap(
      writer,
      new Map<string, Set<number>>([["a", new Set([2, 1])]]),
      "string",
      "Set<i4>"
    );

    expect(toHex(writer.data)).toEqual("a16161" + "d9010282" + "0102");
  });

  it("named container formatters resolve like scalars", () => {
    IonFormatterStorage.registerMap("Map<string,i4>", "string", "i4");
    IonFormatterStorage.registerFixedArray("i4[3]", "i4", 3);

    const w1 = new CborWriter();
    IonFormatterStorage.get<Map<string, number>>("Map<string,i4>").write(
      w1,
      new Map([
        ["b", 2],
        ["a", 1],
      ])
    );
    expect(toHex(w1.data)).toEqual("a2616101616202");

    const w2 = new CborWriter();
    IonFormatterStorage.get<number[]>("i4[3]").write(w2, [1, 2, 3]);
    expect(toHex(w2.data)).toEqual("83010203");
  });
});

/** Length in hex characters of the single CBOR item at the head of `hex`. */
function nextItemHexLength(hex: string): number {
  const reader = new CborReader(fromHex(hex));
  reader.skipValue();
  return reader.position * 2;
}
