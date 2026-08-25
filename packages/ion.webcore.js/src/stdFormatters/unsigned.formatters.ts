import { CborReader, CborWriter } from "../cbor";
import { IonIntegerRangeError } from "../errors";
import { IonFormatterStorage } from "../logic/IonFormatter";

// ── Ion's unsigned integers ─────────────────────────────────────────────────────────────────────
//
// The same rule as the signed side, plus the sign itself: a CBOR negative integer is a well-formed
// item no unsigned Ion type can hold. `reader.readInt32() & 0xff` used to reinterpret -1 as 255
// and 256 as 0 — and because `enum E : u1` reads through this formatter, an enum built on a narrow
// base type inherited exactly that defect: 256 arrived as the member numbered 0.

/** Rejects `value` unless it lies in `[0, max]`. */
const inRange = (value: number, max: number, ionType: string): number => {
  if (!Number.isInteger(value) || value < 0 || value > max)
    throw new IonIntegerRangeError(ionType, value, `[0, ${max}]`);
  return value;
};

IonFormatterStorage.register("u1", {
  read(reader: CborReader): number {
    return inRange(reader.readInt32(), 255, "u1");
  },
  write(writer: CborWriter, value: number): void {
    writer.writeInt32(value);
  },
});

IonFormatterStorage.register("u2", {
  read(reader: CborReader): number {
    return inRange(reader.readInt32(), 65535, "u2");
  },
  write(writer: CborWriter, value: number): void {
    writer.writeUInt32(value);
  },
});

IonFormatterStorage.register("u4", {
  read(reader: CborReader): number {
    return reader.readUInt32();
  },
  write(writer: CborWriter, value: number): void {
    writer.writeUInt32(value);
  },
});

IonFormatterStorage.register("u8", {
  read(reader: CborReader): bigint {
    return reader.readUInt64();
  },
  write(writer: CborWriter, value: bigint): void {
    writer.writeUInt64(value);
  },
});

IonFormatterStorage.register("u16", {
  read(reader: CborReader): bigint {
    return reader.readUInt128();
  },
  write(writer: CborWriter, value: bigint): void {
    writer.writeUInt128(value);
  },
});
