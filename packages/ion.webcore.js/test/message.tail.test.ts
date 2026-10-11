import { describe, expect, it } from "vitest";
import { CborReader, IonFieldCountError, IonFormatterStorage } from "../src";
import "../src/index";

// The runtime half of "a nullable field appended to a shipped message works in both directions".
// The generated-code half — messages, union case payloads and argument arrays, read short and
// long — is pinned across all three runtimes by the `nullable-appended.*` and
// `nullable-argument-added.*` vectors in /tests/golden/compat.golden.json.

const reader = (hex: string) =>
  new CborReader(new Uint8Array((hex.match(/../g) ?? []).map((b) => Number.parseInt(b, 16))));

describe("readStartMessage / readEndMessage", () => {
  it("accepts a payload short by nullable fields and closes it without skipping", () => {
    // [1] against a three-field schema that requires one.
    const r = reader("8101");
    const declared = IonFormatterStorage.readStartMessage(r, 3, "Msg", 1);
    expect(declared).toBe(1);
    expect(r.readInt32()).toBe(1);
    IonFormatterStorage.readEndMessage(r, declared, 3);
    expect(r.position).toBe(2);
  });

  it("still raises the typed field-count error below the required count", () => {
    const r = reader("8101");
    let error: unknown;
    try {
      IonFormatterStorage.readStartMessage(r, 3, "Msg", 2);
    } catch (e) {
      error = e;
    }
    expect(error).toBeInstanceOf(IonFieldCountError);
    expect((error as IonFieldCountError).expectedFields).toBe(2);
    expect((error as IonFieldCountError).actualFields).toBe(1);
  });

  it("skips a longer payload's tail", () => {
    // [1, true, true] against a two-field schema.
    const r = reader("8301f5f5");
    const declared = IonFormatterStorage.readStartMessage(r, 2, "Msg", 1);
    expect(r.readInt32()).toBe(1);
    expect(r.readBoolean()).toBe(true);
    IonFormatterStorage.readEndMessage(r, declared, 2);
    expect(r.position).toBe(4);
  });

  it("keeps the three-argument form, which code generated before 2.0.3 calls, strict", () => {
    expect(() => IonFormatterStorage.readStartMessage(reader("8101"), 2, "Msg")).toThrow(
      IonFieldCountError
    );
  });

  it("rejects a required count outside the declared range as a programming error", () => {
    expect(() => IonFormatterStorage.readStartMessage(reader("8101"), 1, "Msg", 2)).toThrow(RangeError);
    expect(() => IonFormatterStorage.readStartMessage(reader("8101"), 1, "Msg", -1)).toThrow(RangeError);
  });
});
