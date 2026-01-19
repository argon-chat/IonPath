import { CborReader, CborWriter } from "../cbor";
import { IonFormatterStorage } from "../logic/IonFormatter";

IonFormatterStorage.register("u1", {
  read(reader: CborReader): number {
    return reader.readInt32() & 0xff;
  },
  write(writer: CborWriter, value: number): void {
    writer.writeInt32(value);
  },
});

IonFormatterStorage.register("u2", {
  read(reader: CborReader): number {
    return reader.readUInt32() & 0xffff;
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
