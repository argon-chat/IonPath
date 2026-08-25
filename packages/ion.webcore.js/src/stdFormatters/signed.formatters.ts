import { CborReader, CborWriter } from "../cbor";
import { IonIntegerRangeError } from "../errors";
import { IonFormatterStorage } from "../logic/IonFormatter";

// ── Ion's signed integers ───────────────────────────────────────────────────────────────────────
//
// CBOR integers carry no width, so `i1`, `i2` and `i4` are all narrower than what the wire can
// hold and the reader is the only place the declared width can be enforced. These used to be
// sign-extension tricks — `(reader.readInt32() << 24) >> 24` — which quietly turned an out-of-range
// value into a different, in-range one, and `readInt32` itself checked nothing at all.
//
// Writers are untouched: an in-range value produces exactly the bytes it did before.

/** Rejects `value` unless it lies in `[min, max]`. */
const inRange = (value: number, min: number, max: number, ionType: string): number => {
  if (!Number.isInteger(value) || value < min || value > max)
    throw new IonIntegerRangeError(ionType, value, `[${min}, ${max}]`);
  return value;
};

IonFormatterStorage.register("i1", {
  read(reader: CborReader): number {
    return inRange(reader.readInt32(), -128, 127, "i1");
  },
  write(writer: CborWriter, value: number): void {
    writer.writeInt32(value);
  },
});

IonFormatterStorage.register("i2", {
  read(reader: CborReader): number {
    return inRange(reader.readInt32(), -32768, 32767, "i2");
  },
  write(writer: CborWriter, value: number): void {
    writer.writeInt32(value);
  },
});

IonFormatterStorage.register("i4", {
  read(reader: CborReader): number {
    // readInt32 already enforces the 32-bit range.
    return reader.readInt32();
  },
  write(writer: CborWriter, value: number): void {
    writer.writeInt32(value);
  },
});

IonFormatterStorage.register("i8", {
  read(reader: CborReader): bigint {
    return reader.readInt64();
  },
  write(writer: CborWriter, value: bigint): void {
    writer.writeInt64(value);
  },
});

IonFormatterStorage.register("i16", {
  read(reader: CborReader): bigint {
    return reader.readInt128();
  },
  write(writer: CborWriter, value: bigint): void {
    writer.writeInt128(value);
  },
});
