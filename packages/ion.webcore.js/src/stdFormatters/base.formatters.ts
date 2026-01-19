import {
  bytes,
  DateOnly,
  DateTimeOffset,
  Duration,
  Guid,
  IonBytes,
  TimeOnly,
} from "../baseTypes";
import { CborReader, CborWriter } from "../cbor";
import { IonFormatterStorage } from "../logic/IonFormatter";

IonFormatterStorage.register("bool", {
  read(reader: CborReader): boolean {
    return reader.readBoolean();
  },
  write(writer: CborWriter, value: boolean): void {
    writer.writeBoolean(value);
  },
});

IonFormatterStorage.register("string", {
  read(reader: CborReader): string {
    return reader.readTextString();
  },
  write(writer: CborWriter, value: string): void {
    writer.writeTextString(value);
  },
});

IonFormatterStorage.register("bytes", {
  read(reader: CborReader): IonBytes {
    return reader.readByteString();
  },
  write(writer: CborWriter, value: IonBytes): void {
    writer.writeByteString(value);
  },
});

IonFormatterStorage.register("guid", {
  read(reader: CborReader): Guid {
    const bytes = reader.readByteString();
    if (bytes.length !== 16) throw new Error("Expected 16-byte GUID");
    const hex = [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
    return (
      hex.substring(0, 8) +
      "-" +
      hex.substring(8, 12) +
      "-" +
      hex.substring(12, 16) +
      "-" +
      hex.substring(16, 20) +
      "-" +
      hex.substring(20)
    );
  },
  write(writer: CborWriter, value: Guid): void {
    const hex = value.replace(/-/g, "");
    if (hex.length !== 32) throw new Error("Invalid GUID format");
    const bytes = new Uint8Array(16);
    for (let i = 0; i < 16; i++) {
      bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
    }
    writer.writeByteString(bytes);
  },
});

IonFormatterStorage.register("dateonly", {
  read(reader: CborReader): DateOnly {
    reader.readStartArray();
    const y = reader.readInt32();
    const m = reader.readInt32();
    const d = reader.readInt32();
    reader.readInt32();
    reader.readEndArray();
    return { year: y, month: m, day: d };
  },
  write(writer: CborWriter, value: DateOnly): void {
    writer.writeStartArray(null);
    writer.writeInt32(value.year);
    writer.writeInt32(value.month);
    writer.writeInt32(value.day);
    writer.writeInt32(0);
    writer.writeEndArray();
  },
});

IonFormatterStorage.register("timeonly", {
  read(reader: CborReader): TimeOnly {
    const h = reader.readInt32();
    const m = reader.readInt32();
    const s = reader.readInt32();
    const ms = reader.readInt32();
    const µs = reader.readInt32();
    return { hour: h, minute: m, second: s, millisecond: ms, microsecond: µs };
  },
  write(writer: CborWriter, value: TimeOnly): void {
    writer.writeInt32(value.hour);
    writer.writeInt32(value.minute);
    writer.writeInt32(value.second);
    writer.writeInt32(value.millisecond);
    writer.writeInt32(value.microsecond);
  },
});

IonFormatterStorage.register("duration", {
  read(reader: CborReader): Duration {
    return { ticks: reader.readInt64() };
  },
  write(writer: CborWriter, value: Duration): void {
    writer.writeInt64(value.ticks);
  },
});

IonFormatterStorage.register("datetime", {
  read(reader: CborReader): DateTimeOffset {
    const tag = reader.readTag();
    if (tag !== 0) {
      throw new Error(`Unexpected CBOR tag ${tag}, expected 0 (datetime)`);
    }
    const iso = reader.readTextString();
    const d = new Date(iso);
    const match = iso.match(/([+-]\d{2}:\d{2}|Z)$/);
    let offsetMinutes = 0;
    if (match) {
      if (match[0] === "Z") offsetMinutes = 0;
      else {
        const [h, m] = match[0].split(":");
        offsetMinutes = parseInt(h, 10) * 60 + parseInt(m, 10);
      }
    }
    return { date: d, offsetMinutes };
  },
  write(writer: CborWriter, value: DateTimeOffset): void {
    const iso = value.date.toISOString();
    writer.writeTag(0);
    writer.writeTextString(iso);
  },
});


IonFormatterStorage.register("bytes", {
  read(reader: CborReader): bytes {
    return reader.readByteString();
  },
  write(writer: CborWriter, value: bytes): void {
    writer.writeByteString(value);
  },
});