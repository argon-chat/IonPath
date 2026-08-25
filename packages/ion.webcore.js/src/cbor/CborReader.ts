import { BinaryReader } from "../binary/BinaryReader";
import {
  ION_MAX_DEPTH,
  IonContainerNotConsumedError,
  IonContainerOverreadError,
  IonDepthLimitError,
  IonFieldCountError,
  IonIntegerRangeError,
  IonLengthOverclaimError,
  IonMalformedValueError,
  IonTruncatedPayloadError,
  IonUnexpectedCborTypeError,
} from "../errors";

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

const INT32_MIN = -2147483648;
const INT32_MAX = 2147483647;
const UINT32_MAX = 4294967295;

interface Frame {
  type: "array" | "map";
  definite: boolean;
  /** Declared item count — twice the entry count for a map. `null` when indefinite. */
  declared: number | null;
  /** Items still to be read. `null` when indefinite. */
  remaining: number | null;
}

/**
 * A CBOR reader that knows how many items each open container still owes it.
 *
 * ## Why the item accounting exists
 *
 * A CBOR definite-length array is `[header(n), item, item, …]` with **no end marker**. Nothing in
 * the byte stream says where it stops — only the declared `n` does. This reader used to record `n`
 * and never look at it again, which had two consequences, both silent:
 *
 * - A reader whose schema expects more fields than the payload declares read the extra fields from
 *   *beyond* the array — the next element of the enclosing `T[]`, or the next frame on the wire —
 *   and handed the caller a value assembled from two different messages.
 * - `readEndArrayAndSkip(declared - fieldsRead)` computed a **negative** skip, took `Math.abs()`
 *   of it, and skipped *forward*: five bytes consumed for a three-byte message.
 *
 * Every data item now decrements the enclosing frame, reading past zero is
 * {@link IonContainerOverreadError}, and closing a frame with items outstanding is
 * {@link IonContainerNotConsumedError} — which is what catches a `union` envelope carrying a third
 * item that would otherwise be read as the next field of the enclosing message.
 *
 * A tag and the item it wraps are **one** data item, so `readTag()` does not consume a slot of its
 * own; the value behind it does.
 *
 * ## Depth
 *
 * Opening a container past {@link ION_MAX_DEPTH} is {@link IonDepthLimitError}. Nesting is
 * attacker-controlled recursion — `skipValue` walks a field the schema never looks at — and the
 * limit is the same 128 in all three runtimes.
 */
export class CborReader {
  private r: BinaryReader;
  private stack: Frame[] = [];
  private finished = false;

  /**
   * Set by `readTag()`: the *next* value completes the tagged data item and must not be counted
   * against the enclosing container a second time.
   */
  private pendingTag = false;

  constructor(buffer: ArrayBuffer | Uint8Array) {
    this.r = new BinaryReader(buffer);
  }

  get position() {
    return this.r.position;
  }

  get hasData() {
    return !this.finished && this.r.position < this.r.length;
  }

  /** Number of currently open containers. */
  get currentDepth() {
    return this.stack.length;
  }

  // ─── item accounting ──────────────────────────────────────────────────────
  //
  // Called immediately before the initial byte of a data item is consumed. `isTag` marks the item
  // as a tag, whose payload is part of the same data item.

  private beginItem(isTag: boolean): void {
    if (this.pendingTag) {
      // Still inside a tagged item: a tag of a tag stays pending, anything else completes it.
      this.pendingTag = isTag;
      return;
    }

    const frame = this.stack[this.stack.length - 1];
    if (frame && frame.definite) {
      if ((frame.remaining ?? 0) <= 0)
        throw new IonContainerOverreadError(frame.type, frame.declared ?? 0);
      frame.remaining = (frame.remaining as number) - 1;
    }

    this.pendingTag = isTag;
  }

  private pushFrame(type: "array" | "map", items: number | null): void {
    if (this.stack.length >= ION_MAX_DEPTH)
      throw new IonDepthLimitError(ION_MAX_DEPTH, this.stack.length + 1);

    this.stack.push({
      type,
      definite: items !== null,
      declared: items,
      remaining: items,
    });
  }

