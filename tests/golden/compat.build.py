# -*- coding: utf-8 -*-
"""Assembles `tests/golden/compat.golden.json`.

Run from this directory:

    python compat.build.py

The base payload hexes in `compat.hex.json` come from
`packages/ion.webcore.js/test/compat/__emit.ts`, which writes each value with the *vendored
generated* TypeScript formatters:

    cd packages/ion.webcore.js && bun run test/compat/__emit.ts > ../../tests/golden/compat.hex.json

This script only assembles the vector list and its prose. It deliberately writes an EMPTY
`expect` block for every vector; the recorded behaviour is merged in afterwards by
`compat.record.py` from the three runtimes' record-mode output. Re-running this script
therefore CLEARS the recordings - re-record after running it.
"""
import collections
import io
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
H = json.load(io.open(os.path.join(HERE, "compat.hex.json"), encoding="utf-8"))

ev = []
mal = []


def E(name, axis, position, direction, reader, hex_, notes, **kw):
    v = collections.OrderedDict()
    v["name"] = name
    v["axis"] = axis
    v["position"] = position
    v["direction"] = direction
    v["reader"] = reader
    v["hex"] = hex_
    for k, val in kw.items():
        v[k] = val
    if notes:
        v["notes"] = notes
    v["expect"] = {}
    ev.append(v)


def M(name, group, reader, hex_, notes, **kw):
    v = collections.OrderedDict()
    v["name"] = name
    v["group"] = group
    v["reader"] = reader
    if "build" in kw:
        v["build"] = kw.pop("build")
    else:
        v["hex"] = hex_
    for k, val in kw.items():
        v[k] = val
    if notes:
        v["notes"] = notes
    v["expect"] = {}
    mal.append(v)


A1, A2 = H["AppendedV1"], H["AppendedV2"]

# -- axis: field appended at the end ----------------------------------------
E("appended.forward.bare", "field-appended", "bare", "v2-writes-v1-reads", "AppendedV1", A2,
  "The whole point of the trailing-skip mechanism: an old reader must accept a longer array and drop the tail.")
E("appended.backward.bare", "field-appended", "bare", "v1-writes-v2-reads", "AppendedV2", A1,
  "A new reader against an old writer. There is no field to read, so this must be a typed decode error naming what was expected - never a default value, and never a read past the array.")
E("appended.forward.array", "field-appended", "T[]", "v2-writes-v1-reads", "Array<AppendedV1>", "81" + A2,
  "The same message one element inside `T[]`.")
E("appended.backward.array", "field-appended", "T[]", "v1-writes-v2-reads", "Array<AppendedV2>", "81" + A1,
  "A short element inside an array is the dangerous shape: if the element reader runs off the end of its own array it lands in the NEXT element, not somewhere diagnosable.")
E("appended.forward.array2", "field-appended", "T[]", "v2-writes-v1-reads", "Array<AppendedV1>", "82" + A2 + A2,
  "Two elements, so a per-element over-read shows up as a corrupted second element rather than as end-of-input.")
E("appended.backward.array2", "field-appended", "T[]", "v1-writes-v2-reads", "Array<AppendedV2>", "82" + A1 + A1,
  "Two short elements. A reader that runs off element 0 consumes element 1's header.")
E("appended.forward.fixed", "field-appended", "T[N]", "v2-writes-v1-reads", "Fixed<AppendedV1,2>", "82" + A2 + A2,
  "`T[N]` holds N whole messages; each is skipped independently.")
E("appended.backward.fixed", "field-appended", "T[N]", "v1-writes-v2-reads", "Fixed<AppendedV2,2>", "82" + A1 + A1, "")
E("appended.forward.map", "field-appended", "Map value", "v2-writes-v1-reads", "Map<string,AppendedV1>", "a1616b" + A2, "")
E("appended.backward.map", "field-appended", "Map value", "v1-writes-v2-reads", "Map<string,AppendedV2>", "a1616b" + A1, "")
E("appended.forward.maybe", "field-appended", "T?", "v2-writes-v1-reads", "Maybe<AppendedV1>", A2, "")
E("appended.backward.maybe", "field-appended", "T?", "v1-writes-v2-reads", "Maybe<AppendedV2>", A1, "")
E("appended.forward.union-payload", "field-appended", "union case payload", "v2-writes-v1-reads", "Union<GrowV1>", "82008301616207",
  "A union is `[caseIndex, payload]`. The payload is an ordinary message, so appending a field to a case should behave exactly like appending to a message.")
