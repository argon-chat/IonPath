import { BinaryWriter } from "../binary/BinaryWriter";

const textEncoder = new TextEncoder();

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
      // A bigint argument used to jump straight to the 8-byte header, so `i8`/`u8` values and
      // any other bigint-carried head wrote e.g. -1 as `3b0000000000000000` where C#'s
      // CborWriter and Rust's minicbor both write `20`. Same class of defect as the float
      // shrinking: the three runtimes disagreed byte-wise on the same logical value, and it only
      // ever surfaced for values small enough to fit a shorter head. The minimal form is chosen
      // here too, which converges this runtime onto the other two rather than the reverse.
      if (value < 24n) {
        this.w.writeUint8((majorType << 5) | Number(value));
      } else if (value <= 0xffn) {
        this.w.writeUint8((majorType << 5) | 24);
        this.w.writeUint8(Number(value));
      } else if (value <= 0xffffn) {
        this.w.writeUint8((majorType << 5) | 25);
        this.w.writeUint16(Number(value));
      } else if (value <= 0xffff_ffffn) {
        this.w.writeUint8((majorType << 5) | 26);
        this.w.writeUint32(Number(value));
      } else {
        this.w.writeUint8((majorType << 5) | 27);
        this.w.writeBigUint64(value);
      }
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

  /**
   * Writes an arbitrary-precision integer in Ion's canonical form: a plain CBOR integer while the
   * value fits the i64/u64 range, and a tag 2 (positive) / tag 3 (negative) bignum with a
   * minimal-length big-endian magnitude beyond that.
   *
   * Distinct from {@link writeInt128}, which falls back to a bignum for anything above
   * `i64::MAX` and so would encode a `u64`-range mantissa differently from C# and Rust. Used by
   * the `decimal` formatter for the tag 4 mantissa.
   */
  writeBigInteger(value: bigint) {
    // The window is the union of the i64 and u64 ranges — what every runtime can express without
    // arbitrary precision. Note the deliberate asymmetry: -2^63 - 1 becomes a bignum even though
    // CBOR could hold it as a plain negative integer, because C#'s BigInteger path draws the line
    // at long.MinValue and all three runtimes must draw it in the same place.
    if (value >= -0x8000_0000_0000_0000n && value <= 0xffff_ffff_ffff_ffffn) {
      if (value >= 0n) this.writeTypeAndLength(0, value);
      else this.writeTypeAndLength(1, -1n - value);
      return;
    }

    const positive = value >= 0n;
    let magnitude = positive ? value : -1n - value;

    this.writeTypeAndLength(6, positive ? 2 : 3);

    const bytes: number[] = [];
    while (magnitude > 0n) {
      bytes.unshift(Number(magnitude & 0xffn));
      magnitude >>= 8n;
    }
    if (bytes.length === 0) bytes.push(0);

    this.writeTypeAndLength(2, bytes.length);
    this.w.writeBytes(new Uint8Array(bytes));
  }

  /**
   * Splices in a already-encoded CBOR data item verbatim.
   *
   * Needed by the `Map<K,V>` and `Set<T>` formatters: canonical ordering is defined over encoded
   * bytes, so each key/element is written into a scratch {@link CborWriter} first and the sorted
   * results are pasted in here. The caller is responsible for `bytes` holding exactly one item.
   */
  writeEncodedValue(bytes: Uint8Array) {
    this.w.writeBytes(bytes);
  }

  writeHalf(value: number) {
    this.w.writeUint8((7 << 5) | 25);
    this.w.writeFloat16(value);
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
    const bytes = textEncoder.encode(str);
    this.writeTypeAndLength(3, bytes.length);
    this.w.writeBytes(bytes);
  }

  writeStartTextString() {
    this.writeTypeAndLength(3, null);
  }

  writeTextStringChunk(str: string) {
    const bytes = textEncoder.encode(str);
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
