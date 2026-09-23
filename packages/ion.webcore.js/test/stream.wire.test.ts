import { describe, expect, it } from "vitest";
import { CborReader, CborWriter, IonContainerOverreadError } from "../src";
import "../src/index";
import {
  FrameAssembler,
  type FrameSink,
  varintSize,
  withVarintPrefix,
  writeVarint,
} from "../src/ws/IonStreamWire";

/** Collects what the assembler delivers; copies, because the bytes are only lent. */
class Collector implements FrameSink {
  frames: number[][] = [];
  violations: string[] = [];
  stopAfter = Infinity;

  onFrame(bytes: Uint8Array, start: number, end: number): boolean {
    this.frames.push(Array.from(bytes.subarray(start, end)));
    return this.frames.length < this.stopAfter;
  }

  onViolation(message: string): void {
    this.violations.push(message);
  }
}

const frameOf = (length: number, fill: number) => new Uint8Array(length).fill(fill);

describe("QUIC varints (RFC 9000 §16)", () => {
  it.each([
    [0, [0x00]],
    [37, [0x25]],
    [63, [0x3f]],
    [64, [0x40, 0x40]],
    [15293, [0x7b, 0xbd]], // RFC 9000 §A.1
    [16383, [0x7f, 0xff]],
    [16384, [0x80, 0x00, 0x40, 0x00]],
    [494878333, [0x9d, 0x7f, 0x3e, 0x7d]], // RFC 9000 §A.1
    [2 ** 30 - 1, [0xbf, 0xff, 0xff, 0xff]],
    [2 ** 30, [0xc0, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00]],
    [2 ** 40 + 5, [0xc0, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x05]],
  ])("%d encodes as the shortest form", (value, bytes) => {
    const out = new Uint8Array(8);
    const size = writeVarint(out, 0, value);
    expect(size).toBe(varintSize(value));
    expect(Array.from(out.subarray(0, size))).toEqual(bytes);
  });

  it("withVarintPrefix prepends the length", () => {
    expect(Array.from(withVarintPrefix(Uint8Array.of(0x03)))).toEqual([0x01, 0x03]);
    const big = withVarintPrefix(frameOf(300, 7));
    expect(Array.from(big.subarray(0, 2))).toEqual([0x41, 0x2c]);
    expect(big.length).toBe(302);
  });
});

describe("FrameAssembler", () => {
  const sizes = [1, 2, 63, 64, 65, 300, 16383, 16384, 70_000];
  const frames = sizes.map((n, i) => frameOf(n, i + 1));
  const stream = (() => {
    const parts = frames.map(withVarintPrefix);
    const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
    let at = 0;
    for (const p of parts) {
      out.set(p, at);
      at += p.length;
    }
    return out;
  })();

  const feed = (chunkSize: number) => {
    const assembler = new FrameAssembler();
    const sink = new Collector();
    for (let at = 0; at < stream.length; at += chunkSize)
      expect(assembler.push(stream.subarray(at, at + chunkSize), sink)).toBe(true);
    return { assembler, sink };
  };

  it.each([1, 2, 3, 7, 64, 1000, 65_536, stream.length])(
    "delivers every frame intact when fed %d byte(s) at a time",
    (chunkSize) => {
      const { assembler, sink } = feed(chunkSize);
      expect(sink.violations).toEqual([]);
      expect(sink.frames.map((f) => f.length)).toEqual(sizes);
      sink.frames.forEach((f, i) => expect(f.every((b) => b === i + 1)).toBe(true));
      expect(assembler.pending).toBe(0);
    }
  );

  it("reports the bytes of an unfinished frame as pending", () => {
    const assembler = new FrameAssembler();
    const sink = new Collector();
    assembler.push(Uint8Array.of(0x05, 1, 2), sink);
    expect(assembler.pending).toBe(3);
    assembler.push(Uint8Array.of(3, 4, 5, 0x01), sink);
    expect(sink.frames).toEqual([[1, 2, 3, 4, 5]]);
    expect(assembler.pending).toBe(1); // the next frame's prefix, waiting for its byte
  });

  it("a zero-length frame is a violation", () => {
    const sink = new Collector();
    expect(new FrameAssembler().push(Uint8Array.of(0x01, 0x03, 0x00), sink)).toBe(false);
    expect(sink.frames).toEqual([[0x03]]);
    expect(sink.violations).toHaveLength(1);
  });

  it("stops delivering as soon as the sink says so", () => {
    const sink = new Collector();
    sink.stopAfter = 2;
    const bytes = Uint8Array.of(0x01, 0x0a, 0x01, 0x0b, 0x01, 0x0c);
    expect(new FrameAssembler().push(bytes, sink)).toBe(false);
    expect(sink.frames).toEqual([[0x0a], [0x0b]]);
  });

  it("parses a chunk that starts on a frame boundary in place, without copying it", () => {
    const chunk = Uint8Array.of(0x02, 0x00, 0x2a, 0x01, 0x03);
    let lent: Uint8Array | null = null;
    new FrameAssembler().push(chunk, {
      onFrame(bytes) {
        lent ??= bytes;
        return true;
      },
      onViolation() {},
    });
    expect(lent).toBe(chunk);
  });
});

describe("CborReader.reset", () => {
  const encode = (write: (w: CborWriter) => void) => {
    const w = new CborWriter();
    write(w);
    return w.data.slice();
  };

  it("decodes a window of a larger buffer, then another payload, with one reader", () => {
    const text = encode((w) => w.writeTextString("hello"));
    const padded = new Uint8Array(text.length + 4);
    padded.set(text, 2);

    const reader = new CborReader(new Uint8Array(0));
    reader.reset(padded, 2, 2 + text.length);
    expect(reader.readTextString()).toBe("hello");
    expect(reader.hasData).toBe(false);

    reader.reset(encode((w) => w.writeInt32(-7)));
    expect(reader.readInt32()).toBe(-7);
  });

  it("forgets the containers a failed read left open", () => {
    const reader = new CborReader(new Uint8Array(0));
    reader.reset(encode((w) => {
      w.writeStartArray(1);
      w.writeInt32(1);
      w.writeEndArray();
    }));
    reader.readStartArray();
    reader.readInt32();
    expect(() => reader.readInt32()).toThrow(IonContainerOverreadError);

    reader.reset(encode((w) => w.writeInt32(42)));
    expect(reader.currentDepth).toBe(0);
    expect(reader.readInt32()).toBe(42);
  });

  it("reads the window only, not the bytes around it", () => {
    const bytes = encode((w) => {
      w.writeInt32(1);
      w.writeInt32(2);
    });
    const reader = new CborReader(new Uint8Array(0));
    reader.reset(bytes, 0, 1);
    expect(reader.readInt32()).toBe(1);
    expect(reader.hasData).toBe(false);
  });
});