E("appended.backward.union-payload", "field-appended", "union case payload", "v1-writes-v2-reads", "Union<GrowV2>", "820082016162", "")
E("appended.forward.union-holder", "field-appended", "union in a field", "v2-writes-v1-reads", "GrowHolderV1", H["GrowHolderV2"],
  "The same union with a sentinel `n: i4` behind it, so a union reader that leaves bytes unconsumed corrupts the NEXT field instead of merely being untidy.")
E("appended.backward.union-holder", "field-appended", "union in a field", "v1-writes-v2-reads", "GrowHolderV2", H["GrowHolderV1"], "")
E("appended.forward.method-arg", "field-appended", "method argument", "v2-writes-v1-reads", "Args<AppendedV1>", H["args_Echo_1arg_V2"],
  "The argument envelope is a positional array with the same trailing-skip as a message; the harness reproduces the generated executor prologue verbatim.")
E("appended.backward.method-arg", "field-appended", "method argument", "v1-writes-v2-reads", "Args<AppendedV2>", H["args_Echo_1arg_V1"], "")
E("appended.forward.method-return", "field-appended", "method return", "v2-writes-v1-reads", "AppendedV1", A2,
  "A return value is written bare at the root with no envelope of its own, so the only thing between it and the next frame is its own declared array length.")
E("appended.backward.method-return", "field-appended", "method return", "v1-writes-v2-reads", "AppendedV2", A1, "")
E("appended.forward.every-position", "field-appended", "all at once", "v2-writes-v1-reads", "NestV1", H["NestV2"],
  "One message carrying the evolved type in every position at once (bare, `T[]`, `T[N]`, Map value, `T?`) with `tail: i4 = 99` as the sentinel. If any position leaves the cursor misplaced, `tail` is the field that shows it.")
E("appended.backward.every-position", "field-appended", "all at once", "v1-writes-v2-reads", "NestV2", H["NestV1"], "")
E("appended.forward.method-args-nested", "field-appended", "method argument", "v2-writes-v1-reads", "Args<NestV1,i4>", H["args_EchoNestV2"], "")
E("appended.backward.method-args-nested", "field-appended", "method argument", "v1-writes-v2-reads", "Args<NestV2,i4>", H["args_EchoNestV1"], "")

# -- axis: method argument added --------------------------------------------
E("argument-added.forward", "argument-added", "method argument", "v2-writes-v1-reads", "Args<AppendedV1>", H["args_Echo_2args_V2"],
  "v2 added a second method argument; the v1 executor declares one and skips the rest.")
E("argument-added.backward", "argument-added", "method argument", "v1-writes-v2-reads", "Args<AppendedV2,i4>", H["args_Echo_1arg_V2"],
  "v2's executor wants two arguments and the v1 caller sent one.")

# -- axis: field inserted in the middle --------------------------------------
E("inserted.forward.typed", "field-inserted", "bare", "v2-writes-v1-reads", "InsertedV1", H["InsertedV2"],
  "Insertion caught only because the inserted field's type differs from the field it displaced (`string` where `bool` was).")
E("inserted.backward.typed", "field-inserted", "bare", "v1-writes-v2-reads", "InsertedV2", H["InsertedV1"], "")
E("inserted.forward.same-type", "field-inserted", "bare", "v2-writes-v1-reads", "ShiftedV1", H["ShiftedV2"],
  "THE DANGEROUS CASE. Every field is `i4`, so the insertion slides `c` along with nothing to notice. v2 wrote {a=1,b=2,c=3}; a v1 reader cannot decode this correctly at all, and the one thing it must not do is hand back {a=1,c=2} as if it were fine.")
E("inserted.backward.same-type", "field-inserted", "bare", "v1-writes-v2-reads", "ShiftedV2", H["ShiftedV1"],
  "The mirror: v1 wrote {a=1,c=3}; v2 reads a=1, b=3 and then has nothing left for c.")
E("inserted.forward.same-type.array", "field-inserted", "T[]", "v2-writes-v1-reads", "Array<ShiftedV1>", "82" + H["ShiftedV2"] + H["ShiftedV2"], "")
E("inserted.forward.same-type.map", "field-inserted", "Map value", "v2-writes-v1-reads", "Map<string,ShiftedV1>", "a1616b" + H["ShiftedV2"], "")
E("inserted.forward.same-type.fixed", "field-inserted", "T[N]", "v2-writes-v1-reads", "Fixed<ShiftedV1,2>", "82" + H["ShiftedV2"] + H["ShiftedV2"], "")
E("inserted.forward.same-type.args", "field-inserted", "method argument", "v2-writes-v1-reads", "Args<ShiftedV1>", "81" + H["ShiftedV2"], "")

