import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { CborReader, IonStreamDisconnectedError, IonWsClient } from "../src";
import "../src/index";
import {
  ARGS,
  CLIENT_CLOSE,
  DATA_I4,
  END,
  END_OF_INPUT,
  ERROR,
  INPUT_I4,
  READY,
  cbor,
  channel,
  consume,
  createHarness,
  delay,
  frame,
  isPing,
  recordEvents,
  until,
} from "./support/streamFakes";

/**
 * Resumable sessions against scripted fake transports: what the client acknowledges and when,
 * what it sends first on a resuming connection, what it sends again after RESUMED — and when it
 * gives the session up and starts the call afresh instead.
 */

let events: ReturnType<typeof recordEvents>;

beforeEach(() => {
  IonWsClient.resetTransportHealth();
  events = recordEvents();
});

afterEach(() => events.dispose());

const TOKEN = "resume-token-1";

const RESUMED = (received: number) =>
  frame(
    0x08,
    cbor((w) => {
      w.writeStartArray(1);
      w.writeUInt32(received);
      w.writeEndArray();
    })
  );

const ACK = (n: number) => frame(0x06, cbor((w) => w.writeUInt32(n)));

const isAck = (f: Uint8Array) => f[0] === 0x06;
const ackOf = (f: Uint8Array) => Number(new CborReader(f.subarray(1)).readUInt64());

/** The RESUME a client opened a connection with: `[token, received]`. */
function readResume(f: Uint8Array): { token: string; received: number } {
  expect(f[0]).toBe(0x07);
  const r = new CborReader(f.subarray(1));
  expect(r.readStartArray()).toBe(2);
  const token = r.readTextString();
  const received = Number(r.readUInt64());
  r.readEndArray();
  return { token, received };
}

/** The frames the client sent after its first message, without its pings. */
const sentFrames = (frames: Uint8Array[]) => frames.filter((f) => !isPing(f));

/** The next frame the client sent that is neither a PING nor an ACK. */
async function nextFrame(inbox: { next(what?: string): Promise<Uint8Array> }): Promise<Uint8Array> {
  for (;;) {
    const f = await inbox.next("the next frame");
    if (!isPing(f) && !isAck(f)) return f;
  }
}

describe("acknowledgements", () => {
  it("acknowledges a batch at once, a lone frame on the timer, and a goodbye before it closes", async () => {
    const h = createHarness({ keepAliveIntervalMs: 0 });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    expect(await ws.sent.next()).toEqual(ARGS);

    ws.frame(READY(10_000, 0, 0, TOKEN));
    for (let i = 0; i < 64; i++) ws.frame(DATA_I4(i));
    await until(() => sentFrames(ws.frames).some(isAck), "the batch ACK");
    expect(ackOf(sentFrames(ws.frames).find(isAck)!)).toBe(64);

    ws.frame(DATA_I4(64));
    const started = Date.now();
    await until(() => sentFrames(ws.frames).filter(isAck).length === 2, "the timer ACK", 3000);
    expect(ackOf(sentFrames(ws.frames).filter(isAck)[1])).toBe(65);
    expect(Date.now() - started).toBeLessThan(2000);

    ws.frame(END);
    await run.done;
    expect(run.error).toBeUndefined();
    expect(run.items).toHaveLength(65);

    const acks = sentFrames(ws.frames).filter(isAck);
    expect(ackOf(acks[acks.length - 1])).toBe(66); // the END is acknowledged…
    expect(ws.closeCalls).toEqual([{ code: 1000, reason: "completed" }]); // …before our side closes
  });

  it("sends no ACK on a session that is not resumable", async () => {
    const h = createHarness({ keepAliveIntervalMs: 0 });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY());
    for (let i = 0; i < 100; i++) ws.frame(DATA_I4(i));
    ws.frame(END);
    await run.done;
    expect(sentFrames(ws.frames).filter(isAck)).toEqual([]);
  });

  it("an ACK for frames never sent is a protocol violation, and final", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(10_000, 0, 0, TOKEN), ACK(3));
    await run.done;
    expect(run.error).toBeInstanceOf(IonStreamDisconnectedError);
    expect((run.error as IonStreamDisconnectedError).reason).toBe("ProtocolViolation");
    await delay(50);
    expect(h.sockets.count).toBe(1);
  });
});

