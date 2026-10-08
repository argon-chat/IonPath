import { describe, expect, it } from "vitest";
import {
  CborReader,
  CborWriter,
  type DateOnly,
  IonFormatterStorage,
  type TimeOnly,
} from "../src";
import "../src/index";

// `dateonly` and `timeonly` are CBOR arrays of integers on every runtime:
//   Ion_dateonly_Formatter / Ion_timeonly_Formatter   (src/ion.runtime/IonFormatter.cs)
//   IonDateOnly / IonTimeOnly                          (packages/ion.rustcore/src/std_formatters/base.rs)
// Until 2.0.1 this runtime wrote and read `timeonly` as five bare integers, so a .NET server's
// 06:00 (`85 06 00 00 00 00`) failed on the array header and a browser-written value was
// unreadable on the server. These vectors pin the framing to what .NET and Rust emit.

const toHex = (bytes: Uint8Array) =>
  [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");

const fromHex = (hex: string) =>
  new Uint8Array((hex.match(/../g) ?? []).map((b) => Number.parseInt(b, 16)));

const encode = <T>(name: string, value: T) => {
  const writer = new CborWriter();
  IonFormatterStorage.get<T>(name).write(writer, value);
  return toHex(writer.data);
};

const decode = <T>(name: string, hex: string) =>
  IonFormatterStorage.get<T>(name).read(new CborReader(fromHex(hex)));

describe("timeonly framing", () => {
  const vectors: Array<{ value: TimeOnly; hex: string }> = [
    // Venue.dayBoundary 06:00 as a .NET server sends it.
    { value: { hour: 6, minute: 0, second: 0, millisecond: 0, microsecond: 0 }, hex: "850600000000" },
    { value: { hour: 0, minute: 0, second: 0, millisecond: 0, microsecond: 0 }, hex: "850000000000" },
    // 23:59:59.999999 — the last two fields need multi-byte integers.
    {
      value: { hour: 23, minute: 59, second: 59, millisecond: 999, microsecond: 999 },
      hex: "85" + "17" + "183b" + "183b" + "1903e7" + "1903e7",
    },
  ];

  for (const v of vectors) {
    it(`encodes ${v.value.hour}:${v.value.minute}:${v.value.second} as a definite array of five`, () => {
      expect(encode("timeonly", v.value)).toEqual(v.hex);
    });

    it(`decodes ${v.hex}`, () => {
      expect(decode<TimeOnly>("timeonly", v.hex)).toEqual(v.value);
    });
  }

  it("round-trips through its own bytes", () => {
    const value: TimeOnly = { hour: 12, minute: 34, second: 56, millisecond: 789, microsecond: 12 };
    expect(decode<TimeOnly>("timeonly", encode("timeonly", value))).toEqual(value);
  });

  it("accepts an indefinite-length array", () => {
    expect(decode<TimeOnly>("timeonly", "9f0600000000ff")).toEqual({
      hour: 6,
      minute: 0,
      second: 0,
      millisecond: 0,
      microsecond: 0,
    });
  });

  it("rejects the pre-2.0.1 bare-integer shape", () => {
    expect(() => decode<TimeOnly>("timeonly", "0600000000")).toThrow();
  });
});

describe("dateonly framing", () => {
  it("encodes as a definite array of four with the reserved calendar slot", () => {
    expect(encode<DateOnly>("dateonly", { year: 2026, month: 10, day: 8 })).toEqual(
      "84" + "1907ea" + "0a" + "08" + "00"
    );
  });

  it("decodes what .NET writes", () => {
    expect(decode<DateOnly>("dateonly", "841907ea0a0800")).toEqual({
      year: 2026,
      month: 10,
      day: 8,
    });
  });

  it("round-trips", () => {
    const value: DateOnly = { year: 1999, month: 12, day: 31 };
    expect(decode<DateOnly>("dateonly", encode("dateonly", value))).toEqual(value);
  });
});
