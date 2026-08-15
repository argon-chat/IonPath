import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  CborReader,
  CborWriter,
  IonFormatterStorage,
  IonMaybe,
  IonPartialState,
  ionPartialModify,
  ionPartialRemove,
  ionPartialState,
  type IonPartial,
  type IonPartialSchema,
} from "../src";
import "../src/index";

// ─── shared cross-runtime golden vectors ────────────────────────────────────
// /tests/golden/partial.golden.json is also consumed by
//   src/tests/IonTestClientServer/TestTypes.cs   (C#)
//   packages/ion.rustcore/tests/partial_golden.rs (Rust)

interface GoldenVector {
  name: string;
  direction: "encode" | "decode" | "roundtrip";
  hex: string;
  reencodedHex?: string;
  notes?: string;
}

const goldenPath = fileURLToPath(
  new URL("../../../tests/golden/partial.golden.json", import.meta.url)
);
const golden = JSON.parse(readFileSync(goldenPath, "utf8")) as {
  vectors: GoldenVector[];
};

const vector = (name: string): GoldenVector => {
  const v = golden.vectors.find((x) => x.name === name);
  if (!v) throw new Error(`Golden vector '${name}' not found in ${goldenPath}`);
  return v;
};

// ─── the golden message: msg GoldenPatchTarget { n: i4; f: f4; s: string; items: i4[]; note: string?; }

interface GoldenPatchTarget {
  n: number;
  f: number;
  s: string;
  items: number[];
  note: IonMaybe<string>;
}

/** Exactly what codegen will emit for `GoldenPatchTarget~`. */
const schema: IonPartialSchema = [
  { name: "n", type: "i4" },
  { name: "f", type: "f4" },
  { name: "s", type: "string" },
  { name: "items", type: "i4", kind: "array" },
  { name: "note", type: "string", kind: "maybe" },
];

const formatter = IonFormatterStorage.registerPartial<GoldenPatchTarget>(
  "IonPartial<GoldenPatchTarget>",
  schema
);

const toHex = (bytes: Uint8Array) =>
  Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");

const fromHex = (hex: string) =>
  new Uint8Array(
    (hex.match(/../g) ?? []).map((byte) => Number.parseInt(byte, 16))
  );

const encode = (patch: IonPartial<GoldenPatchTarget>) => {
  const writer = new CborWriter();
  formatter.write(writer, patch);
  return toHex(writer.data);
};

const decode = (hex: string) => formatter.read(new CborReader(fromHex(hex)));

const patches: Record<string, () => IonPartial<GoldenPatchTarget>> = {
  "empty": () => ({}),
  "modified-scalar-int": () => ({ n: 7 }),
  "modified-scalar-float": () => ({ f: 1.1 }),
  "modified-scalar-float-half-representable": () => ({ f: 1.5 }),
  "cleared-scalar-float": () => ({ f: null }),
  "cleared-scalar-reference": () => ({ s: null }),
  "modified-array": () => ({ items: [1, 2, 3] }),
  "cleared-array": () => ({ items: null }),
  "modified-optional-some": () => ({ note: IonMaybe.Some("hi") }),
  "cleared-optional": () => ({ note: null }),
  "modified-optional-none": () => ({ note: IonMaybe.None<string>() }),
  // deliberately NOT in declaration order — the encoder must reorder
  "all-fields": () => ({
    note: IonMaybe.Some("hi"),
    items: [1, 2, 3],
    s: "ab",
    f: null,
    n: 7,
  }),
};

describe("Partial<T> golden vectors", () => {
  for (const v of golden.vectors) {
    if (v.direction === "encode" || v.direction === "roundtrip") {
      it(`encodes '${v.name}'`, () => {
        const build = patches[v.name];
        expect(build, `no TS builder for golden vector '${v.name}'`).toBeTruthy();
        expect(encode(build())).toEqual(v.hex);
      });
    }

    if (v.direction === "decode" || v.direction === "roundtrip") {
      it(`decodes '${v.name}'`, () => {
        expect(encode(decode(v.hex))).toEqual(v.reencodedHex ?? v.hex);
      });
    }
  }

  it("encodes 'cleared' and 'modified to none' identically (R4)", () => {
    expect(encode({ note: null })).toEqual(encode({ note: IonMaybe.None() }));
    expect(encode({ note: null })).toEqual(vector("cleared-optional").hex);
  });
});

describe("IonPartial<T>", () => {
  it("distinguishes untouched / modified / cleared", () => {
    const patch: IonPartial<GoldenPatchTarget> = {};
    expect(ionPartialState(patch, "n")).toEqual(IonPartialState.None);

    ionPartialModify(patch, "n", 7);
    expect(ionPartialState(patch, "n")).toEqual(IonPartialState.Modified);

    ionPartialRemove(patch, "n");
    expect(ionPartialState(patch, "n")).toEqual(IonPartialState.Removed);
  });

  it("treats an explicit undefined as untouched", () => {
    expect(encode({ n: undefined })).toEqual("a0");
  });

  it("skips unknown keys on read", () => {
    const decoded = decode(vector("unknown-keys-skipped").hex);
    expect(Object.keys(decoded)).toEqual(["n"]);
    expect(decoded.n).toEqual(7);
  });

  it("reads an indefinite-length map", () => {
    const decoded = decode(vector("indefinite-length-map").hex);
    expect(decoded.n).toEqual(7);
    expect(decoded.f).toBeNull();
  });

  it("rejects a field that is not in the schema", () => {
    expect(() => encode({ nope: 1 } as any)).toThrow(/Unknown field/);
  });
});
