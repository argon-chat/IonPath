import { CborReader, CborWriter } from "../cbor";
import { IonFormatterStorage } from "../logic/IonFormatter";

IonFormatterStorage.register("i1", {
  read(reader: CborReader): number {
    return (reader.readInt32() << 24) >> 24;
  },
  write(writer: CborWriter, value: number): void {
    writer.writeInt32(value);
  },
});

IonFormatterStorage.register("i2", {
  read(reader: CborReader): number {
    return (reader.readInt32() << 16) >> 16;
  },
  write(writer: CborWriter, value: number): void {
    writer.writeInt32(value);
  },
});

IonFormatterStorage.register("i4", {
  read(reader: CborReader): number {
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