# -- axis: field removed ------------------------------------------------------
E("removed.forward.typed", "field-removed", "bare", "v2-writes-v1-reads", "RemovedV1", H["RemovedV2"],
  "v2 dropped the middle field. The v1 reader runs off the end of a shorter array - the case `Math.Abs(skipCount)` in `CborReaderEx.ReadEndArrayAndSkip` is reachable from.")
E("removed.backward.typed", "field-removed", "bare", "v1-writes-v2-reads", "RemovedV2", H["RemovedV1"], "")

# -- axis: fields reordered ---------------------------------------------------
E("reordered.forward.typed", "field-reordered", "bare", "v2-writes-v1-reads", "ReorderedV1", H["ReorderedV2"], "")
E("reordered.backward.typed", "field-reordered", "bare", "v1-writes-v2-reads", "ReorderedV2", H["ReorderedV1"], "")
E("reordered.forward.same-type", "field-reordered", "bare", "v2-writes-v1-reads", "SwapV1", H["SwapV2"],
  "THE DANGEROUS CASE. Two `i4` fields swapped: both revisions decode, and each hands back the other one's values.")
E("reordered.backward.same-type", "field-reordered", "bare", "v1-writes-v2-reads", "SwapV2", H["SwapV1"], "")

# -- axis: field type changed -------------------------------------------------
E("retyped.forward", "field-retyped", "bare", "v2-writes-v1-reads", "RetypedV1", H["RetypedV2"], "`string` -> `i4`.")
E("retyped.backward", "field-retyped", "bare", "v1-writes-v2-reads", "RetypedV2", H["RetypedV1"], "")
E("widened.forward.in-range", "field-retyped", "bare", "v2-writes-v1-reads", "WidenedV1", H["WidenedV2_small"],
  "`i4` -> `i8` with a value that fits in 32 bits. CBOR integers are width-agnostic, so this is the one type change invisible on the wire - and therefore the one that keeps working right up until a value exceeds the old range.")
E("widened.forward.out-of-range", "field-retyped", "bare", "v2-writes-v1-reads", "WidenedV1", H["WidenedV2_wide"],
  "The same widening with 2^40. Must be a typed range error.")
E("widened.backward", "field-retyped", "bare", "v1-writes-v2-reads", "WidenedV2", H["WidenedV1"], "A narrow value into a wide field: always fine.")

# -- axis: optional added / tightened -----------------------------------------
E("optional-added.forward.null", "optional-added", "bare", "v2-writes-v1-reads", "OptAddedV1", H["OptAddedV2_null"], "")
E("optional-added.forward.set", "optional-added", "bare", "v2-writes-v1-reads", "OptAddedV1", H["OptAddedV2_set"], "")
E("optional-added.backward", "optional-added", "bare", "v1-writes-v2-reads", "OptAddedV2", H["OptAddedV1"],
  "Adding an OPTIONAL field is still adding a field: `?` describes the value, not its presence on the wire. A v2 reader is not entitled to treat the missing slot as null.")
E("optional-tightened.forward.null", "optional-tightened", "bare", "v1-writes-v2-reads", "OptTightenedV2", H["OptTightenedV1_null"],
  "`string?` became `string`, and the old writer sent null.")
E("optional-tightened.forward.set", "optional-tightened", "bare", "v1-writes-v2-reads", "OptTightenedV2", H["OptTightenedV1_set"], "")
E("optional-tightened.backward", "optional-tightened", "bare", "v2-writes-v1-reads", "OptTightenedV1", H["OptTightenedV2"], "")

# -- axis: T[] <-> T[N] -------------------------------------------------------
E("array-to-fixed.forward.right-length", "array-shape", "bare", "v1-writes-v2-reads", "VarToFixedV2", H["VarToFixedV1_3"], "")
E("array-to-fixed.forward.wrong-length", "array-shape", "bare", "v1-writes-v2-reads", "VarToFixedV2", H["VarToFixedV1_2"],
  "`i4[]` holding 2 read as `i4[3]`.")
E("array-to-fixed.backward", "array-shape", "bare", "v2-writes-v1-reads", "VarToFixedV1", H["VarToFixedV2"],
  "`i4[3]` read as `i4[]`: a fixed array is an ordinary CBOR array on the wire, so this direction is always safe.")
