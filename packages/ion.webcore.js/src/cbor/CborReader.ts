import { BinaryReader } from "../binary/BinaryReader";

const textDecoder = new TextDecoder("utf-8");

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

  readTag(): number | bigint {
    const reader = this.r;
    const initial = reader.readUint8();
    const majorType = initial >> 5;
    const additional = initial & 0x1f;

    if (majorType !== 6) {
      throw new Error(`Unexpected major type ${majorType}, expected 6 (tag)`);
    }

    if (additional < 24) {
      return additional;
    } else if (additional === 24) {
      return reader.readUint8();
    } else if (additional === 25) {
      return reader.readUint16(false);
    } else if (additional === 26) {
      return reader.readUint32(false);
    } else if (additional === 27) {
      return reader.readBigUint64(false);
    } else {
      throw new Error(`Invalid additional info for tag: ${additional}`);
    }
  }

  /**
   * Reads the tag at the cursor **without consuming it**, so a formatter can validate the tag
   * before deciding how to proceed. Throws if the next item is not a tag.
   */
  peekTag(): number | bigint {
    const saved = this.r.position;
    try {
      return this.readTag();
    } finally {
      this.r.seek(saved);
    }
  }

  /**
   * Reads an arbitrary-precision integer: a plain CBOR integer (major type 0/1) or a tag 2/3
   * bignum. The counterpart of {@link CborWriter.writeBigInteger}; used for the `decimal`
   * mantissa.
   */
  readBigInteger(): bigint {
    return this.readInt128();
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

  readLength(ai: number): number | bigint | null {
    if (ai < 24) {
      return ai;
    }
    switch (ai) {
      case 24:
        return this.r.readUint8();
      case 25:
        return this.r.readUint16();
      case 26:
        return this.r.readUint32();
      case 27:
        return this.r.readBigUint64();
      case 31:
        return null;
      default:
        throw new Error(`Invalid additional info for length: ${ai}`);
    }
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

  readEndTextString(): void {
    const b = this.r.readUint8();
    if (b !== 0xff) throw new Error("Expected break for text string");
  }

  readTextString(): string {
    const initialByte = this.r.peekUint8();
    const majorType = initialByte >> 5;
    const ai = initialByte & 0x1f;
    if (majorType !== 3) throw new Error("Not a text string");

    if (ai === 31) {
      this.r.readUint8();
      let chunks: Uint8Array[] = [];
      for (;;) {
        const b = this.r.peekUint8();
        if (b === 0xff) {
          this.r.readUint8();
          break;
        }
        chunks.push(this.readTextStringChunkRaw());
      }
      const total = new Uint8Array(
        chunks.reduce((sum, c) => sum + c.length, 0)
      );
      let offset = 0;
      for (const c of chunks) {
        total.set(c, offset);
        offset += c.length;
      }
      return textDecoder.decode(total);
    } else {
      this.r.readUint8();
      const len = this.readLength(ai);
      if (len == null) throw new Error("Invalid definite length");
      const bytes = this.r.readBytes(len);
      return textDecoder.decode(bytes);
    }
  }

  private readTextStringChunkRaw(): Uint8Array {
    const initialByte = this.r.readUint8();
    const majorType = initialByte >> 5;
    const ai = initialByte & 0x1f;
    if (majorType !== 3) throw new Error("Not a text string chunk");

    const len = this.readLength(ai);
    if (len == null)
      throw new Error("Chunks of indefinite text string must be definite");

    return this.r.readBytes(len);
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
    for (let i = 0; i < Math.abs(skipSize); i++) this.skipValue();
    this.readEndArray();
  }

  /**
   * Skips exactly one data item, including all of its children.
   *
   * The previous implementation drove itself off `peekState()`, which only
   * reports `EndArray`/`EndMap` for *indefinite-length* containers — this reader
   * does not track the remaining item count of a definite-length one. Skipping a
   * definite-length array or map therefore ran past the end of the container and
   * consumed whatever followed it. Nested containers are now walked by their
   * declared length instead.
   */
  skipValue(): void {
    // consume any tags in front of the value
    while ((this.r.peekUint8() >> 5) === 6) this.readTag();

    const initial = this.r.peekUint8();
    const mt = initial >> 5;
    const ai = initial & 0x1f;

    switch (mt) {
      case 0: // unsigned
      case 1: // negative
        this.r.readUint8();
        this.readLength(ai);
        return;

      case 2:
        this.readByteString();
        return;

      case 3:
        this.readTextString();
        return;

      case 4: {
        const len = this.readStartArray();
        if (len === null) {
          while (this.r.peekUint8() !== 0xff) this.skipValue();
        } else {
          for (let i = 0; i < len; i++) this.skipValue();
        }
        this.readEndArray();
        return;
      }

      case 5: {
        const len = this.readStartMap();
        if (len === null) {
          while (this.r.peekUint8() !== 0xff) {
            this.skipValue();
            this.skipValue();
          }
        } else {
          for (let i = 0; i < len; i++) {
            this.skipValue();
            this.skipValue();
          }
        }
        this.readEndMap();
        return;
      }

      case 7:
        if (ai === 25) {
          this.r.readUint8();
          this.r.readFloat16();
          return;
        }
        if (ai === 26) {
          this.r.readUint8();
          this.r.readFloat32();
          return;
        }
        if (ai === 27) {
          this.r.readUint8();
          this.r.readFloat64();
          return;
        }
        if (ai === 31) throw new Error("Skip: unexpected break");
        this.r.readUint8();
        if (ai === 24) this.r.readUint8(); // one-byte simple value
        return;

      default:
        throw new Error(`Skip: invalid major type ${mt}`);
    }
  }

  /** Alias of {@link skipValue}. */
  readEncodedValue() {
    this.skipValue();
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