describe("resuming", () => {
  it("opens with RESUME, resends only what the server lacks, and loses and repeats nothing", async () => {
    const h = createHarness();
    const input = channel<number>();
    const run = consume(
      h.client().callServerStreamingFullDuplex<number, number>("i4", ARGS, input.iterable, "i4")
    );

    const ws1 = await h.sockets.next();
    ws1.accept();
    expect(await ws1.sent.next()).toEqual(ARGS);
    ws1.frame(READY(10_000, 0, 0, TOKEN), DATA_I4(1), DATA_I4(2));
    input.push(10);
    input.push(11);
    expect(await nextFrame(ws1.sent)).toEqual(INPUT_I4(10));
    expect(await nextFrame(ws1.sent)).toEqual(INPUT_I4(11));
    await until(() => run.items.length === 2, "the first two items");

    ws1.drop();

    const ws2 = await h.sockets.next("the resuming connection");
    ws2.accept();
    const resume = readResume(await ws2.sent.next());
    expect(resume).toEqual({ token: TOKEN, received: 2 });

    // The server had the first input item, not the second.
    ws2.frame(RESUMED(1));
    expect(await nextFrame(ws2.sent)).toEqual(INPUT_I4(11));

    input.push(12);
    expect(await nextFrame(ws2.sent)).toEqual(INPUT_I4(12));
    input.end();
    expect(await nextFrame(ws2.sent)).toEqual(END_OF_INPUT);

    ws2.frame(DATA_I4(3), END);
    await run.done;
    expect(run.error).toBeUndefined();
    expect(run.items).toEqual([1, 2, 3]);
    expect(events.log.filter((e) => e.startsWith("reconnected"))).toEqual(["reconnected(1,resumed)"]);
  });

  it("a RESUME the server cannot honour starts the call afresh, at once", async () => {
    const h = createHarness({ reconnect: { initialDelayMs: 200, maxDelayMs: 200, jitter: false } });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));

    const ws1 = await h.sockets.next();
    ws1.accept();
    ws1.frame(READY(10_000, 0, 0, TOKEN), DATA_I4(1));
    await until(() => run.items.length === 1, "the first item");
    ws1.drop();

    const ws2 = await h.sockets.next("the resuming connection", 3000);
    ws2.accept();
    readResume(await ws2.sent.next());
    ws2.frame(ERROR("STREAM_NOT_RESUMABLE", "gone"));

    const started = Date.now();
    const ws3 = await h.sockets.next("the fresh connection");
    expect(Date.now() - started).toBeLessThan(150); // no backoff for an answer
    ws3.accept();
    expect(await ws3.sent.next()).toEqual(ARGS);
    ws3.frame(READY(), DATA_I4(1), END);

    await run.done;
    expect(run.error).toBeUndefined();
    expect(run.items).toEqual([1, 1]); // started afresh: the method ran again
    expect(events.log.filter((e) => e.startsWith("reconnected"))).toEqual(["reconnected(1)"]);
  });

  it("past the resume window the client does not even try", async () => {
    const h = createHarness({ reconnect: { initialDelayMs: 150, maxDelayMs: 150, jitter: false } });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));

    const ws1 = await h.sockets.next();
    ws1.accept();
    ws1.frame(READY(10_000, 0, 0, TOKEN, 50), DATA_I4(1));
    await until(() => run.items.length === 1, "the first item");
    ws1.drop();

    const ws2 = await h.sockets.next("the next connection", 3000);
    ws2.accept();
    expect(await ws2.sent.next()).toEqual(ARGS);
    ws2.frame(READY(), END);
    await run.done;
    expect(run.error).toBeUndefined();
  });

  it("a RESUMED that claims frames the client never sent is a protocol violation, and final", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));

    const ws1 = await h.sockets.next();
    ws1.accept();
    ws1.frame(READY(10_000, 0, 0, TOKEN));
    ws1.drop();

    const ws2 = await h.sockets.next();
    ws2.accept();
    readResume(await ws2.sent.next());
    ws2.frame(RESUMED(5));

    await run.done;
    expect(run.error).toBeInstanceOf(IonStreamDisconnectedError);
    expect((run.error as IonStreamDisconnectedError).reason).toBe("ProtocolViolation");
  });

  it("a WebTransport session that cannot come back over QUIC resumes over a WebSocket", async () => {
    const h = createHarness({}, { webTransport: true });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));

    const wt1 = await h.transports.next();
    wt1.accept();
    expect(await wt1.sent.next()).toEqual(ARGS);
    wt1.send([READY(10_000, 0, 0, TOKEN), DATA_I4(1)]);
    await until(() => run.items.length === 1, "the first item");
    wt1.reset();

    const wt2 = await h.transports.next("the WebTransport retry");
    wt2.refuse();

    const ws = await h.sockets.next("the WebSocket");
    ws.accept();
    expect(readResume(await ws.sent.next())).toEqual({ token: TOKEN, received: 1 });
    ws.frame(RESUMED(0), DATA_I4(2), END);

    await run.done;
    expect(run.error).toBeUndefined();
    expect(run.items).toEqual([1, 2]);
  });

  it("leaving a resumed session says goodbye like any other", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS), 2);

    const ws1 = await h.sockets.next();
    ws1.accept();
    ws1.frame(READY(10_000, 0, 0, TOKEN), DATA_I4(1));
    await until(() => run.items.length === 1, "the first item");
    ws1.drop();

    const ws2 = await h.sockets.next();
    ws2.accept();
    readResume(await ws2.sent.next());
    ws2.frame(RESUMED(0), DATA_I4(2));

    await run.done;
    expect(run.items).toEqual([1, 2]);
    expect(sentFrames(ws2.frames).filter((f) => !isAck(f))).toEqual([CLIENT_CLOSE]);
    expect(ws2.closeCalls).toEqual([{ code: 1000, reason: "client closed" }]);
  });
});

