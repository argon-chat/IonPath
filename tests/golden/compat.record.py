# -*- coding: utf-8 -*-
"""Merges the three runtimes' recorded behaviour into `tests/golden/compat.golden.json`.

Workflow, from the repository root:

    python tests/golden/compat.build.py                 # (re)generate the vector list
    cd src/tests/IonTestClientServer && ION_COMPAT_RECORD=1 dotnet test ../../../src/tests/IonTestClientServer/IonTestClientServer.csproj --filter FullyQualifiedName~CompatGoldenTests
    cd packages/ion.webcore.js       && ION_COMPAT_RECORD=1 bun run test
    cd packages/ion.rustcore         && ION_COMPAT_RECORD=1 cargo test --test compat_golden
    python tests/golden/compat.record.py                # merge + classify

Each runtime writes what it actually did to `tests/golden/.dump/compat.{cs,ts,rust}.json`
(gitignored). This script copies those observations into each vector's `expect` block and stamps
the vector with a `verdict` and, where the behaviour is wrong, a `shouldBe`.

THE VERDICTS ARE THE JUDGEMENT AND THEY LIVE HERE, not in the recordings: re-recording must never
be able to silently turn a defect into an expectation. `compat.build.py` clears `expect`;
this script refills it and re-applies the table below.
"""
import collections
import io
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
RUNTIMES = ("cs", "ts", "rust")

# ── verdict table ───────────────────────────────────────────────────────────
# ("exact"|"prefix", pattern, verdict, shouldBe). First match wins, so put the exact names first.
#
#   correct     the behaviour is right, and the vector guards it.
#   acceptable  not wrong, but worth knowing and worth not changing by accident.
#   defect      the behaviour is wrong; `shouldBe` says what it must become.
#   divergence  the three runtimes disagree on the same bytes.

UNTYPED = (
    "A typed Ion decode failure (ion.runtime.IonDecodeException / IonDecodeError / an IonError "
    "variant other than the catch-all Decode(String)) naming the field position and what was "
    "expected. Detection is already right here - only the error type is wrong, and a caller "
    "cannot tell 'the peer speaks an older schema' from 'the peer sent garbage' by catching "
    "System.InvalidOperationException or a bare Error."
)

POSITIONAL = (
    "Nothing at the wire level can defend against this: a positional array cannot distinguish a "
    "field inserted in the middle from a longer message, so both readers decode and both are "
    "wrong. It has to be stopped before it ships - ion.lock.json must reject a non-append change "
    "to a message's field list, and ionc must refuse to build against a lock that disagrees. The "
    "vector pins the runtime consequence so the cost of a missing lock check is measurable."
)

LOCKED = (
    "Nothing a decoder can do. The wire is a positional array, so an inserted or reordered "
    "field of the same type is indistinguishable from the original - every runtime decodes it "
    "and hands back shifted values. ion.lock.json is the only mechanism that can see it, and "
    "it does: ION0021 on both edits."
)

OPEN_ENUM = (
    "An enum value that fits the declared base type but is not a declared member is carried "
    "through and re-encoded byte-identically, so adding a member does not break an older "
    "reader. A value that does not fit the base type stays a typed error."
)