E("fixed.direct.short", "array-shape", "T[N]", "wrong-length", "Fixed<i4,3>", "820102", "")
E("fixed.direct.long", "array-shape", "T[N]", "wrong-length", "Fixed<i4,3>", "8401020304",
  "THE DOCUMENTED ASYMMETRY. `read_fixed_array` in Rust says extra items are deliberately NOT "
  "skipped for forward compatibility, unlike a message's trailing fields. This vector pins that "
  "it is a real rule and not a Rust-only accident, and the asymmetry is right: a message's "
  "length is schema (so a longer array is a newer revision, and the surplus is skippable), while "
  "a `T[N]`'s length IS the declared type (so a longer array is a different type, and skipping "
  "would hide exactly the schema change the feature exists to report). A plain `T[]` sits between "
  "them - its length is data, so there is nothing to skip - and a message ELEMENT inside either "
  "container still skips its own trailing fields, which `appended.forward.array` and "
  "`appended.forward.fixed` pin.")

# -- axis: enum value added ---------------------------------------------------
E("enum-added.forward.known", "enum-added", "bare", "v2-writes-v1-reads", "EnumHolderV1", H["EnumHolderV2_known"], "Control: a value both revisions know.")
E("enum-added.forward.unknown", "enum-added", "bare", "v2-writes-v1-reads", "EnumHolderV1", H["EnumHolderV2_new"],
  "v2 sent Trial=2, which v1's enum cannot represent. `n: i4 = 5` behind it is the sentinel.")
E("enum-added.backward", "enum-added", "bare", "v1-writes-v2-reads", "EnumHolderV2", H["EnumHolderV1"], "")
E("enum-added.forward.unknown.set", "enum-added", "Set", "v2-writes-v1-reads", "Set<TierV1>", "d9010283000102", "")
E("enum-added.forward.known.set", "enum-added", "Set", "v2-writes-v1-reads", "Set<TierV1>", "d90102820001", "")
E("enum-added.forward.unknown.setholder", "enum-added", "Set in a field", "v2-writes-v1-reads", "SetHolderV1", H["SetHolderV2_new"], "")
E("enum-added.forward.unknown.array", "enum-added", "T[]", "v2-writes-v1-reads", "Array<TierV1>", "8102", "")
E("enum-added.forward.unknown.every-position", "enum-added", "all at once", "v2-writes-v1-reads", "NestV1", H["NestV2_newEnumUnion"],
  "An unknown enum AND an unknown union case in one payload, in a message that also exercises every container position.")

# -- axis: union case added ---------------------------------------------------
E("union-added.forward.known", "union-added", "bare", "v2-writes-v1-reads", "UnionHolderV1", H["UnionHolderV2_known"], "Control.")
E("union-added.forward.unknown", "union-added", "bare", "v2-writes-v1-reads", "UnionHolderV1", H["UnionHolderV2_new"],
  "Case index 2 does not exist in v1. `n: i4 = 5` behind it is the sentinel.")
E("union-added.backward", "union-added", "bare", "v1-writes-v2-reads", "UnionHolderV2", H["UnionHolderV1"], "")
E("union-added.forward.unknown.direct", "union-added", "union", "v2-writes-v1-reads", "Union<ShapeV1>", "82028107", "")
E("union-added.arity3", "union-added", "union", "hostile", "Union<ShapeV1>", "83008107f5",
  "A union envelope declaring three items. The generated reader reads the index and the payload and then ends the array without consulting the declared length, so this pins whether a longer union envelope is rejected or quietly leaves a stray item behind.")

# -- recursion ----------------------------------------------------------------
E("tree.depth3", "recursion", "T[] self-reference", "roundtrip", "TreeV1", H["TreeV1_depth3"], "Control for the depth probes in `malformed`.")

# ===================== malformed / hostile ===================================
M("truncated.empty", "truncated", "AppendedV1", "", "Nothing at all.")
M("truncated.header-only", "truncated", "AppendedV1", "82", "The array header and no items.")
M("truncated.mid-message", "truncated", "AppendedV1", "8201", "First field present, second missing entirely.")
M("truncated.mid-field", "truncated", "AppendedV1", "820162",
  "A text string declaring 2 bytes with none following: truncation INSIDE a field rather than between fields.")