describe("the replay budget", () => {
  it("is the server's input budget when that is smaller", async () => {
    const h = createHarness();
    const input = channel<number>();
    const run = consume(
      h.client().callServerStreamingFullDuplex<number, number>("i4", ARGS, input.iterable, "i4")
    );

    const ws = await h.sockets.next();
    ws.accept();
    await ws.sent.next(); // ARGS
    ws.frame(READY(10_000, 0, 0, TOKEN, 30_000, 8 * 1024));

    // The server holds 8 KiB for a method that is not reading: the input stops at the frame that
    // reaches it — not at our own 1 MiB, nor at the 4,096-frame cap.
    for (let i = 0; i < 3000; i++) input.push(i);
    await delay(100);
    const data = sentFrames(ws.frames).filter((f) => f[0] === 0x00);
    const held = data.length;
    const bytes = data.reduce((sum, f) => sum + f.length, 0);
    expect(bytes).toBeGreaterThanOrEqual(8 * 1024);
    expect(bytes - data[held - 1].length).toBeLessThan(8 * 1024);

    ws.frame(ACK(held));
    await until(() => sentFrames(ws.frames).filter((f) => f[0] === 0x00).length > held, "more input after the ACK");
    ws.frame(END);
    await run.done;
    expect(run.error).toBeUndefined();
  });

  it("holds the input at the cap on unacknowledged frames, and lets it go on ACK", async () => {
    const h = createHarness({ resumeBufferSize: 0 }); // clamped to the 32 KiB floor
    const input = channel<number>();
    const run = consume(
      h.client().callServerStreamingFullDuplex<number, number>("i4", ARGS, input.iterable, "i4")
    );

    const ws = await h.sockets.next();
    ws.accept();
    await ws.sent.next(); // ARGS
    ws.frame(READY(10_000, 0, 0, TOKEN));

    // Each input frame is 6 bytes, so 4,096 of them — the unacknowledged-frame cap — come before
    // the 32 KiB byte budget does.
    const total = 7000;
    for (let i = 0; i < total; i++) input.push(i);
    await delay(100);
    const inputFrames = () => sentFrames(ws.frames).filter((f) => f[0] === 0x00).length;
    const held = inputFrames();
    expect(held).toBe(4096);

    ws.frame(ACK(held));
    await until(() => inputFrames() === total, "the rest of the input");

    ws.frame(END);
    await run.done;
    expect(run.error).toBeUndefined();
  });
});