  // ─── tags ─────────────────────────────────────────────────────────────────

  readTag(): number | bigint {
    this.beginItem(true);
    return this.readTagRaw();
  }

  private readTagRaw(): number | bigint {
    const reader = this.r;
    const initial = reader.readUint8();
    const majorType = initial >> 5;
    const additional = initial & 0x1f;

    if (majorType !== 6)
      throw new IonUnexpectedCborTypeError(
        "tag",
        "a tagged item (major type 6)",
        `major type ${majorType}`
      );

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
      throw new IonMalformedValueError("tag", `invalid additional info ${additional}`);
    }
  }

  /**
   * Reads the tag at the cursor **without consuming it**, so a formatter can validate the tag
   * before deciding how to proceed. Throws if the next item is not a tag.
   */
  peekTag(): number | bigint {
    const saved = this.r.position;
    try {
      return this.readTagRaw();
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
    // A definite-length container reports its own end once its declared items are used up, the
    // same way System.Formats.Cbor does. Formatters branch on this to tell "the array is over"
    // from "the payload stopped".
    const frame = this.stack[this.stack.length - 1];
    if (frame && frame.definite && (frame.remaining ?? 0) <= 0 && !this.pendingTag)
      return frame.type === "array"
        ? CborReaderState.EndArray
        : CborReaderState.EndMap;

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
        throw new IonMalformedValueError(
          "length",
          `invalid additional info ${ai} in a length header`
        );
    }
  }

  // -------------------
  // Integers
  // -------------------

  /**
   * Reads a CBOR integer as a `number`, rejecting anything outside the signed 32-bit range.
   *
   * The range check is the point: CBOR integers carry no width, so an `i4` field can perfectly
   * well receive 2^40 from a peer whose schema widened it to `i8`. This runtime used to return
   * `Number(2n ** 40n)` and let it through — the only one of the three that range-checked nothing
   * at all.
   */
  readInt32(): number {
    const value = this.readIntegerRaw();
    if (value < BigInt(INT32_MIN) || value > BigInt(INT32_MAX))
      throw new IonIntegerRangeError("i4", value, `[${INT32_MIN}, ${INT32_MAX}]`);
    return Number(value);
  }

  readInt64(): bigint {
    const value = this.readIntegerRaw();
    const min = -(2n ** 63n);
    const max = 2n ** 63n - 1n;
    if (value < min || value > max)
      throw new IonIntegerRangeError("i8", value, `[${min}, ${max}]`);
    return value;
  }

  readInt128(): bigint {
    this.beginItem(false);
    const b = this.r.peekUint8();
    const mt = b >> 5;

    if (mt === 6) {
      const tag = this.readTagRaw();
      if (tag !== 2 && tag !== 3)
        throw new IonUnexpectedCborTypeError("i16", "a bignum tag (2 or 3)", `tag ${tag}`);
      const bytes = this.readByteStringRaw();
      let val = 0n;
      for (const byte of bytes) {
        val = (val << 8n) | BigInt(byte);
      }
      return tag === 2 ? val : -1n - val;
    }

    return this.readIntegerBody();
  }

  readUInt32(): number {
    const value = this.readIntegerRaw();
    if (value < 0n || value > BigInt(UINT32_MAX))
      throw new IonIntegerRangeError("u4", value, `[0, ${UINT32_MAX}]`);
    return Number(value);
  }

  readUInt64(): bigint {
    const value = this.readIntegerRaw();
    const max = 2n ** 64n - 1n;
    if (value < 0n || value > max)
      throw new IonIntegerRangeError("u8", value, `[0, ${max}]`);
    return value;
  }

  readUInt128(): bigint {
    this.beginItem(false);
    const b = this.r.peekUint8();
    const mt = b >> 5;

    if (mt === 6) {
      const tag = this.readTagRaw();
      if (tag !== 2)
        throw new IonUnexpectedCborTypeError("u16", "an unsigned bignum tag (2)", `tag ${tag}`);
      const bytes = this.readByteStringRaw();
      let val = 0n;
      for (const byte of bytes) {
        val = (val << 8n) | BigInt(byte);
      }
      return val;
    }

    const value = this.readIntegerBody();
    if (value < 0n)
      throw new IonIntegerRangeError("u16", value, "[0, 2^128 - 1]");
    return value;
  }

  /**
   * Reads one CBOR integer item, exactly, as a `bigint`.
   *
   * A `bigint` rather than a `number` because the range check has to happen against the *declared*
   * Ion width, and `Number` has already lost the information by the time it is 2^53 or more.
   */
  private readIntegerRaw(): bigint {
    this.beginItem(false);
    return this.readIntegerBody();
  }

  private readIntegerBody(): bigint {
    const b = this.r.readUint8();
    const mt = b >> 5;
    const ai = b & 0x1f;

    if (mt !== 0 && mt !== 1)
      throw new IonUnexpectedCborTypeError(
        "integer",
        "an integer (major type 0 or 1)",
        `major type ${mt}`
      );

    const len = this.readLength(ai);
    if (len === null)
      throw new IonMalformedValueError("integer", "indefinite length is not valid for an integer");

    const magnitude = typeof len === "bigint" ? len : BigInt(len);
    return mt === 0 ? magnitude : -1n - magnitude;
  }

  // -------------------
  // Floats
  // -------------------
  readDouble(): number {
    this.beginItem(false);
    const b = this.r.readUint8();
    const mt = b >> 5;
    const ai = b & 0x1f;
    if (mt !== 7)
      throw new IonUnexpectedCborTypeError(
        "float",
        "a floating-point number (major type 7)",
        `major type ${mt}`
      );
    if (ai === 25) return this.r.readFloat16();
    if (ai === 26) return this.r.readFloat32();
    if (ai === 27) return this.r.readFloat64();
    throw new IonUnexpectedCborTypeError(
      "float",
      "a half, single or double",
      `simple value ${ai}`
    );
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
    this.beginItem(false);
    const b = this.r.readUint8();
    if (b === 0xf4) return false;
    if (b === 0xf5) return true;
    throw new IonUnexpectedCborTypeError("bool", "true or false", `initial byte 0x${b.toString(16)}`);
  }

  readNull(): null {
    this.beginItem(false);
    const b = this.r.readUint8();
    if (b !== 0xf6)
      throw new IonUnexpectedCborTypeError("null", "null", `initial byte 0x${b.toString(16)}`);
    return null;
  }

  readUndefined(): undefined {
    this.beginItem(false);
    const b = this.r.readUint8();
    if (b !== 0xf7)
      throw new IonUnexpectedCborTypeError(
        "undefined",
        "undefined",
        `initial byte 0x${b.toString(16)}`
      );
    return undefined;
  }

  // -------------------
  // Byte strings
  // -------------------
  readByteString(): Uint8Array {
    this.beginItem(false);
    return this.readByteStringRaw();
  }

  /** The body of a byte string, with no item accounting — for chunks and bignum payloads. */
  private readByteStringRaw(): Uint8Array {
    const b = this.r.readUint8();
    const ai = b & 0x1f;
    if (b >> 5 !== 2)
      throw new IonUnexpectedCborTypeError(
        "bytes",
        "a byte string (major type 2)",
        `major type ${b >> 5}`
      );
    const len = this.readLength(ai);
    if (len === null) {
      const chunks: Uint8Array[] = [];
      while (this.peekByte() !== 0xff) {
        chunks.push(this.readByteStringRaw());
      }
      this.r.readUint8(); // break
      const total = new Uint8Array(chunks.reduce((sum, c) => sum + c.length, 0));
      let offset = 0;
      for (const c of chunks) {
        total.set(c, offset);
        offset += c.length;
      }
      return total;
    }
    this.ensureLengthIsPlausible("bytes", len);
    return this.r.readBytes(len as number);
  }

  // -------------------
  // Text strings
  // -------------------
  readStartTextString(): void {
    const b = this.r.readUint8();
    if (b >> 5 !== 3 || (b & 0x1f) !== 31)
      throw new IonUnexpectedCborTypeError(
        "string",
        "an indefinite-length text string",
        `initial byte 0x${b.toString(16)}`
      );
  }

  readEndTextString(): void {
    const b = this.r.readUint8();
    if (b !== 0xff)
      throw new IonMalformedValueError("string", "expected a break after an indefinite text string");
  }

  /**
   * Reads a text string, definite or **chunked** (indefinite) length.
   *
   * Chunked text is accepted deliberately: maps, sets and fixed-size arrays already accept an
   * indefinite length in all three runtimes, and `partial.golden.json` requires it for the
   * `Partial` map, so refusing it for strings alone — as Rust did — is the odd answer out. The
   * chunk boundaries carry no meaning and are erased by the decode; writers always emit a single
   * definite-length chunk, so encoding is unchanged.
   */
  readTextString(): string {
    this.beginItem(false);

    const initialByte = this.r.peekUint8();
    const majorType = initialByte >> 5;
    const ai = initialByte & 0x1f;
    if (majorType !== 3)
      throw new IonUnexpectedCborTypeError(
        "string",
        "a text string (major type 3)",
        `major type ${majorType}`
      );

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
      if (len == null)
        throw new IonMalformedValueError("string", "invalid definite length");
      this.ensureLengthIsPlausible("string", len);
      const bytes = this.r.readBytes(len);
      return textDecoder.decode(bytes);
    }
  }

  private readTextStringChunkRaw(): Uint8Array {
    const initialByte = this.r.readUint8();
    const majorType = initialByte >> 5;
    const ai = initialByte & 0x1f;
    if (majorType !== 3)
      throw new IonUnexpectedCborTypeError(
        "string",
        "a text string chunk (major type 3)",
        `major type ${majorType}`
      );

    const len = this.readLength(ai);
    if (len == null)
      throw new IonMalformedValueError(
        "string",
        "the chunks of an indefinite-length text string must themselves be definite"
      );

    this.ensureLengthIsPlausible("string", len);
    return this.r.readBytes(len);
  }

  // -------------------
  // Arrays
  // -------------------
  readStartArray(): number | null {
    this.beginItem(false);

    const b = this.r.readUint8();
    if (b >> 5 !== 4)
      throw new IonUnexpectedCborTypeError(
        "array",
        "an array (major type 4)",
        `major type ${b >> 5}`
      );
    const ai = b & 0x1f;
    const len = this.readLength(ai);
    if (len !== null) this.ensureLengthIsPlausible("array", len);
    const count = len === null ? null : Number(len);
    this.pushFrame("array", count);
    return count;
  }

  /**
   * Closes an array.
   *
   * A definite-length array with items still outstanding is
   * {@link IonContainerNotConsumedError}. That is what stops a `union` envelope of three items
   * from leaving the third one in the stream for the next field of the enclosing message to read
   * as its own value — the reader used to consume `[index, payload]` and simply stop.
   *
   * A message closes its array with {@link readEndArrayAndSkip}, which drops the tail first, so
   * forward compatibility is unaffected.
   */
  readEndArray() {
    const ctx = this.stack.pop();
    if (!ctx || ctx.type !== "array")
      throw new IonMalformedValueError("array", "readEndArray without a matching readStartArray");
    if (!ctx.definite) {
      const b = this.r.readUint8();
      if (b !== 0xff)
        throw new IonMalformedValueError("array", "expected a break for an indefinite array");
      return;
    }
    if ((ctx.remaining ?? 0) > 0)
      throw new IonContainerNotConsumedError("array", ctx.remaining as number);
  }

  /**
   * Skips a message's trailing items and closes its array.
   *
   * `skipSize` is `declaredLength - fieldsRead`. A **negative** value means the payload is shorter
   * than the schema, which is a decode failure — not a distance. It used to be passed through
   * `Math.abs()`, which turned "one field missing" into "skip one more item", i.e. a read past the
   * end of the array and into whatever followed it.
   */
  readEndArrayAndSkip(skipSize: number) {
    if (skipSize < 0) throw new IonFieldCountError("<message>", -skipSize, 0);
    for (let i = 0; i < skipSize; i++) this.skipValue();
    this.readEndArray();
  }

  /**
   * Skips exactly one data item, including all of its children.
   *
   * Nested containers are walked by their declared length. The recursion is bounded by
   * {@link ION_MAX_DEPTH} through {@link pushFrame}: a skipped field is the cheapest way for a
   * peer to reach deep nesting, because it needs no knowledge of the schema at all, and 100 KB of
   * `0x81` bytes used to be 100 000 levels of JavaScript recursion and a `RangeError`.
   */
  skipValue(): void {
    // consume any tags in front of the value; a tag and its content are one data item
    while (this.hasData && (this.r.peekUint8() >> 5) === 6) this.readTag();

    const initial = this.r.peekUint8();
    const mt = initial >> 5;
    const ai = initial & 0x1f;

    switch (mt) {
      case 0: // unsigned
      case 1: // negative
        this.readIntegerRaw();
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
        this.beginItem(false);
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
        if (ai === 31)
          throw new IonMalformedValueError("skip", "unexpected break");
        this.r.readUint8();
        if (ai === 24) this.r.readUint8(); // one-byte simple value
        return;

      default:
        throw new IonMalformedValueError("skip", `invalid major type ${mt}`);
    }
  }

  /** Alias of {@link skipValue}. */
  readEncodedValue() {
    this.skipValue();
  }

  // -------------------
  // Maps
  // -------------------

  /**
   * Opens a map and returns its **entry** count (`null` when indefinite).
   *
   * The frame tracks *items*, of which each entry is two — a key and a value — so the accounting
   * treats a map exactly like an array of alternating keys and values.
   */
  readStartMap(): number | null {
    this.beginItem(false);

    const b = this.r.readUint8();
    if (b >> 5 !== 5)
      throw new IonUnexpectedCborTypeError(
        "map",
        "a map (major type 5)",
        `major type ${b >> 5}`
      );
    const ai = b & 0x1f;
    const len = this.readLength(ai);
    if (len !== null) this.ensureLengthIsPlausible("map", len, 2);
    const entries = len === null ? null : Number(len);
    this.pushFrame("map", entries === null ? null : entries * 2);
    return entries;
  }

  readEndMap() {
    const ctx = this.stack.pop();
    if (!ctx || ctx.type !== "map")
      throw new IonMalformedValueError("map", "readEndMap without a matching readStartMap");
    if (!ctx.definite) {
      const b = this.r.readUint8();
      if (b !== 0xff)
        throw new IonMalformedValueError("map", "expected a break for an indefinite map");
      return;
    }
    if ((ctx.remaining ?? 0) > 0)
      throw new IonContainerNotConsumedError("map", ctx.remaining as number);
  }

  // -------------------
  // Helpers
  // -------------------

  /**
   * Rejects a declared length the remaining input could not possibly satisfy.
   *
   * Every CBOR data item is at least one byte, and a map entry is two items, so `bytesLeft` is a
   * hard upper bound on the declared count. Checking it here means a nine-byte payload claiming
   * 2^32 elements fails as a decode error rather than as an allocation.
   */
  private ensureLengthIsPlausible(
    context: string,
    declared: number | bigint,
    bytesPerItem = 1
  ): void {
    const available = this.r.remaining;
    const asBig = typeof declared === "bigint" ? declared : BigInt(declared);
    if (asBig * BigInt(bytesPerItem) > BigInt(available))
      throw new IonLengthOverclaimError(context, declared, available);
  }

  private peekByte(): number {
    if (!this.hasData)
      throw new IonTruncatedPayloadError("the next CBOR initial byte");
    return this.r.peekUint8();
  }
}