VERDICTS = [
    # Resolved. Every one of these now behaves identically in all three runtimes; the entries are
    # kept rather than deleted so a regression names the thing that used to be wrong.
    ("prefix", "enum-added.forward.unknown", "correct", OPEN_ENUM),
    ("exact", "union-added.arity3", "correct", None),
    ("exact", "union.arity3.corrupts-next-field", "correct", None),
    ("exact", "indefinite.array.message", "correct", None),
    ("prefix", "union-added.forward.unknown", "correct", None),
    ("exact", "length.underclaim.negative-skip", "correct", None),
    ("exact", "indefinite.text", "correct", None),
    ("exact", "huge.length.array", "correct", None),
    ("exact", "deep.nesting.skipped-tail-100k", "correct", None),
    # Behaviour caught up with the spec: both are now a typed error in all three runtimes.
    ("exact", "indefinite.array.inner", "correct", None),
    ("exact", "widened.forward.out-of-range", "correct", None),
    # ── field appended at the end: the one safe edit ─────────────────────────
    ("prefix", "appended.forward.", "correct",
     None),
    ("exact", "argument-added.forward", "correct", None),
    ("prefix", "appended.backward.", "correct", None),
    ("exact", "argument-added.backward", "correct", None),

    # ── the silent-corruption class ──────────────────────────────────────────
    ("prefix", "inserted.forward.same-type", "acceptable", LOCKED),

    ("prefix", "reordered.forward.same-type", "acceptable", LOCKED),

    ("prefix", "reordered.backward.same-type", "acceptable", LOCKED),

    ("exact", "inserted.backward.same-type", "correct", None),

    ("prefix", "inserted.", "correct", None),

    ("prefix", "removed.", "correct", None),

    ("prefix", "reordered.", "correct", None),

    ("prefix", "retyped.", "correct", None),

    ("exact", "widened.forward.in-range", "acceptable",
     "Nothing to fix on the wire - CBOR integers carry no width, so i4 -> i8 is invisible until a "
     "value exceeds the old range. Worth knowing: this edit passes every test until production "
     "data grows."),
    ("exact", "widened.forward.out-of-range", "divergence",
     "All three must reject an integer outside the declared type's range with a typed range "
     "error. TypeScript accepts 2^40 into an `i4` field and hands it to the caller - the only "
     "runtime of the three that does not range-check an integer at all."),
    ("exact", "widened.backward", "correct", None),

    # ── optionality ──────────────────────────────────────────────────────────
    ("prefix", "optional-added.forward", "correct", None),
    ("exact", "optional-added.backward", "correct", None),
    ("exact", "optional-tightened.forward.null", "correct", None),
    ("exact", "optional-tightened.forward.set", "correct", None),
    ("exact", "optional-tightened.backward", "correct", None),

    # ── T[] <-> T[N] ─────────────────────────────────────────────────────────
    ("prefix", "array-to-fixed.forward.wrong-length", "correct", None),
    ("prefix", "array-to-fixed.", "correct", None),
    ("prefix", "fixed.direct.", "correct", None),

    # ── enums ────────────────────────────────────────────────────────────────
    ("exact", "enum-added.forward.known", "correct", None),
    ("exact", "enum-added.forward.known.set", "correct", None),
    ("exact", "enum-added.backward", "correct", None),
    ("prefix", "enum-added.forward.unknown", "divergence",
     "Three different answers to the same bytes. C# generates an unchecked cast, so an unknown "
     "value becomes an enum instance outside the declared set and flows on silently - a `switch` "
     "over it falls through every arm. TypeScript throws a bare Error('invalid enum type'). Rust "
     "generates a closed enum and returns the typed IonError::InvalidEnum. They must converge, "
     "and the choice is a language-design one: either every runtime rejects an unknown value with "
     "a typed error (and adding an enum member becomes a breaking change the lock file must "
     "police), or every runtime carries it in an explicit `Unknown(u8)` arm. What cannot stand is "
     "C# accepting what Rust refuses - the same payload succeeds on one peer and fails on "
     "another."),

    # ── unions ───────────────────────────────────────────────────────────────
    ("exact", "union-added.forward.known", "correct", None),
    ("exact", "union-added.backward", "correct", None),
    ("prefix", "union-added.forward.unknown", "divergence",
     "An unknown union case must be a typed decode error naming the index in all three. Rust does "
     "that (IonError::InvalidUnionIndex). C# throws a bare InvalidOperationException and "
     "TypeScript `throw new Error()` with NO MESSAGE AT ALL - the generated `else throw new "
     "Error();` arm - which reaches a log as an empty string."),
    ("exact", "union-added.arity3", "defect",
     "The generated union reader reads `[index, payload]` and stops, without checking the "
     "declared length. TypeScript and Rust therefore leave the third item in the stream, which "
     "the NEXT field then reads as its own; C# happens to catch it only because "
     "System.Formats.Cbor refuses to end an array with items outstanding, and reports it as an "
     "untyped InvalidOperationException. A union envelope must either skip its trailing items the "
     "way a message does, or reject a length other than 2 with a typed error."),
    ("exact", "union.arity3.corrupts-next-field", "defect",
     "The consequence of union-added.arity3 with a field behind the union: the stray item is read "
     "as the value of the next field. Same fix."),

    # ── the negative skip ────────────────────────────────────────────────────
    ("exact", "length.underclaim.negative-skip", "divergence",
     "Three answers, two of them wrong, and the wrong ones return DATA. The array header declares "
     "two items and the schema wants three. C# is saved by System.Formats.Cbor, which refuses to "
     "read past a definite-length array, so the third field read throws - which also means "
     "`Math.Abs(skipCount)` in CborReaderEx.ReadEndArrayAndSkip is unreachable from this "
     "direction in C#, not that it is harmless. TypeScript's CborReader tracks no such bound: it "
     "reads the third field from BEYOND the array, computes skipCount = -1, takes abs(), and "
     "skips a fourth item - 5 bytes consumed for a 3-byte message. Rust's minicbor is equally "
     "unbounded and reads the third field from outside the array too; only `saturating_sub` in "
     "`skip_remaining` stops it also skipping forward, so it consumes 4. Both hand back "
     "{a:1,b:2,c:3} from an array that contained {1,2}. Required: the element count must be "
     "enforced (a definite-length array yields exactly its declared items, and running out is a "
     "typed error), and a negative skipCount must be a decode error rather than a distance."),

    # ── recursion ────────────────────────────────────────────────────────────
    ("exact", "tree.depth3", "correct", None),
    ("exact", "deep.nesting.tree", "acceptable",
     "2000 levels of a legitimately typed recursive message decode in all three - but only "
     "because the harness runs every probe on a 64 MB stack. On a default 1 MB stack this is a "
     "StackOverflowException in .NET (uncatchable, takes the process) and an abort in Rust. There "
     "is no depth limit anywhere in the three runtimes; the schema author is the only guard."),
    ("exact", "deep.nesting.skipped-tail", "acceptable",
     "The skip path is iterative in both System.Formats.Cbor and minicbor, so 2000 levels of "
     "nesting the reader never even looks at cost nothing. TypeScript's CborReader.skipValue is "
     "recursive."),
    ("exact", "deep.nesting.skipped-tail-100k", "divergence",
     "100000 levels of nesting in a field the reader only SKIPS - reachable without knowing the "
     "schema, from any peer, in 100 KB. C# and Rust walk all of it without complaint; TypeScript "
     "dies of a RangeError from its recursive skipValue. All three should refuse a payload past a "
     "declared nesting limit with a typed error, rather than one of them accidentally having a "
     "limit set by the JavaScript stack."),

    # ── truncation and lying lengths ─────────────────────────────────────────
    ("prefix", "truncated.", "correct", None),
    ("prefix", "length.overclaim.", "correct", None),

    # ── indefinite lengths ───────────────────────────────────────────────────
    ("exact", "indefinite.map", "correct", None),
    ("exact", "indefinite.set", "correct", None),
    ("exact", "indefinite.fixed", "correct", None),
    ("exact", "indefinite.text", "divergence",
     "A chunked (indefinite-length) text string is accepted by C# and TypeScript and rejected by "
     "Rust. Maps, sets and fixed arrays accept an indefinite length in all three, and "
     "partial.golden.json requires it for the Partial map, so the three should agree on strings "
     "too - and the answer that matches the rest of the file is 'accept'."),
    ("prefix", "indefinite.array", "defect",
     "A message and a `T[]` field both reject an indefinite-length array, which is the right "
     "answer - the declared length is what the trailing-skip arithmetic is computed from. But the "
     "rejection is `throw new Exception(\"undefined len array not allowed\")` straight out of "
     "generated code in C# and TypeScript. " + UNTYPED),
    ("exact", "indefinite.bytes-in-array", "correct", None),

    ("exact", "huge.length.array", "divergence",
     "Rust PANICS (`Vec::with_capacity` capacity overflow) instead of returning Err. A panic "
     "crosses an async boundary as a task abort, not as a decode failure, and in a "
     "`catch_unwind`-free server it takes the connection down. `read_array` must clamp its "
     "pre-allocation to something the input could actually contain - the remaining byte count is "
     "an upper bound on the element count - and return a typed error."),
    ("prefix", "huge.length.", "correct", None),

    # ── trailing bytes ───────────────────────────────────────────────────────
    ("prefix", "trailing.garbage", "correct", None),

    # ── wrong major types ────────────────────────────────────────────────────
    ("exact", "majortype.field0.int", "correct",
     None),
    ("exact", "majortype.field1.text", "correct", None),
    ("exact", "majortype.enum.overflow", "correct", None),
    ("exact", "majortype.enum.negative", "correct", None),
    ("exact", "majortype.set.untagged", "correct", None),
    ("exact", "majortype.set.wrong-tag", "correct", None),
    ("exact", "majortype.map.array", "correct", None),
    ("prefix", "majortype.union.", "correct", None),
    ("prefix", "majortype.", "correct", None),
]


