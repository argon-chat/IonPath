import { BinaryReader } from "../binary/BinaryReader";

export enum CborReaderState {
  UnsignedInteger,
  NegativeInteger,
  ByteString,
  TextString,
  StartArray,
  StartMap,
  Tag,
  FloatingPointNumber,
  Boolean,
  Null,
  Undefined,
  SimpleValue,
  EndArray,
  EndMap,
  Finished,
}

export class CborReader {
  private r: BinaryReader;
  private stack: Array<{
    type: "array" | "map";
    definite: boolean;
    remaining: number | null;
  }> = [];
  private finished = false;

  constructor(buffer: ArrayBuffer | Uint8Array) {
    this.r = new BinaryReader(buffer);
  }

  get position() {
    return this.r.position;
  }

  get hasData() {
    return !this.finished && this.r.position < (this.r as any).view.byteLength;
  }

  peekState(): CborReaderState {
    if (!this.hasData) return CborReaderState.Finished;
    const b = this.peekByte();
    const mt = b >> 5;
    const ai = b & 0x1f;

    switch (mt) {
      case 0:
        return CborReaderState.UnsignedInteger;
      case 1:
        return CborReaderState.NegativeInteger;
      case 2:
        return CborReaderState.ByteString;
      case 3:
        return CborReaderState.TextString;
      case 4:
        return CborReaderState.StartArray;
      case 5:
        return CborReaderState.StartMap;
      case 6:
        return CborReaderState.Tag;
      case 7:
        if (ai === 25 || ai === 26 || ai === 27)
          return CborReaderState.FloatingPointNumber;

        if (ai === 20 || ai === 21) return CborReaderState.Boolean;
        if (ai === 22) return CborReaderState.Null;
        if (ai === 23) return CborReaderState.Undefined;

        if (ai === 31) {
          if (this.stack.length) {
            const top = this.stack[this.stack.length - 1];
            if (top.type === "array") return CborReaderState.EndArray;
            if (top.type === "map") return CborReaderState.EndMap;
          }
        }

        return CborReaderState.SimpleValue;
      default:
        return CborReaderState.Finished;
    }
  }

  private readLength(ai: number): number | bigint | null {
    if (ai < 24) return ai;
    if (ai === 24) return this.r.readUint8();
    if (ai === 25) return this.r.readUint16();
    if (ai === 26) return this.r.readUint32();
    if (ai === 27) return this.r.readBigUint64();
    if (ai === 31) return null;
    throw new Error("invalid length encoding");
  }

  // -------------------
  // Integers
  // -------------------
  readInt32(): number {
    const b = this.r.readUint8();
    const mt = b >> 5;
    const ai = b & 0x1f;
    const len = this.readLength(ai);
    if (mt === 0) return typeof len === "bigint" ? Number(len) : len!;
    if (mt === 1)
      return typeof len === "bigint" ? Number(-1n - len) : -1 - len!;
    throw new Error("Not an integer");
  }

  readInt64(): bigint {
    const b = this.r.readUint8();
    const mt = b >> 5;
    const ai = b & 0x1f;
    const len = this.readLength(ai)!;
    if (mt === 0) return typeof len === "bigint" ? len : BigInt(len);
    if (mt === 1) return typeof len === "bigint" ? -1n - len : BigInt(-1 - len);
    throw new Error("Not an integer");
  }

  readInt128(): bigint {
    const b = this.r.readUint8();
    const mt = b >> 5;
    const ai = b & 0x1f;

    if (mt === 0) {
      const len = this.readLength(ai);
      if (typeof len === "bigint") return len;
      return BigInt(len!);
    }

    if (mt === 1) {
      const len = this.readLength(ai);
      if (typeof len === "bigint") return -1n - len;
      return BigInt(-1 - len!);
    }

    if (mt === 6) {
      const tag = this.readLength(ai);
      if (tag !== 2 && tag !== 3) throw new Error("Not a bignum tag");
      const bytes = this.readByteString();
      let val = 0n;
      for (const byte of bytes) {
        val = (val << 8n) | BigInt(byte);
      }
      if (tag === 2) return val;
      else return -1n - val;
    }
    throw new Error("Not an int128/bignum");
  }

