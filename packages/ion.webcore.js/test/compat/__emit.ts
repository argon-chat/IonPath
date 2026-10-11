import { CborWriter, IonFormatterStorage } from "../../src";
import "../../src/index";
import * as C from "./compat.generated";

const hex = (b: Uint8Array) => Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");
const enc = (name: string, v: any) => {
  const w = new CborWriter();
  IonFormatterStorage.get<any>(name).write(w, v);
  return hex(w.data);
};
const out: Record<string, string> = {};
const put = (k: string, name: string, v: any) => { out[k] = enc(name, v); };

put("AppendedV1", "AppendedV1", { a: 1, b: "b" });
put("AppendedV2", "AppendedV2", { a: 1, b: "b", c: 7 });
put("ShiftedV1", "ShiftedV1", { a: 1, c: 3 });
put("ShiftedV2", "ShiftedV2", { a: 1, b: 2, c: 3 });
put("SwapV1", "SwapV1", { x: 1, y: 2 });
put("SwapV2", "SwapV2", { y: 2, x: 1 });
put("InsertedV1", "InsertedV1", { a: 1, c: true });
put("InsertedV2", "InsertedV2", { a: 1, b: "b", c: true });
put("RemovedV1", "RemovedV1", { a: 1, b: "b", c: true });
put("RemovedV2", "RemovedV2", { a: 1, c: true });
put("ReorderedV1", "ReorderedV1", { a: 1, b: "b" });
put("ReorderedV2", "ReorderedV2", { b: "b", a: 1 });
put("RetypedV1", "RetypedV1", { a: 1, b: "b" });
put("RetypedV2", "RetypedV2", { a: 1, b: 2 });
put("WidenedV1", "WidenedV1", { a: 5 });
put("WidenedV2_small", "WidenedV2", { a: 5n });
put("WidenedV2_wide", "WidenedV2", { a: 1099511627776n });
put("OptAddedV1", "OptAddedV1", { a: 1 });
put("OptAddedV2_null", "OptAddedV2", { a: 1, b: null });
put("OptAddedV2_set", "OptAddedV2", { a: 1, b: "b" });
put("OptTightenedV1_null", "OptTightenedV1", { a: 1, b: null });
put("OptTightenedV1_set", "OptTightenedV1", { a: 1, b: "b" });
put("OptTightenedV2", "OptTightenedV2", { a: 1, b: "b" });
put("VarToFixedV1_3", "VarToFixedV1", { xs: [1, 2, 3] });
put("VarToFixedV1_2", "VarToFixedV1", { xs: [1, 2] });
put("VarToFixedV2", "VarToFixedV2", { xs: [1, 2, 3] });
put("EnumHolderV1", "EnumHolderV1", { t: C.TierV1.Paid, n: 5 });
put("EnumHolderV2_known", "EnumHolderV2", { t: C.TierV2.Paid, n: 5 });
put("EnumHolderV2_new", "EnumHolderV2", { t: C.TierV2.Trial, n: 5 });
put("UnionHolderV1", "UnionHolderV1", { s: new C.CircleV1(7), n: 5 });
put("UnionHolderV2_known", "UnionHolderV2", { s: new C.CircleV2(7), n: 5 });
put("UnionHolderV2_new", "UnionHolderV2", { s: new C.TriangleV2(7), n: 5 });
put("GrowHolderV1", "GrowHolderV1", { g: new C.GrowCaseV1(1, "b"), n: 5 });
put("GrowHolderV2", "GrowHolderV2", { g: new C.GrowCaseV2(1, "b", 7), n: 5 });
put("SetHolderV1", "SetHolderV1", { s: new Set([C.TierV1.Free, C.TierV1.Paid]), n: 5 });
put("SetHolderV2_known", "SetHolderV2", { s: new Set([C.TierV2.Free, C.TierV2.Paid]), n: 5 });
put("SetHolderV2_new", "SetHolderV2", { s: new Set([C.TierV2.Free, C.TierV2.Paid, C.TierV2.Trial]), n: 5 });