def classify(name):
    for kind, pattern, verdict, should in VERDICTS:
        if (kind == "exact" and name == pattern) or (kind == "prefix" and name.startswith(pattern)):
            return verdict, should
    return None, None


def main():
    path = os.path.join(HERE, "compat.golden.json")
    with io.open(path, encoding="utf-8") as f:
        doc = json.load(f, object_pairs_hook=collections.OrderedDict)

    dumps = {}
    for rt in RUNTIMES:
        p = os.path.join(HERE, ".dump", "compat.%s.json" % rt)
        if not os.path.exists(p):
            sys.exit("missing recording %s - run that runtime with ION_COMPAT_RECORD=1 first" % p)
        with io.open(p, encoding="utf-8") as f:
            dumps[rt] = json.load(f)

    unclassified = []
    for section in ("evolution", "malformed"):
        rebuilt = []
        for v in doc[section]:
            name = v["name"]
            verdict, should = classify(name)
            if verdict is None:
                unclassified.append(name)
                verdict = "unclassified"

            out = collections.OrderedDict()
            for k, val in v.items():
                if k == "expect":
                    continue
                out[k] = val
            out["verdict"] = verdict
            if should:
                out["shouldBe"] = should

            expect = collections.OrderedDict()
            for rt in RUNTIMES:
                if name not in dumps[rt]:
                    sys.exit("vector '%s' missing from the %s recording" % (name, rt))
                obs = dumps[rt][name]
                ordered = collections.OrderedDict()
                for k in ("outcome", "reencodedHex", "consumed", "error", "message"):
                    if k in obs and obs[k] is not None:
                        ordered[k] = obs[k]
                expect[rt] = ordered
            out["expect"] = expect
            rebuilt.append(out)
        doc[section] = rebuilt

    # ── the cross-runtime census ────────────────────────────────────────────
    # Behavioural equality across the three runtimes is the acceptance criterion, so it gets
    # counted rather than left implicit. `divergentOutcome` is the serious list: the same bytes
    # decode on one runtime and fail on another, which means a payload one peer writes happily is
    # a hard error at the next. `divergentValue` is worse still where it is non-empty - all three
    # accepted and they do not agree on what the value is.
    divergent_acceptance, divergent_typing, divergent_value, agree = [], [], [], []
    for section in ("evolution", "malformed"):
        for v in doc[section]:
            e = v["expect"]
            outcomes = {e[rt]["outcome"] for rt in RUNTIMES}
            accepted = {rt for rt in RUNTIMES if e[rt]["outcome"] == "ok"}
            if accepted and len(accepted) < len(RUNTIMES):
                divergent_acceptance.append(v["name"])
            elif "panic" in outcomes:
                divergent_acceptance.append(v["name"])
            elif len(outcomes) > 1:
                divergent_typing.append(v["name"])
            elif outcomes == {"ok"} and (
                len({e[rt].get("reencodedHex") for rt in RUNTIMES}) > 1
                or len({e[rt].get("consumed") for rt in RUNTIMES}) > 1
            ):
                divergent_value.append(v["name"])
            else:
                agree.append(v["name"])

    doc["crossRuntime"] = collections.OrderedDict([
        ("$comment", [
            "Computed from the `expect` blocks by compat.record.py and asserted by",
            "src/tests/IonTestClientServer/CompatGoldenTests.CrossRuntimeCensus, so an `expect`",
            "block cannot be edited without the census being redone.",
            "",
            "`divergentAcceptance` is the list that matters: on those bytes at least one runtime",
            "decodes and at least one refuses (or one panics). A payload that one peer writes",
            "without complaint is a hard error at the next, and which peer you are decides",
            "whether you notice. Every entry is a wire-compatibility bug even where each",
            "runtime's own behaviour looks defensible in isolation.",
            "",
            "`divergentValue` means all three accepted the bytes and disagree about what they",
            "mean - the worst outcome available. It must stay empty.",
            "",
            "`divergentErrorTyping` is the long tail: all three reject, but one raises a typed",
            "Ion decode failure and another an untyped exception. Wire-compatible, and still a",
            "defect - a caller cannot write one handler that works against all three.",
        ]),
        ("agreeCount", len(agree)),
        ("divergentAcceptance", divergent_acceptance),
        ("divergentValue", divergent_value),
        ("divergentErrorTypingCount", len(divergent_typing)),
        ("divergentErrorTyping", divergent_typing),
    ])

    # keep the census above the 144 vectors it summarises
    reordered = collections.OrderedDict()
    for k in doc:
        if k == "evolution":
            reordered["crossRuntime"] = doc["crossRuntime"]
        if k != "crossRuntime":
            reordered[k] = doc[k]
    doc = reordered

    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, indent=2, ensure_ascii=False)
        f.write("\n")

    counts = collections.Counter(
        v["verdict"] for s in ("evolution", "malformed") for v in doc[s]
    )
    print("merged %d vectors into %s" % (sum(counts.values()), path))
    for k, n in sorted(counts.items()):
        print("  %-13s %d" % (k, n))
    if unclassified:
        print("UNCLASSIFIED (add them to VERDICTS):")
        for n in unclassified:
            print("  " + n)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