M("truncated.mid-field-partial", "truncated", "AppendedV1", "82016261", "The same text string with one of its two bytes.")
M("truncated.nested-element", "truncated", "Array<AppendedV1>", "82" + A1 + "8201", "A two-element array whose second element is truncated.")
M("truncated.map-value", "truncated", "Map<string,AppendedV1>", "a1616b8201", "")
M("truncated.set", "truncated", "Set<TierV1>", "d90102830001", "A tagged set declaring three elements and holding two.")
M("length.overclaim.array", "length-lie", "AppendedV1", "83016162", "The message array header claims three items and the buffer holds two.")
M("length.overclaim.inner-array", "length-lie", "Array<i4>", "830102", "A `T[]` header claiming three and holding two.")
M("length.overclaim.map", "length-lie", "Map<string,i4>", "a2616b01", "A map header claiming two entries and holding one.")
M("length.underclaim.negative-skip", "length-lie", "ShiftedV2", "8201020304",
  "THE NEGATIVE-SKIP CASE, in isolation. `ShiftedV2` has three `i4` fields; the array header "
  "declares two, and two more integers follow it in the buffer. A reader that does not enforce "
  "the declared length reads the third field from OUTSIDE its own array, and then computes "
  "skipCount = 2 - 3 = -1. `CborReaderEx.ReadEndArrayAndSkip` (C#) and `readEndArrayAndSkip` "
  "(TypeScript) both take `abs(skipCount)`, so a negative delta skips FORWARD - consuming a "
  "fourth item that belongs to whatever comes next. `consumed` is what shows it: 3 bytes is the "
  "message, anything more is somebody else's data.")
M("union.arity3.corrupts-next-field", "length-lie", "GrowHolderV1", "828300820161620905",
  "The union stray-item hazard with a field behind it. The union envelope declares three items; "
  "the generated union reader reads the index and the payload and then ends the array without "
  "consulting the declared length. `n: i4` is next, and the value it picks up is the union's "
  "leftover 9 rather than its own 5.")
M("length.overclaim.set", "length-lie", "Set<TierV1>", "d901028300", "")
M("indefinite.array.message", "indefinite", "AppendedV1", "9f016162ff",
  "A message written as an indefinite-length array. Generated readers call `ReadStartArray()` and reject a null length outright, so this is where the 'undefined len array not allowed' guard is observable.")
M("indefinite.array.inner", "indefinite", "Array<i4>", "9f0102ff", "A `T[]` field as an indefinite-length array.")
M("indefinite.map", "indefinite", "Map<string,i4>", "bf616b01ff", "")
M("indefinite.set", "indefinite", "Set<TierV1>", "d901029f0001ff", "")
M("indefinite.fixed", "indefinite", "Fixed<i4,3>", "9f010203ff",
  "`read_fixed_array` in Rust documents that an indefinite-length array is accepted as long as it turns out to hold exactly N. This pins whether C# and TypeScript agree.")
M("indefinite.text", "indefinite", "AppendedV1", "82017f6162ff", "An indefinite-length (chunked) text string in a `string` field.")
M("indefinite.bytes-in-array", "indefinite", "Array<i4>", "5f41ff41ffff", "An indefinite-length byte string where an array is expected.")
M("huge.length.array", "allocation-guard", "Array<i4>", "9bffffffffffffffff",
  "An array header declaring 2^64-1 items with no payload. A reader that pre-sizes a buffer from the declared length before reading anything is a one-packet out-of-memory.")
M("huge.length.array-32", "allocation-guard", "Array<i4>", "9a7fffffff", "2^31-1 items - the realistic version, small enough that a pre-allocating reader will actually try.")
M("huge.length.fixed", "allocation-guard", "Fixed<i4,3>", "9bffffffffffffffff", "")
M("huge.length.map", "allocation-guard", "Map<string,i4>", "bbffffffffffffffff", "")
M("huge.length.set", "allocation-guard", "Set<TierV1>", "d901029bffffffffffffffff", "")
M("huge.length.text", "allocation-guard", "AppendedV1", "82017bffffffffffffffff", "A text string declaring 2^64-1 bytes.")
M("huge.length.bytes", "allocation-guard", "Array<i4>", "5bffffffffffffffff", "")
M("huge.length.message", "allocation-guard", "AppendedV1", "9bffffffffffffffff", "The message's own array header.")
M("deep.nesting.skipped-tail", "recursion-depth", "AppendedV1", None,
  "A well-formed AppendedV1 with a third array item 2000 nested arrays deep. The reader never looks at the value - it only SKIPS it - so this measures the skip path's recursion, which is the part an attacker reaches without knowing the schema.",
  build={"prefix": "83016162", "repeat": "81", "count": 2000, "suffix": "00"})
M("deep.nesting.skipped-tail-100k", "recursion-depth", "AppendedV1", None,
  "The same at 100000 deep.",
  build={"prefix": "83016162", "repeat": "81", "count": 100000, "suffix": "00"})
