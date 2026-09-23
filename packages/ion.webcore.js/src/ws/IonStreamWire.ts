/**
 * The wire contract of an Ion stream, client side. The authority is `IonStreamProtocol` in
 * `ion.runtime` (C#); the opcodes and payloads here must match it byte for byte.
 *
 * The first client message is the CBOR argument array with no opcode — or, for a client coming
 * back to a session it lost the transport of, a RESUME frame. From then on every message in either
 * direction is one frame, `[opcode][payload]`. Over a WebSocket one message is one frame; over
 * WebTransport each frame is preceded by its length as a QUIC varint.
 */

/** Server → client: one CBOR item. Client → server: `array(1)[item]` of the input stream. */
export const OP_DATA = 0x00;
/** Server → client: the stream completed. Client → server: end of the input stream. */
export const OP_END = 0x01;
/** `[code, message]`. Server → client it ends the stream; client → server it faults the input. */
export const OP_ERROR = 0x02;
/** Heartbeat, either direction, no payload. */
export const OP_PING = 0x03;
/** Deliberate close. Server → client `[reason?, allowReconnect]`; client → server `[reason?]`. */
export const OP_CLOSE = 0x04;
/**
 * Server → client, once, first: `[connectionId, keepAliveMs, clientTimeoutMs]`, followed by
 * `resumeToken, resumeWindowMs` when the session is resumable.
 */
export const OP_READY = 0x05;
/** Resumable sessions, either direction: `uint n` — "I have your reliable frames 1..n". */
export const OP_ACK = 0x06;
/** Client → server, the first message of a resuming transport: `[resumeToken, n]`. */
export const OP_RESUME = 0x07;
/** Server → client, the first frame on a resumed transport: `[m]`. */
export const OP_RESUMED = 0x08;

/** The query parameter by which a client asks for a resumable session. */
export const RESUME_QUERY_PARAMETER = "resume";
/** The ERROR code of a RESUME the server cannot honour: the session is gone. */
export const NOT_RESUMABLE_CODE = "STREAM_NOT_RESUMABLE";
/** A receiver acknowledges at least every this many bytes of reliable frames… */
export const ACK_EVERY_BYTES = 16 * 1024;
/** …or this many reliable frames, whichever comes first. */
export const ACK_EVERY_FRAMES = 64;

/** Whether a frame is numbered and acknowledged on a resumable session: DATA, END, ERROR, CLOSE. */
export function isReliable(opcode: number): boolean {
  return opcode === OP_DATA || opcode === OP_END || opcode === OP_ERROR || opcode === OP_CLOSE;
}

/** The only protocol version this client speaks. */
export const ION_STREAM_VERSION = 2;

/**
 * Bytes a WebTransport frame reserves in front of itself for its length prefix: the widest QUIC
 * varint. Outgoing frames are encoded after this gap and the prefix is written into its tail, so
 * a frame costs one allocation however it is framed.
 */
export const VARINT_HEADROOM = 8;

// ─── QUIC variable-length integers (RFC 9000 §16) ───────────────────────────
// The top two bits of the first byte give the size — 1, 2, 4 or 8 bytes — and the remaining
// bits are the value, big-endian. Mirrors `ion.runtime.IonVarint`.

const TWO_POW_32 = 2 ** 32;

/** The size of the shortest varint that holds `value` (a non-negative safe integer). */
export function varintSize(value: number): 1 | 2 | 4 | 8 {
  if (value < 0x40) return 1;
  if (value < 0x4000) return 2;
  if (value < 0x4000_0000) return 4;
  return 8;
}

/** Writes `value` as the shortest varint at `target[offset]` and returns its size. */
export function writeVarint(target: Uint8Array, offset: number, value: number): number {
  const size = varintSize(value);
  switch (size) {
    case 1:
      target[offset] = value;
      break;
    case 2:
      target[offset] = 0x40 | (value >>> 8);
      target[offset + 1] = value & 0xff;
      break;
    case 4:
      target[offset] = 0x80 | (value >>> 24);
      target[offset + 1] = (value >>> 16) & 0xff;
      target[offset + 2] = (value >>> 8) & 0xff;
      target[offset + 3] = value & 0xff;
      break;
    default: {
      const hi = Math.floor(value / TWO_POW_32);
      const lo = value >>> 0;
      target[offset] = 0xc0 | (hi >>> 24);
      target[offset + 1] = (hi >>> 16) & 0xff;
      target[offset + 2] = (hi >>> 8) & 0xff;
      target[offset + 3] = hi & 0xff;
      target[offset + 4] = lo >>> 24;
      target[offset + 5] = (lo >>> 16) & 0xff;
      target[offset + 6] = (lo >>> 8) & 0xff;
      target[offset + 7] = lo & 0xff;
    }
  }
  return size;
}