  readUInt32(): number {
    const b = this.r.readUint8();
    const mt = b >> 5;
    const ai = b & 0x1f;
    const len = this.readLength(ai);
    if (mt === 0) return typeof len === "bigint" ? Number(len) : len!;
    throw new Error("Not an unsigned integer");
  }

  readUInt64(): bigint {
    const b = this.r.readUint8();
    const mt = b >> 5;
    const ai = b & 0x1f;
    const len = this.readLength(ai)!;
    if (mt === 0) return typeof len === "bigint" ? len : BigInt(len);
    throw new Error("Not an unsigned integer");
  }

  readUInt128(): bigint {
    const b = this.r.readUint8();
    const mt = b >> 5;
    const ai = b & 0x1f;

    if (mt === 0) {
      const len = this.readLength(ai);
      return typeof len === "bigint" ? len : BigInt(len!);
    }

    if (mt === 6) {
      const tag = this.readLength(ai);
      if (tag !== 2) throw new Error("Not an unsigned bignum tag");
      const bytes = this.readByteString();
      let val = 0n;
      for (const byte of bytes) {
        val = (val << 8n) | BigInt(byte);
      }
      return val;
    }

    throw new Error("Not an unsigned int128/bignum");
  }

  // -------------------
  // Floats
  // -------------------
  readDouble(): number {
    const b = this.r.readUint8();
    const mt = b >> 5;
    const ai = b & 0x1f;
    if (mt !== 7) throw new Error("Not a float");
    if (ai === 25) return this.r.readFloat16();
    if (ai === 26) return this.r.readFloat32();
    if (ai === 27) return this.r.readFloat64();
    throw new Error("Unsupported float");
  }

  readSingle(): number {
    return this.readDouble();
  }

  readHalf(): number {
    return this.readDouble();
  }

  // -------------------
  // Boolean / null / undef
  // -------------------
  readBoolean(): boolean {
    const b = this.r.readUint8();
    if (b === 0xf4) return false;
    if (b === 0xf5) return true;
    throw new Error("Not a boolean");
  }

  readNull(): null {
    const b = this.r.readUint8();
    if (b !== 0xf6) throw new Error("Not null");
    return null;
  }

  readUndefined(): undefined {
    const b = this.r.readUint8();
    if (b !== 0xf7) throw new Error("Not undefined");
    return undefined;
  }

  // -------------------
  // Byte strings
  // -------------------
  readByteString(): Uint8Array {
    const b = this.r.readUint8();
    const ai = b & 0x1f;
    if (b >> 5 !== 2) throw new Error("Not a byte string");
    const len = this.readLength(ai);
    if (len === null) {
      const chunks: Uint8Array[] = [];
      while (this.peekByte() !== 0xff) {
        chunks.push(this.readByteString());
      }
      this.r.readUint8(); // break
      return Uint8Array.from(chunks.flatMap((x) => [...x]));
    }
    return this.r.readBytes(len as number);
  }

  // -------------------
  // Text strings
  // -------------------
  readStartTextString(): void {
    const b = this.r.readUint8();
    if (b >> 5 !== 3 || (b & 0x1f) !== 31)
      throw new Error("Not an indefinite text string");
  }

  readTextStringChunk(): string {
    const b = this.r.readUint8();
    if (b >> 5 !== 3) throw new Error("Not a text string chunk");
    const ai = b & 0x1f;
    const len = this.readLength(ai);
    if (len === null)
      throw new Error("Nested indefinite string not allowed here");
    const bytes = this.r.readBytes(len as number);
    return new TextDecoder("utf-8").decode(bytes);
  }

  readEndTextString(): void {
    const b = this.r.readUint8();
    if (b !== 0xff) throw new Error("Expected break for text string");
  }