M("deep.nesting.tree", "recursion-depth", "TreeV1", None,
  "A legitimately typed recursive message 2000 levels deep. Unlike the skip path, this one goes through the generated reader.",
  build={"prefix": "", "repeat": "820181", "count": 2000, "suffix": "820180"})
M("trailing.garbage", "trailing", "AppendedV1", A1 + "f5f5f5f5",
  "A complete message with four stray bytes behind it. Framing is the transport's job, so the decode must succeed - but it must consume exactly four bytes and leave the rest.")
M("trailing.garbage.args", "trailing", "Args<AppendedV1>", "81" + A1 + "f5", "")

for _label, _hex in [
    ("int", "01"),
    ("text", "6161"),
    ("bytes", "41ff"),
    ("array", "80"),
    ("map", "a0"),
    ("bool", "f5"),
    ("null", "f6"),
    ("undefined", "f7"),
    ("float", "fa3f800000"),
    ("tag", "c101"),
]:
    M("majortype.field0." + _label, "wrong-major-type", "AppendedV1", "82" + _hex + "6162",
      "CONTROL: field 0 is declared `i4` and this IS an integer, so the payload is well formed. "
      "The sweep needs its baseline."
      if _label == "int" else
      "Field 0 is declared `i4`; here it is a CBOR " + _label + ".")
    M("majortype.field1." + _label, "wrong-major-type", "AppendedV1", "8201" + _hex,
      "CONTROL: field 1 is declared `string` and this IS a text string, so the payload is well "
      "formed."
      if _label == "text" else
      "Field 1 is declared `string`; here it is a CBOR " + _label + ".")
    M("majortype.root." + _label, "wrong-major-type", "AppendedV1", _hex,
      "The message's own array header replaced by an EMPTY CBOR array - the right major type, "
      "the wrong length, which is the one case in this sweep that reaches the field reads."
      if _label == "array" else
      "The message's own array header replaced by a CBOR " + _label + ".")

M("majortype.enum.text", "wrong-major-type", "EnumHolderV1", "82616105", "An enum field carrying a text string.")
M("majortype.enum.negative", "wrong-major-type", "EnumHolderV1", "822005", "An enum field carrying -1, which no `u1`-based enum can hold.")
M("majortype.enum.overflow", "wrong-major-type", "EnumHolderV1", "8219010005", "An enum field carrying 256, one past `u1`.")
M("majortype.set.untagged", "wrong-major-type", "Set<TierV1>", "820001", "A `Set<T>` payload without tag 258.")
M("majortype.set.wrong-tag", "wrong-major-type", "Set<TierV1>", "d90101820001", "Tag 257 instead of 258.")
M("majortype.map.array", "wrong-major-type", "Map<string,i4>", "82616b01", "A map written as an array of alternating keys and values.")
M("majortype.union.index-negative", "wrong-major-type", "Union<ShapeV1>", "82208107", "Union case index -1.")
M("majortype.union.index-huge", "wrong-major-type", "Union<ShapeV1>", "821bffffffffffffffff8107", "Union case index 2^64-1.")
M("majortype.union.index-text", "wrong-major-type", "Union<ShapeV1>", "8261618107", "Union case index as a text string.")
M("majortype.union.no-payload", "wrong-major-type", "Union<ShapeV1>", "8100", "A union envelope with an index and no payload.")

