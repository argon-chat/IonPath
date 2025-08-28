import { BinaryWriter } from "../binary/BinaryWriter";

export class CborWriter {
  private w: BinaryWriter;
  private stack: Array<{ type: "array" | "map"; definite: boolean }> = [];

  constructor(initialSize = 64) {
    this.w = new BinaryWriter(initialSize);
  }

  get data(): Uint8Array {
    return this.w.data;
  }

  private writeTypeAndLength(majorType: number, value: number | bigint | null) {
    if (value === null) {
      this.w.writeUint8((majorType << 5) | 31); // indefinite
      return;
    }
    if (typeof value === "number") {
      if (value < 24) {
        this.w.writeUint8((majorType << 5) | value);
      } else if (value <= 0xff) {
        this.w.writeUint8((majorType << 5) | 24);
        this.w.writeUint8(value);
      } else if (value <= 0xffff) {
        this.w.writeUint8((majorType << 5) | 25);
        this.w.writeUint16(value);
      } else if (value <= 0xffffffff) {
        this.w.writeUint8((majorType << 5) | 26);
        this.w.writeUint32(value);
      } else {
        this.w.writeUint8((majorType << 5) | 27);
        this.w.writeBigUint64(BigInt(value));
      }
    } else {
      this.w.writeUint8((majorType << 5) | 27);
      this.w.writeBigUint64(value);
    }
  }

  writeInt32(value: number) {
    if (value >= 0) this.writeTypeAndLength(0, value);
    else this.writeTypeAndLength(1, -1 - value);
  }

  writeUInt32(value: number) {
    if (value < 0) throw new Error("Value must be unsigned");
    this.writeTypeAndLength(0, value);
  }

  writeInt64(value: bigint) {
    if (value >= 0n) this.writeTypeAndLength(0, value);
    else this.writeTypeAndLength(1, -1n - value);
  }

  writeInt128(value: bigint) {
    if (value <= 0x7fff_ffff_ffff_ffffn && value >= -0x8000_0000_0000_0000n) {
      this.writeInt64(value);
      return;
    }

    let tag: number;
    let abs: bigint;

    if (value >= 0n) {
      tag = 2;
      abs = value;
    } else {
      tag = 3;
      abs = -1n - value;
    }

    this.writeTypeAndLength(6, tag);

    const bytes: number[] = [];
    let tmp = abs;
    while (tmp > 0n) {
      bytes.push(Number(tmp & 0xffn));
      tmp >>= 8n;
    }
    if (bytes.length === 0) {
      bytes.push(0);
    }
    bytes.reverse();

    this.writeTypeAndLength(2, bytes.length);
    this.w.writeBytes(new Uint8Array(bytes));
  }

  writeUInt128(value: bigint) {
    if (value < 0n) throw new Error("Value must be unsigned");

    if (value <= 0xffff_ffff_ffff_ffffn) {
      this.writeUInt64(value);
      return;
    }

    this.writeTypeAndLength(6, 2);

    const bytes: number[] = [];
    let tmp = value;
    while (tmp > 0n) {
      bytes.push(Number(tmp & 0xffn));
      tmp >>= 8n;
    }
    if (bytes.length === 0) {
      bytes.push(0);
    }
    bytes.reverse();

    this.writeTypeAndLength(2, bytes.length);
    this.w.writeBytes(new Uint8Array(bytes));
  }

  writeUInt64(value: bigint) {
    this.writeTypeAndLength(0, value);
  }

  writeHalf(value: number) {
    this.w.writeUint8((7 << 5) | 25);
    this.w.writeFloat32(value);
  }

  writeSingle(value: number) {
    this.w.writeUint8((7 << 5) | 26);
    this.w.writeFloat32(value);
  }

  writeDouble(value: number) {
    this.w.writeUint8((7 << 5) | 27);
    this.w.writeFloat64(value);
  }

  writeBoolean(value: boolean) {
    this.w.writeUint8(value ? 0xf5 : 0xf4);
  }

  writeNull() {
    this.w.writeUint8(0xf6);
  }

  writeUndefined() {
    this.w.writeUint8(0xf7);
  }

  writeTag(tag: number | bigint) {
    this.writeTypeAndLength(6, tag);
  }

  writeTextString(str: string) {
    const bytes = new TextEncoder().encode(str);
    this.writeTypeAndLength(3, bytes.length);
    this.w.writeBytes(bytes);
  }

  writeStartTextString() {
    this.writeTypeAndLength(3, null);
  }

  writeTextStringChunk(str: string) {
    const bytes = new TextEncoder().encode(str);
    this.writeTypeAndLength(3, bytes.length);
    this.w.writeBytes(bytes);
  }

  writeEndTextString() {
    this.w.writeUint8(0xff);
  }

  writeByteString(bytes: Uint8Array) {
    this.writeTypeAndLength(2, bytes.length);
    this.w.writeBytes(bytes);
  }

  writeStartArray(length: number | null = null) {
    this.writeTypeAndLength(4, length);
    this.stack.push({ type: "array", definite: length !== null });
  }

  writeUndefineds(len: number) {
    for (let index = 0; index < len; index++) {
      this.writeUndefined();
    }
  }

  writeEndArray() {
    const ctx = this.stack.pop();
    if (!ctx || ctx.type !== "array")
      throw new Error("Mismatched WriteEndArray");
    if (!ctx.definite) {
      this.w.writeUint8(0xff);
    }
  }

  writeStartMap(length: number | null = null) {
    this.writeTypeAndLength(5, length);
    this.stack.push({ type: "map", definite: length !== null });
  }

  writeEndMap() {
    const ctx = this.stack.pop();
    if (!ctx || ctx.type !== "map") throw new Error("Mismatched WriteEndMap");
    if (!ctx.definite) {
      this.w.writeUint8(0xff);
    }
  }
}