/** `frame` with its varint length prefix, in one new array. */
export function withVarintPrefix(frame: Uint8Array): Uint8Array<ArrayBuffer> {
  const size = varintSize(frame.length);
  const out = new Uint8Array(size + frame.length);
  writeVarint(out, 0, frame.length);
  out.set(frame, size);
  return out;
}

/** Receives what {@link FrameAssembler} cuts out of the byte stream. */
export interface FrameSink {
  /**
   * One complete frame, `bytes[start]` being its opcode. The bytes are lent: they may be
   * overwritten once this returns. Returning `false` stops delivery — the transport is going away.
   */
  onFrame(bytes: Uint8Array, start: number, end: number): boolean;
  /** The byte stream is not a sequence of well-formed frames. */
  onViolation(message: string): void;
}

/** A retained buffer larger than this is dropped once it empties rather than kept for reuse. */
const RETAIN_LIMIT = 1 << 20;

/**
 * Cuts varint-length-prefixed frames out of a byte stream that arrives in chunks of any size.
 *
 * A chunk that starts on a frame boundary — the usual case — is parsed in place and nothing is
 * copied. Only the tail of a frame that straddles chunks is copied, into one buffer that is
 * compacted and reused rather than reallocated per chunk, and grows by doubling when a frame
 * outgrows it.
 */
export class FrameAssembler {
  private buf: Uint8Array = new Uint8Array(0);
  private start = 0;
  private end = 0;

  /** Bytes of an incomplete frame waiting for the rest of it. */
  get pending(): number {
    return this.end - this.start;
  }

  /** Feeds one chunk. Returns `false` once the sink stopped delivery or the stream was malformed. */
  push(chunk: Uint8Array, sink: FrameSink): boolean {
    if (this.start === this.end) {
      const consumed = this.drain(chunk, 0, chunk.length, sink);
      if (consumed < 0) return false;
      if (consumed < chunk.length) this.append(chunk, consumed);
      return true;
    }

    this.append(chunk, 0);
    const consumed = this.drain(this.buf, this.start, this.end, sink);
    if (consumed < 0) return false;

    if (consumed === this.end) {
      this.start = this.end = 0;
      if (this.buf.length > RETAIN_LIMIT) this.buf = new Uint8Array(0);
    } else {
      this.start = consumed;
    }
    return true;
  }

  /** Delivers every complete frame in `bytes[pos, limit)`; returns where the first incomplete one starts, or -1. */
  private drain(bytes: Uint8Array, pos: number, limit: number, sink: FrameSink): number {
    while (pos < limit) {
      const first = bytes[pos];
      const size = 1 << (first >> 6);
      if (limit - pos < size) break;

      let length = first & 0x3f;
      for (let i = 1; i < size; i++) length = length * 256 + bytes[pos + i];

      if (length === 0) {
        sink.onViolation("The server sent an empty frame");
        return -1;
      }
      if (length > Number.MAX_SAFE_INTEGER) {
        sink.onViolation(`The server announced a frame of ${length} bytes`);
        return -1;
      }

      const frameStart = pos + size;
      const frameEnd = frameStart + length;
      if (frameEnd > limit) break;

      pos = frameEnd;
      if (!sink.onFrame(bytes, frameStart, frameEnd)) return -1;
    }
    return pos;
  }

  /** Copies `chunk[from..]` behind the buffered bytes, compacting or growing the buffer as needed. */
  private append(chunk: Uint8Array, from: number): void {
    const incoming = chunk.length - from;
    if (this.end + incoming > this.buf.length) {
      const live = this.end - this.start;
      if (live + incoming <= this.buf.length) {
        this.buf.copyWithin(0, this.start, this.end);
      } else {
        const grown = new Uint8Array(Math.max(this.buf.length * 2, live + incoming, 4096));
        grown.set(this.buf.subarray(this.start, this.end));
        this.buf = grown;
      }
      this.start = 0;
      this.end = live;
    }
    this.buf.set(from === 0 ? chunk : chunk.subarray(from), this.end);
    this.end += incoming;
  }
}
