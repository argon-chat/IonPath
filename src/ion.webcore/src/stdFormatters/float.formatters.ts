import { CborReader, CborWriter } from "../cbor";
import { IonFormatter, IonFormatterStorage } from "../logic/IonFormatter";


const f2Formatter: IonFormatter<number> = {
  read(reader: CborReader): number {
    return reader.readHalf();
  },
  write(writer: CborWriter, value: number): void {
    writer.writeHalf(value);
  }
};
IonFormatterStorage.register("f2", f2Formatter);

const f4Formatter: IonFormatter<number> = {
  read(reader: CborReader): number {
    return reader.readSingle();
  },
  write(writer: CborWriter, value: number): void {
    writer.writeSingle(value);
  }
};
IonFormatterStorage.register("f4", f4Formatter);

const f8Formatter: IonFormatter<number> = {
  read(reader: CborReader): number {
    return reader.readDouble();
  },
  write(writer: CborWriter, value: number): void {
    writer.writeDouble(value);
  }
};
IonFormatterStorage.register("f8", f8Formatter);