const nest1: any = {
  bare: { a: 1, b: "b" },
  list: [{ a: 2, b: "c" }],
  slots: [{ a: 3, b: "d" }, { a: 4, b: "e" }],
  byName: new Map([["k", { a: 5, b: "f" }]]),
  opt: { a: 6, b: "g" },
  tier: C.TierV1.Paid,
  tiers: new Set([C.TierV1.Free, C.TierV1.Paid]),
  shape: new C.CircleV1(7),
  tail: 99,
};
const nest2: any = {
  bare: { a: 1, b: "b", c: 11 },
  list: [{ a: 2, b: "c", c: 12 }],
  slots: [{ a: 3, b: "d", c: 13 }, { a: 4, b: "e", c: 14 }],
  byName: new Map([["k", { a: 5, b: "f", c: 15 }]]),
  opt: { a: 6, b: "g", c: 16 },
  tier: C.TierV2.Paid,
  tiers: new Set([C.TierV2.Free, C.TierV2.Paid]),
  shape: new C.CircleV2(7),
  tail: 99,
};
put("NestV1", "NestV1", nest1);
put("NestV2", "NestV2", nest2);
const nest2trial = { ...nest2, tier: C.TierV2.Trial, tiers: new Set([C.TierV2.Free, C.TierV2.Paid, C.TierV2.Trial]), shape: new C.TriangleV2(7) };
put("NestV2_newEnumUnion", "NestV2", nest2trial);
put("TreeV1_depth3", "TreeV1", { v: 1, kids: [{ v: 2, kids: [{ v: 3, kids: [] }] }] });

// nullable fields appended: v1 payloads a v2 reader must accept, v2 payloads a v1 reader skips.
put("NullAppendV1", "NullAppendV1", { a: 1, b: "b" });
put("NullAppendV2_set", "NullAppendV2", {
  a: 1, b: "b", c: 7, note: "n", xs: [1, 2], inner: { a: 2, b: "c" },
});
put("NullAppendV2_null", "NullAppendV2", { a: 1, b: "b", c: null, note: null, xs: null, inner: null });
put("MidNullV1", "MidNullV1", { a: 1, b: null });
put("MidNullV2", "MidNullV2", { a: 1, b: null, c: 3 });
const nullNest1: any = {
  list: [{ a: 1, b: "b" }],
  slots: [{ a: 2, b: "c" }, { a: 3, b: "d" }],
  byName: new Map([["k", { a: 4, b: "e" }]]),
  opt: { a: 5, b: "f" },
  tail: 99,
};
const nullNest2: any = {
  list: [{ a: 1, b: "b", c: 11, note: null, xs: null, inner: null }],
  slots: [
    { a: 2, b: "c", c: null, note: "s", xs: null, inner: null },
    { a: 3, b: "d", c: null, note: null, xs: [3], inner: null },
  ],
  byName: new Map([["k", { a: 4, b: "e", c: null, note: null, xs: null, inner: { a: 9, b: "z" } }]]),
  opt: { a: 5, b: "f", c: 15, note: "o", xs: [], inner: null },
  tail: 99,
};
put("NullNestV1", "NullNestV1", nullNest1);
put("NullNestV2", "NullNestV2", nullNest2);
put("GrowNullHolderV1", "GrowNullHolderV1", { g: new C.GrowNullCaseV1(1, "b"), n: 5 });
put("GrowNullHolderV2", "GrowNullHolderV2", { g: new C.GrowNullCaseV2(1, "b", 7, "n"), n: 5 });

// method-argument envelopes: exactly what the generated client writes.
const argEnv = (items: Array<[string, any]>) => {
  const w = new CborWriter();
  w.writeStartArray(items.length);
  for (const [n, v] of items) IonFormatterStorage.get<any>(n).write(w, v);
  w.writeEndArray();
  return hex(w.data);
};
out["args_EchoNestV1"] = argEnv([["NestV1", nest1], ["i4", 99]]);
out["args_EchoNestV2"] = argEnv([["NestV2", nest2], ["i4", 99]]);
out["args_Echo_1arg_V1"] = argEnv([["AppendedV1", { a: 1, b: "b" }]]);
out["args_Echo_1arg_V2"] = argEnv([["AppendedV2", { a: 1, b: "b", c: 7 }]]);
out["args_Echo_2args_V2"] = argEnv([["AppendedV2", { a: 1, b: "b", c: 7 }], ["i4", 42]]);
// `Tag` gained two trailing nullable arguments in v2; a v2 client writes all three.
const argEnvNullable = (items: Array<[string, any, boolean]>) => {
  const w = new CborWriter();
  w.writeStartArray(items.length);
  for (const [n, v, nullable] of items)
    if (nullable) IonFormatterStorage.writeNullable<any>(w, v, n);
    else IonFormatterStorage.get<any>(n).write(w, v);
  w.writeEndArray();
  return hex(w.data);
};
out["args_Tag_V1"] = argEnvNullable([["AppendedV1", { a: 1, b: "b" }, false]]);
out["args_Tag_V2"] = argEnvNullable([
  ["AppendedV1", { a: 1, b: "b" }, false],
  ["string", "n", true],
  ["i4", 5, true],
]);
out["args_Tag_V2_nulls"] = argEnvNullable([
  ["AppendedV1", { a: 1, b: "b" }, false],
  ["string", null, true],
  ["i4", null, true],
]);

console.log(JSON.stringify(out, null, 2));
