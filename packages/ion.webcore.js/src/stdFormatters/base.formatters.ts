import {
  DateOnly,
  Duration,
  Guid,
  IonBytes,
  IonDateTime,
  IonDecimal,
  TimeOnly,
} from "../baseTypes";
import { CborReader, CborWriter } from "../cbor";
import { CborReaderState } from "../cbor/CborReader";
import {
  IonDateTimeFormatError,
  IonMalformedValueError,
  IonUnexpectedTagError,
} from "../errors";
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

/** CBOR tag 0: "standard date/time string". */
const DATETIME_TAG = 0;

/** CBOR tag 4: decimal fraction. */
const DECIMAL_TAG = 4;

/**
 * `datetime` — CBOR tag 0 wrapping an RFC 3339 date-time, always with an explicit numeric offset
 * and always with exactly seven fractional-second digits.
 *
 * **This replaces a broken encoding.** The previous implementation wrote
 * `value.date.toISOString()`, i.e. millisecond resolution, so the 100ns ticks a C# peer routinely
 * sends were silently truncated on every round trip; and it returned a `{ date: Date }`, which
 * cannot hold them either. The value type is now {@link IonDateTime}. See
 * `/tests/golden/datetime.golden.json` — including the wire-breaking notice, since Rust used to
 * write a completely different shape (`[ticks, offsetMinutes]`) and could not interoperate at all.
 *
 * Readers accept any fractional precision (truncating past the seventh digit, never rounding),
 * `Z` as well as a numeric offset, and a lower-case `t` or a space separator. Tag 0 itself is
 * **required**: a bare text string is exactly how Ion's `string` encodes, so accepting one would
 * make `datetime` and `string` indistinguishable in a capture.
 */
IonFormatterStorage.register("datetime", {
  read(reader: CborReader): IonDateTime {
    if (reader.peekState() !== CborReaderState.Tag)
      throw new IonMalformedValueError(
        "datetime",
        `expected CBOR tag ${DATETIME_TAG}, got a value with no tag`
      );

    const tag = reader.readTag();
    if (Number(tag) !== DATETIME_TAG)
      throw new IonUnexpectedTagError(DATETIME_TAG, tag, "datetime");

    if (reader.peekState() !== CborReaderState.TextString)
      throw new IonMalformedValueError("datetime", "tag 0 must wrap a text string");

    const text = reader.readTextString();
    try {
      return IonDateTime.fromString(text);
    } catch (cause) {
      throw new IonDateTimeFormatError(
        text,
        cause instanceof Error ? cause.message : String(cause),
        { cause }
      );
    }
  },
  write(writer: CborWriter, value: IonDateTime): void {
    writer.writeTag(DATETIME_TAG);
    writer.writeTextString(value.toString());
  },
});

/**
 * `decimal` — CBOR tag 4 (decimal fraction) wrapping `[exponent, mantissa]`, whose value is
 * `mantissa × 10^exponent`.
 *
 * The mantissa is **normalised on write** (trailing zeros stripped, zero as `[0, 0]`) so that
 * `1.50` and `1.5` produce identical bytes. Without that rule a C# `1.50m`, which remembers its
 * scale, could emit a form neither TypeScript nor Rust is able to reproduce.
 *
 * Readers accept an indefinite-length inner array, a non-normalised mantissa and a bignum holding
 * a value that would have fitted a plain integer. Tag 4 is **required**: an untagged
 * `[exponent, mantissa]` array is exactly the encoding of an `i8[2]`.
 *
 * TypeScript has no range limit here — the mantissa is a native `bigint` — so this formatter
 * decodes values that C# must reject as out of `System.Decimal`'s range. See
 * `/tests/golden/decimal.golden.json`.
 */
IonFormatterStorage.register("decimal", {
  read(reader: CborReader): IonDecimal {
    if (reader.peekState() !== CborReaderState.Tag)
      throw new IonMalformedValueError(
        "decimal",
        `expected CBOR tag ${DECIMAL_TAG}, got a value with no tag`
      );

    const tag = reader.readTag();
    if (Number(tag) !== DECIMAL_TAG)
      throw new IonUnexpectedTagError(DECIMAL_TAG, tag, "decimal");

    if (reader.peekState() !== CborReaderState.StartArray)
      throw new IonMalformedValueError("decimal", "tag 4 must wrap an array");

    const length = reader.readStartArray();
    if (length !== null && length !== 2)
      throw new IonMalformedValueError(
        "decimal",
        `tag 4 requires exactly 2 elements, got ${length}`
      );

    const exponent = reader.readInt32();
    const mantissa = reader.readBigInteger();

    // An indefinite-length inner array is accepted, but must still hold exactly two items.
    if (length === null && reader.peekState() !== CborReaderState.EndArray)
      throw new IonMalformedValueError("decimal", "tag 4 requires exactly 2 elements, got more");

    reader.readEndArray();
    return new IonDecimal(exponent, mantissa).normalized();
  },
  write(writer: CborWriter, value: IonDecimal): void {
    const canonical = value.normalized();
    writer.writeTag(DECIMAL_TAG);
    writer.writeStartArray(2);
    writer.writeInt32(canonical.exponent);
    writer.writeBigInteger(canonical.mantissa);
    writer.writeEndArray();
  },
});