doc = collections.OrderedDict()
doc["$format"] = "ionpath/compat-golden/1"
doc["$comment"] = [
    "Cross-runtime BACKWARD/FORWARD COMPATIBILITY vectors.",
    "",
    "The other files in this directory pin the bytes a VALUE encodes to. This one pins what a",
    "reader DOES when the bytes were written against a different revision of the schema, or by",
    "something hostile. Consumed by all three in-repo runtimes:",
    "  C#   src/tests/IonTestClientServer/CompatGoldenTests.cs",
    "  TS   packages/ion.webcore.js/test/compat.golden.test.ts",
    "  Rust packages/ion.rustcore/tests/compat_golden.rs",
    "",
    "IMPORTANT: a vector's `expect` block records what the runtimes do TODAY, not what they",
    "should do. Read `verdict` first:",
    "  correct     - the behaviour is right, and the vector guards it against regression.",
    "  defect      - the behaviour is wrong; `shouldBe` says what it must become. The assertion",
    "                pins the wrong behaviour ON PURPOSE, so that fixing the runtime turns the",
    "                test red and forces the pin to be updated - rather than leaving the gap",
    "                invisible.",
    "  divergence  - the three runtimes disagree about the same bytes. Same rule: pinned, with",
    "                `shouldBe` naming the behaviour they should converge on.",
    "  acceptable  - not wrong, but worth knowing and worth not changing by accident.",
    "",
    "Outcomes:",
    "  ok            - decoded. `reencodedHex` is the decoded value re-encoded with the SAME",
    "                  reader schema, which catches a wrong value without having to model every",
    "                  message in JSON; `consumed` is how many bytes the read advanced.",
    "  error         - a TYPED Ion decode failure; `error` names the class or variant.",
    "  untyped-error - threw something that is NOT an Ion decode failure. Always a defect: a",
    "                  caller cannot tell 'the peer speaks an older schema' from 'the peer sent",
    "                  garbage', and cannot catch it with a single handler.",
    "",
    "OVER-READ DETECTION. Every `evolution` vector is decoded from `hex` + `trailerHex`, and",
    "`consumed` is asserted. A message is a positional array with no self-delimiting end marker,",
    "so a reader that mis-counts fields does not fail - it walks into whatever bytes come next.",
    "Inside a `T[]` that is the next element; over a stream it is the next frame. `consumed` is",
    "the only thing that catches it. `malformed` vectors are read WITHOUT the trailer, so a",
    "truncation stays a truncation.",
    "",
    "HOW THE BYTES WERE PRODUCED. The schemas live in",
    "src/tests/IonTestClientServer/Compat/Compat.ion, are compiled by `ionc` into all three",
    "targets: vendored into the TypeScript and Rust test trees, generated at build time for C#",
    "(see Compat/README.md). The payload",
    "hexes were then written by those vendored GENERATED formatters - see `$producer`. All three",
    "runtimes re-derive the same bytes when the suite runs, so a divergence between the writers",
    "shows up as a failing vector rather than as a stale constant.",
]
doc["$producer"] = (
    "tests/golden/compat.build.py assembles this file from tests/golden/compat.hex.json; "
    "the hexes in that file come from `bun run test/compat/__emit.ts` in packages/ion.webcore.js, "
    "which writes each value with the vendored generated TypeScript formatters. "
    "The `expect` blocks are filled in by tests/golden/compat.record.py from the three runtimes' "
    "record mode (ION_COMPAT_RECORD=1)."
)
doc["schema"] = collections.OrderedDict([
    ("source", "src/tests/IonTestClientServer/Compat/Compat.ion"),
    ("module", "IonCompat"),
    ("note", "Every V1/V2 pair is the same logical message at two revisions, spelled with distinct names so that both revisions link into one test binary."),
])
doc["trailerHex"] = "f5f5f5f5"
doc["readers"] = collections.OrderedDict([
    ("<MsgName>", "the generated formatter for that message, read at the root"),
    ("Array<T>", "what a `T[]` field reads: the runtime's array helper over element formatter T"),
    ("Fixed<T,N>", "what a `T[N]` field reads"),
    ("Map<K,V>", "what a `Map<K,V>` field reads"),
    ("Set<T>", "what a `Set<T>` field reads (CBOR tag 258)"),
    ("Maybe<T>", "what a `T?` field reads"),
    ("Union<U>", "the generated union formatter for U"),
    ("Args<T0,T1,...>", "the method-argument envelope: ReadStartArray(), each argument, ReadEndArrayAndSkip(len - argc) - the generated executor prologue, verbatim"),
])

# ── what the toolchain stops before it ever reaches a reader ────────────────
def L(axis, caught, diagnostic, note):
    return collections.OrderedDict([
        ("axis", axis),
        ("caught", caught),
        ("diagnostic", diagnostic),
        ("note", note),
    ])