  readTextString(): string {
    const b = this.r.readUint8();
    if (b >> 5 !== 3) throw new Error("Not a text string");
    const ai = b & 0x1f;
    const len = this.readLength(ai);

    if (len === null) {
      let s = "";
      while (this.peekByte() !== 0xff) {
        s += this.readTextStringChunk();
      }
      this.readEndTextString();
      return s;
    }

    const bytes = this.r.readBytes(len as number);
    return new TextDecoder("utf-8").decode(bytes);
  }

  // -------------------
  // Arrays
  // -------------------
  readStartArray(): number | null {
    const b = this.r.readUint8();
    if (b >> 5 !== 4) throw new Error("Not array");
    const ai = b & 0x1f;
    const len = this.readLength(ai);
    this.stack.push({
      type: "array",
      definite: len !== null,
      remaining: len === null ? null : Number(len),
    });
    return len === null ? null : Number(len);
  }

  readEndArray() {
    const ctx = this.stack.pop();
    if (!ctx || ctx.type !== "array")
      throw new Error("Mismatched ReadEndArray");
    if (!ctx.definite) {
      const b = this.r.readUint8();
      if (b !== 0xff) throw new Error("Expected break for indefinite array");
    }
  }

  readEndArrayAndSkip(skipSize: number) {
    for (var i = 0; i < Math.abs(skipSize); i++) 
        this.skipValue();
    this.readEndArray();
  }

  skipValue(): void {
    this.readEncodedValue();
  }

  readEncodedValue() {
    let depth = 0;

    do {
      depth = this.skipNextNode(depth);
    } while (depth > 0);
  }

  skipNextNode(initialDepth: number): number {
    let state: CborReaderState;
    let depth = initialDepth;

    while ((state = this.peekState()) === CborReaderState.Tag)
      this.r.readUint8();

    switch (state) {
      case CborReaderState.UnsignedInteger:
        this.readUInt32();
        break;

      case CborReaderState.NegativeInteger:
        this.readInt32();
        break;

      case CborReaderState.ByteString:
        this.readByteString();
        break;

      case CborReaderState.TextString:
        this.readTextString();
        break;

      case CborReaderState.StartArray:
        this.readStartArray();
        depth++;
        break;

      case CborReaderState.EndArray:
        if (depth === 0) throw new Error(`Skip invalid state: ${state}`);

        this.readEndArray();
        depth--;
        break;

      case CborReaderState.StartMap:
        this.readStartMap();
        depth++;
        break;

      case CborReaderState.EndMap:
        if (depth === 0) throw new Error(`Skip invalid state: ${state}`);

        this.readEndMap();
        depth--;
        break;

      case CborReaderState.FloatingPointNumber:
        this.readDouble();
        break;

      case CborReaderState.Null:
        this.readNull();
        break;
      case CborReaderState.Boolean:
        this.readBoolean();
        break;
      case CborReaderState.Undefined:
        this.readUndefined();
        break;

      default:
        throw new Error(`Skip invalid state: ${state}`);
    }

    return depth;
  }

  // -------------------
  // Maps
  // -------------------
  readStartMap(): number | null {
    const b = this.r.readUint8();
    if (b >> 5 !== 5) throw new Error("Not map");
    const ai = b & 0x1f;
    const len = this.readLength(ai);
    this.stack.push({
      type: "map",
      definite: len !== null,
      remaining: len === null ? null : Number(len),
    });
    return len === null ? null : Number(len);
  }

  readEndMap() {
    const ctx = this.stack.pop();
    if (!ctx || ctx.type !== "map") throw new Error("Mismatched ReadEndMap");
    if (!ctx.definite) {
      const b = this.r.readUint8();
      if (b !== 0xff) throw new Error("Expected break for indefinite map");
    }
  }

  // -------------------
  // Helpers
  // -------------------
  private peekByte(): number {
    return this.r.peekUint8();
  }
}