doc["lockCheck"] = collections.OrderedDict([
    ("$comment", [
        "`ionc lock check` over one v1 -> v2 edit per axis, recorded by",
        "tests/golden/compat.lockprobe.sh. This is the other half of the picture: the vectors",
        "below say what a reader DOES with a payload from another revision, and this says what",
        "stops that payload from being written in the first place. A positional array cannot",
        "carry the information a reader would need to detect a non-append edit, so ion.lock.json",
        "is the ONLY place the toolchain can reject one.",
        "",
        "`caught` is 'error' when `ionc lock check` exits non-zero and leaves the lock file",
        "alone, 'warning' when it reports but exits 0 and rewrites the lock, and 'no' when it",
        "says nothing at all. Every 'no' on an axis whose vectors below are red is a hole: the",
        "edit ships, and the failure surfaces at a peer.",
    ]),
    ("recordedBy", "tests/golden/compat.lockprobe.sh"),
    ("axes", [
        L("append", "warning", "ION0029 (field added is not nullable)",
          "Correct: appending is the one safe edit, and the warning is about the reader's inability to distinguish 'absent' from 'default', not about the wire."),
        L("optional-add", "no", None,
          "Correct on the wire, but see the `optional-added.backward` vectors: a NEW reader still cannot read an OLD writer's payload, because `?` describes the value, not its presence."),
        L("insert-middle", "error", "ION0021 (field changed index)", "Correct - this is the edit the `inserted.forward.same-type` vectors show silently corrupting all three runtimes."),
        L("remove-field", "error", "ION0020 (field removed)", "Correct."),
        L("reorder-fields", "error", "ION0021 (field changed index)", "Correct - matches the `reordered.*.same-type` vectors."),
        L("retype-field", "error", "ION0022 (field changed type)", "Correct."),
        L("widen-field", "error", "ION0022 (i4 -> i8)", "Correct, and stricter than the wire needs: the vectors show i4 -> i8 is invisible until a value exceeds the old range."),
        L("rename-field", "error", "ION0020 + ION0029", "Correct, though reported as a removal plus an addition rather than as a rename."),
        L("optional-tighten", "error", "ION0022 (string? -> string)", "Correct."),
        L("array-to-fixed", "error", "ION0022 (Array<i4> -> Array<i4,3>)", "Correct."),
        L("fixed-resize", "error", "ION0022 (Array<i4,3> -> Array<i4,4>)", "Correct."),
        L("msg-remove", "error", "ION0023 (definition removed)", "Correct."),
        L("enum-add", "error", "ION0070 (closed value space gained a member)",
          "Closed. A new member lands inside a value the reader is already obliged to interpret, unlike an appended field which lands past the end of what it reads - hence an error where ION0029 is a warning."),
        L("enum-remove", "error", "ION0023 (enum member removed)",
          "Closed. An old peer still sending the removed value now fails to decode, so the edit is breaking."),
        L("enum-renumber", "error", "ION0027 (member changed value)", "Correct."),
        L("enum-basetype", "error", "ION0022 (enum changed base type)",
          "Closed. The base type is the wire width, so changing it is a retype."),
        L("enum-rename-member", "warning", "ION0025 (member renamed, value unchanged)",
          "Warning by design: the ordinal is what is on the wire and the name never is, so every payload ever written still decodes to the identical value. Only generated code that names the member breaks."),
        L("union-add-case", "error", "ION0070 (closed value space gained a member)",
          "Closed. An unknown case index is a typed failure in all three runtimes - there is no payload shape to skip."),
        L("union-reorder-cases", "error", "ION0028 (case changed index)", "Correct."),
        L("union-rename-case", "error", "ION0023 (union case removed)", "Correct."),
        L("union-case-append", "warning", "ION0029 (field added is not nullable)",
          "Deliberately identical to appending to a msg: a case payload is a positional array nested in the union envelope, so the same rule applies."),
        L("union-case-insert", "error", "ION0021 (field changed index)",
          "Closed. The lock now records each inline case payload field by index, name and canonical type, so the silent-corruption shape is visible."),
        L("union-case-retype", "error", "ION0022 (field changed type)",
          "Closed by the same change."),
        L("method-add-arg", "error", "ION0026 (argument count)", "Correct - stricter than the wire, which skips a trailing argument (see `argument-added.forward`)."),
        L("method-arg-retype", "error", "ION0026 (argument type)", "Correct."),
        L("method-retype", "error", "ION0026 (return type)", "Correct."),
        L("method-remove", "warning", "ION0025 (method removed)", "Reported, but exit 0 and the lock is rewritten, so it does not stop a build."),
        L("method-rename", "warning", "ION0025 (method removed)", "Same, and a rename is indistinguishable from a removal plus an addition."),
    ]),
    ("note", [
        "`ionc lock check` REWRITES ion.lock.json whenever the edit is non-breaking, so it is not",
        "a read-only command. On a breaking edit it exits non-zero and leaves the file alone.",
    ]),
])

doc["evolution"] = ev
doc["malformed"] = mal

out = os.path.join(HERE, "compat.golden.json")
with io.open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump(doc, f, indent=2, ensure_ascii=False)
    f.write("\n")
print("evolution: %d  malformed: %d  -> %s" % (len(ev), len(mal), out))
