import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  CborReader,
  IonDecodeError,
  IonRequestException,
  IonStreamClosedError,
  IonStreamDisconnectedError,
  IonWsClient,
} from "../src";
import "../src/index";
import {
  ARGS,
  CLIENT_CLOSE,
  CLOSE,
  DATA_I4,
  DATA_STR,
  END,
  END_OF_INPUT,
  ERROR,
  INPUT_I4,
  PING,
  READY,
  cbor,
  channel,
  consume,
  createHarness,
  delay,
  isPing,
  recordEvents,
  until,
} from "./support/streamFakes";

/**
 * The streaming client against scripted fake transports. Timeouts are real but tens of
 * milliseconds long: fake timers and async generators interleave badly, and a real clock is what
 * the heartbeat actually runs on.
 */

let events: ReturnType<typeof recordEvents>;

beforeEach(() => {
  IonWsClient.resetTransportHealth();
  events = recordEvents();
});

afterEach(() => {
  events.dispose();
  vi.unstubAllGlobals();
});

const expectDisconnected = (e: unknown, reason: string, code: string) => {
  expect(e).toBeInstanceOf(IonStreamDisconnectedError);
  const error = e as IonStreamDisconnectedError;
  expect(error.reason).toBe(reason);
  expect(error.error.code).toBe(code);
  return error;
};

describe("WebSocket transport", () => {
  it("READY, DATA×3, END: yields every item and returns; nothing reconnects", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS, undefined, "corr-1"));

    const ws = await h.sockets.next();
    expect(ws.url).toBe("wss://ion.test/ion/IStreamService/Numbers.ws?sid=session-1&cid=corr-1&resume=1");
    expect(ws.binaryType).toBe("arraybuffer");

    ws.accept();
    expect(await ws.sent.next()).toEqual(ARGS);
    ws.frame(READY(), DATA_I4(1), DATA_I4(2), PING, DATA_I4(3), END);

    await run.done;
    expect(run.error).toBeUndefined();
    expect(run.items).toEqual([1, 2, 3]);
    expect(ws.closeCalls).toEqual([{ code: 1000, reason: "completed" }]);
    expect(ws.frames).toEqual([]); // no goodbye of our own after the server's, no ping

    await delay(30);
    expect(h.sockets.count).toBe(1);
    expect(events.log).toEqual([]);
  });

  it("offers exactly one sub-protocol: v2, carrying the ticket", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    expect(ws.protocols).toEqual([`ion!ticket#${h.tickets[0]}!ver#2`]);
    ws.accept();
    ws.frame(READY(), END);
    await run.done;
    expect(run.error).toBeUndefined();
  });

  it("items queued before the consumer pulls are all delivered, then END", async () => {
    const h = createHarness();
    const gen = h.client().callServerStreaming<number>("i4", ARGS);
    const first = gen.next();
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), ...Array.from({ length: 100 }, (_, i) => DATA_I4(i)), END);

    const items = [(await first).value];
    for await (const n of gen) items.push(n);
    expect(items).toEqual(Array.from({ length: 100 }, (_, i) => i));
  });

  it("an empty negotiated sub-protocol is accepted; READY decides", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept("");
    ws.frame(READY(), DATA_I4(9), END);
    await run.done;
    expect(run.items).toEqual([9]);
  });

  it("a server that negotiates v1 is refused as a protocol violation, without retrying", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept(`ion!ticket#${h.tickets[0]}!ver#1`);
    await run.done;

    const error = expectDisconnected(run.error, "ProtocolViolation", "PROTOCOL_VIOLATION");
    expect(error.retryable).toBe(false);
    expect(error.message).toContain("v1");
    expect(ws.sent.count).toBe(0); // not even the arguments
    await delay(30);
    expect(h.sockets.count).toBe(1);
  });

  it("DATA before READY — a v1 server that ignored the sub-protocol — is a protocol violation", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept("");
    ws.frame(DATA_I4(1));
    await run.done;
    expectDisconnected(run.error, "ProtocolViolation", "PROTOCOL_VIOLATION");
    expect(run.items).toEqual([]);
    await delay(30);
    expect(h.sockets.count).toBe(1);
  });

  it("a text message is a protocol violation", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY());
    ws.text("hello");
    await run.done;
    expectDisconnected(run.error, "ProtocolViolation", "PROTOCOL_VIOLATION");
  });
});

describe("WebTransport transport", () => {
  const long = "x".repeat(100); // 2-byte varint prefix
  const huge = "y".repeat(20_000); // 4-byte varint prefix

  it.each([
    ["one chunk", undefined],
    ["byte by byte", [1]],
    ["ragged chunks", [3, 1, 7, 2, 64, 5, 1, 300, 2]],
    ["chunks that split every varint", [1, 2, 1, 1, 3]],
  ])("reassembles frames across chunk boundaries: %s", async (_, chunks) => {
    const h = createHarness({}, { webTransport: true });
    const run = consume(h.client().callServerStreaming<string>("string", ARGS, undefined, "corr-1"));

    const wt = await h.transports.next();
    expect(wt.url).toBe(
      `https://ion.test/ion/IStreamService/Numbers.wt?ticket=${h.tickets[0]}&ver=2&sid=session-1&cid=corr-1&resume=1`
    );
    wt.accept();
    expect(await wt.sent.next()).toEqual(ARGS);

    wt.send([READY(), DATA_STR("a"), DATA_STR(long), PING, DATA_STR(huge), DATA_STR("b"), END], chunks);

    await run.done;
    expect(run.error).toBeUndefined();
    expect(run.items).toEqual(["a", long, huge, "b"]);
    await until(() => wt.closeCalls.length === 1, "the session close");
    expect(wt.writerClosed).toBe(true);
    expect(wt.closeCalls).toEqual([{ closeCode: 0, reason: "completed" }]);
    expect(wt.frames).toEqual([]);
    expect(h.sockets.count).toBe(0);
  });

  it("reassembles random frames under random chunking (seeded)", async () => {
    for (let seed = 1; seed <= 8; seed++) {
      // mulberry32: a 32-bit PRNG that stays in integer arithmetic.
      let a = seed * 0x9e3779b9;
      const rnd = (n: number) => {
        a = (a + 0x6d2b79f5) | 0;
        let t = Math.imul(a ^ (a >>> 15), 1 | a);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return Math.floor((((t ^ (t >>> 14)) >>> 0) / 4294967296) * n);
      };

      const expected = Array.from({ length: 40 }, (_, i) =>
        "z".repeat(rnd(10) === 0 ? 17_000 + rnd(1000) : rnd(300)) + i
      );
      const chunks = Array.from({ length: 16 }, () => 1 + rnd(rnd(2) === 0 ? 8 : 5000));

      const h = createHarness({}, { webTransport: true });
      const run = consume(h.client().callServerStreaming<string>("string", ARGS));
      const wt = await h.transports.next();
      wt.accept();
      await wt.sent.next();
      wt.send([READY(), ...expected.flatMap((v) => [DATA_STR(v), PING]), END], chunks);
      await run.done;
      expect(run.error, `seed ${seed}`).toBeUndefined();
      expect(run.items, `seed ${seed}`).toEqual(expected);
    }
  });

  it("the server's FIN without END is a lost transport and reconnects", async () => {
    const h = createHarness({}, { webTransport: true });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const wt1 = await h.transports.next();
    wt1.accept();
    wt1.send([READY(), DATA_I4(1)]);
    await until(() => run.items.length === 1, "the first item");
    wt1.finish();

    // Connected fine before it dropped: WebTransport is not blamed, and is used again.
    const wt2 = await h.transports.next();
    expect(wt2.ticket).not.toBe(wt1.ticket);
    wt2.accept();
    wt2.send([READY(), DATA_I4(2), END]);
    await run.done;
    expect(run.items).toEqual([1, 2]);
    expect(events.log).toEqual(["reconnecting(1,5)", "reconnected(1)"]);
    expect(h.sockets.count).toBe(0);
  });

  it("a stream that ends inside a frame is a protocol violation", async () => {
    const h = createHarness({}, { webTransport: true });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const wt = await h.transports.next();
    wt.accept();
    wt.send([READY()]);
    wt.raw(Uint8Array.of(0x05, 0x00, 0x01)); // announces 5 bytes, delivers 2
    wt.finish();
    await run.done;
    expectDisconnected(run.error, "ProtocolViolation", "PROTOCOL_VIOLATION");
  });

  it("a zero-length frame is a protocol violation", async () => {
    const h = createHarness({}, { webTransport: true });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const wt = await h.transports.next();
    wt.accept();
    wt.send([READY()]);
    wt.raw(Uint8Array.of(0x00));
    await run.done;
    expectDisconnected(run.error, "ProtocolViolation", "PROTOCOL_VIOLATION");
    await until(() => wt.closeCalls.length === 1 && wt.writerAborted, "the abort");
    expect(wt.closeCalls).toEqual([undefined]);
  });

  it("frames the client sends are varint-prefixed, including the arguments", async () => {
    const h = createHarness({ keepAliveIntervalMs: 15 }, { webTransport: true });
    const input = channel<number>();
    const run = consume(
      h.client().callServerStreamingFullDuplex<number, number>("i4", ARGS, input.iterable, "i4")
    );
    const wt = await h.transports.next();
    wt.accept();
    expect(await wt.sent.next()).toEqual(ARGS);
    input.push(5);
    expect(await wt.sent.next()).toEqual(INPUT_I4(5));
    input.end();
    expect(await wt.sent.next()).toEqual(END_OF_INPUT);
    wt.send([READY()]);
    expect(await wt.sent.next()).toEqual(PING);
    wt.send([DATA_I4(1), END]);
    await run.done;
    expect(run.items).toEqual([1]);
  });
});

describe("how a stream ends", () => {
  it("ERROR throws IonRequestException with the server's error, and does not reconnect", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), DATA_I4(1), ERROR("BOOM", "it broke"));
    await run.done;

    expect(run.items).toEqual([1]);
    expect(run.error).toBeInstanceOf(IonRequestException);
    expect(run.error).not.toBeInstanceOf(IonStreamClosedError);
    expect(run.error).not.toBeInstanceOf(IonStreamDisconnectedError);
    expect((run.error as IonRequestException).error).toEqual({ code: "BOOM", message: "it broke" });
    expect(ws.closeCalls).toEqual([{ code: 1000, reason: "error" }]);

    await delay(30);
    expect(h.sockets.count).toBe(1);
    expect(events.log).toEqual([]);
  });

  it("ERROR before READY — a connect hook refused the client — is thrown as is", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(ERROR("FORBIDDEN", "not in this room"));
    await run.done;
    expect((run.error as IonRequestException).error.code).toBe("FORBIDDEN");
    await delay(30);
    expect(h.sockets.count).toBe(1);
  });

  it("CLOSE without allowReconnect throws IonStreamClosedError and does not reconnect", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), DATA_I4(1), CLOSE("kicked", false));
    await run.done;

    expect(run.items).toEqual([1]);
    expect(run.error).toBeInstanceOf(IonStreamClosedError);
    const error = run.error as IonStreamClosedError;
    expect(error.reason).toBe("kicked");
    expect(error.allowReconnect).toBe(false);
    expect(error.error).toEqual({ code: "STREAM_CLOSED", message: "kicked" });
    expect(ws.closeCalls).toEqual([{ code: 1000, reason: "closed" }]);
    await delay(30);
    expect(h.sockets.count).toBe(1);
    expect(events.log).toEqual([]);
  });

  it("CLOSE with an empty payload is a close with no reason and no invitation back", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), Uint8Array.of(0x04));
    await run.done;
    const error = run.error as IonStreamClosedError;
    expect(error).toBeInstanceOf(IonStreamClosedError);
    expect(error.reason).toBeNull();
    expect(error.allowReconnect).toBe(false);
    expect(error.error.message).toBe("The server closed the stream.");
  });

  it("CLOSE with allowReconnect reconnects with a fresh ticket", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws1 = await h.sockets.next();
    ws1.accept();
    ws1.frame(READY(), DATA_I4(1), CLOSE("restarting", true));

    const ws2 = await h.sockets.next();
    expect(h.tickets).toHaveLength(2);
    expect(h.tickets[1]).not.toBe(h.tickets[0]);
    expect(ws2.protocols).toEqual([`ion!ticket#${h.tickets[1]}!ver#2`]);
    ws2.accept();
    expect(await ws2.sent.next()).toEqual(ARGS);
    ws2.frame(READY(), DATA_I4(2), END);

    await run.done;
    expect(run.error).toBeUndefined();
    expect(run.items).toEqual([1, 2]);
    expect(events.log).toEqual(["reconnecting(1,5)", "reconnected(1)"]);
  });

  it("CLOSE with allowReconnect is thrown when reconnecting is off", async () => {
    const h = createHarness({ reconnect: false });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), CLOSE("restarting", true));
    await run.done;
    const error = run.error as IonStreamClosedError;
    expect(error).toBeInstanceOf(IonStreamClosedError);
    expect(error.allowReconnect).toBe(true);
    await delay(30);
    expect(h.sockets.count).toBe(1);
  });

  it("a dropped transport reconnects, with backoff, and emits the events in order", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));

    const ws1 = await h.sockets.next();
    ws1.accept();
    ws1.frame(READY(), DATA_I4(1));
    await until(() => run.items.length === 1, "the first item");
    ws1.drop(1006);

    const ws2 = await h.sockets.next();
    ws2.refuse();

    const ws3 = await h.sockets.next();
    ws3.accept();
    ws3.frame(READY(), DATA_I4(2), END);

    await run.done;
    expect(run.error).toBeUndefined();
    expect(run.items).toEqual([1, 2]);
    expect(events.log).toEqual(["reconnecting(1,5)", "reconnecting(2,10)", "reconnected(2)"]);
    expect(new Set(h.tickets).size).toBe(3);
  });

  it("the attempt counter starts over once a connection is established", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws1 = await h.sockets.next();
    ws1.accept();
    ws1.frame(READY());
    ws1.drop();
    const ws2 = await h.sockets.next();
    ws2.accept();
    ws2.frame(READY());
    ws2.drop();
    const ws3 = await h.sockets.next();
    ws3.accept();
    ws3.frame(READY(), END);
    await run.done;
    expect(events.log).toEqual([
      "reconnecting(1,5)",
      "reconnected(1)",
      "reconnecting(1,5)",
      "reconnected(1)",
    ]);
  });

  it("with reconnecting off a drop throws IonStreamDisconnectedError with the close code", async () => {
    const h = createHarness({ reconnect: false });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY());
    ws.drop(1011, "server blew up");
    await run.done;

    const error = expectDisconnected(run.error, "TransportLost", "STREAM_DISCONNECTED");
    expect(error.retryable).toBe(true);
    expect(error.closeCode).toBe(1011);
    expect(error.closeReason).toBe("server blew up");
    expect(events.log).toEqual([]);
  });

  it("maxAttempts is respected: the last failure is thrown after that many reconnects", async () => {
    const h = createHarness({
      reconnect: { initialDelayMs: 1, maxDelayMs: 4, maxAttempts: 2, jitter: false },
    });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    for (let i = 0; i < 3; i++) (await h.sockets.next()).refuse();
    await run.done;

    expectDisconnected(run.error, "TransportLost", "STREAM_DISCONNECTED");
    expect(events.log).toEqual(["reconnecting(1,1)", "reconnecting(2,2)"]);
    await delay(30);
    expect(h.sockets.count).toBe(3);
  });

  it("backoff is capped by maxDelayMs, and full jitter keeps each delay below its bound", async () => {
    const h = createHarness({
      reconnect: { initialDelayMs: 2, maxDelayMs: 5, maxAttempts: 5, jitter: true },
    });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    for (let i = 0; i < 6; i++) (await h.sockets.next()).refuse();
    await run.done;

    const delays = events.log.map((e) => Number(/,(\d+)\)/.exec(e)![1]));
    expect(delays).toHaveLength(5);
    [2, 4, 5, 5, 5].forEach((bound, i) => {
      expect(delays[i]).toBeGreaterThanOrEqual(0);
      expect(delays[i]).toBeLessThan(bound);
    });
  });

  it("an unknown opcode is a protocol violation and never loops", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), DATA_I4(1), Uint8Array.of(0x7f, 0x00));
    await run.done;

    expect(run.items).toEqual([1]);
    const error = expectDisconnected(run.error, "ProtocolViolation", "PROTOCOL_VIOLATION");
    expect(error.retryable).toBe(false);
    expect(error.message).toContain("0x7f");
    expect(ws.closeCalls).toEqual([{}]); // aborted, not closed politely
    await delay(30);
    expect(h.sockets.count).toBe(1);
    expect(events.log).toEqual([]);
  });

  it("an item that cannot be decoded throws the decode error, says goodbye and does not reconnect", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), DATA_I4(1), DATA_STR("not an i4"), DATA_I4(3));
    await run.done;

    expect(run.items).toEqual([1]);
    expect(run.error).toBeInstanceOf(IonDecodeError);
    expect(ws.frames).toEqual([CLIENT_CLOSE]);
    expect(ws.closeCalls).toEqual([{ code: 1000, reason: "client error" }]);
    await delay(30);
    expect(h.sockets.count).toBe(1);
  });

  it("READY with elements a later version added is accepted", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(10_000, 0, 3, "token-1"), DATA_I4(4), END);
    await run.done;
    expect(run.error).toBeUndefined();
    expect(run.items).toEqual([4]);
  });

  it("READY with too few elements is a protocol violation", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(
      Uint8Array.of(
        0x05,
        ...cbor((w) => {
          w.writeStartArray(2);
          w.writeTextString("conn");
          w.writeUInt32(1000);
          w.writeEndArray();
        })
      )
    );
    await run.done;
    expectDisconnected(run.error, "ProtocolViolation", "PROTOCOL_VIOLATION");
  });

  it("a second READY is a protocol violation", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), READY());
    await run.done;
    expectDisconnected(run.error, "ProtocolViolation", "PROTOCOL_VIOLATION");
  });

  it("a listener that throws does not change how the stream goes", async () => {
    const h = createHarness();
    const throwing = () => {
      throw new Error("listener bug");
    };
    const log = vi.spyOn(console, "error").mockImplementation(() => {});
    IonWsClient.on("reconnected", throwing);
    try {
      const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
      const ws1 = await h.sockets.next();
      ws1.accept();
      ws1.frame(READY());
      ws1.drop();
      const ws2 = await h.sockets.next();
      ws2.accept();
      ws2.frame(READY(), DATA_I4(1), END);
      await run.done;
      expect(run.error).toBeUndefined();
      expect(run.items).toEqual([1]);
      expect(log).toHaveBeenCalled();
    } finally {
      IonWsClient.off("reconnected", throwing);
      log.mockRestore();
    }
  });
});

describe("heartbeat and timeouts", () => {
  it("a silent server times out and the client reconnects", async () => {
    const h = createHarness({ serverTimeoutMs: 40 });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws1 = await h.sockets.next();
    ws1.accept();
    const started = performance.now();
    ws1.frame(READY(10)); // the server's keep-alive is 10 ms, so 40 ms stands

    const ws2 = await h.sockets.next();
    expect(performance.now() - started).toBeGreaterThanOrEqual(38);
    expect(ws1.closeCalls).toEqual([{}]); // a dead transport is aborted
    ws2.accept();
    ws2.frame(READY(10), DATA_I4(5), END);

    await run.done;
    expect(run.error).toBeUndefined();
    expect(run.items).toEqual([5]);
    expect(events.log).toEqual(["reconnecting(1,5)", "reconnected(1)"]);
  });

  it("a silent server with reconnecting off throws a Timeout", async () => {
    const h = createHarness({ serverTimeoutMs: 30, reconnect: false });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(10));
    await run.done;
    const error = expectDisconnected(run.error, "Timeout", "STREAM_TIMEOUT");
    expect(error.retryable).toBe(true);
  });

  it("server pings keep a quiet stream alive", async () => {
    const h = createHarness({ serverTimeoutMs: 40 });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(10));
    for (let i = 0; i < 15; i++) {
      await delay(10);
      ws.frame(PING);
    }
    ws.frame(DATA_I4(1), END);
    await run.done;
    expect(run.error).toBeUndefined();
    expect(run.items).toEqual([1]);
    expect(h.sockets.count).toBe(1);
  });

  it("the server timeout is raised to twice the server's keep-alive", async () => {
    const h = createHarness({ serverTimeoutMs: 20, reconnect: false });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(60)); // 2 × 60 = 120 ms, not 20
    await delay(60);
    expect(run.settled).toBe(false);
    await run.done;
    expectDisconnected(run.error, "Timeout", "STREAM_TIMEOUT");
  });

  it("a server that announces no keep-alive is never timed out", async () => {
    const h = createHarness({ serverTimeoutMs: 20 });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(0));
    await delay(100);
    expect(run.settled).toBe(false);
    ws.frame(DATA_I4(1), END);
    await run.done;
    expect(run.items).toEqual([1]);
    expect(h.sockets.count).toBe(1);
  });

  it("the client pings at its keep-alive interval while it has nothing else to send", async () => {
    const h = createHarness({ keepAliveIntervalMs: 20 });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY());
    await delay(115);
    const pings = ws.frames.filter(isPing).length;
    expect(pings).toBeGreaterThanOrEqual(3);
    expect(pings).toBeLessThanOrEqual(6);
    expect(ws.frames.every(isPing)).toBe(true);
    ws.frame(END);
    await run.done;
  });

  it("the keep-alive is lowered to half the server's client timeout", async () => {
    const h = createHarness({ keepAliveIntervalMs: 10_000 });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(10_000, 40)); // pings every 20 ms
    await delay(115);
    expect(ws.frames.filter(isPing).length).toBeGreaterThanOrEqual(3);
    ws.frame(END);
    await run.done;
  });

  it("no pings before READY, and none while the input stream keeps the connection busy", async () => {
    const h = createHarness({ keepAliveIntervalMs: 60 });
    async function* ticking() {
      for (let i = 0; i < 25; i++) {
        await delay(5);
        yield i;
      }
    }
    const run = consume(
      h.client().callServerStreamingFullDuplex<number, number>("i4", ARGS, ticking(), "i4")
    );
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY());
    await until(() => ws.frames.some((f) => f.length === 1 && f[0] === 0x01), "the end of input");
    ws.frame(END);
    await run.done;

    expect(ws.frames.filter(isPing)).toEqual([]);
    expect(ws.frames).toHaveLength(26);
  });

  it("READY must arrive within the handshake timeout", async () => {
    const h = createHarness({ handshakeTimeoutMs: 30, reconnect: false });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    await run.done;
    const error = expectDisconnected(run.error, "Timeout", "STREAM_TIMEOUT");
    expect(error.message).toContain("READY");
  });

  it("a WebSocket that never opens times out and is retried", async () => {
    const h = createHarness({ handshakeTimeoutMs: 30 });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws1 = await h.sockets.next();
    const ws2 = await h.sockets.next();
    expect(ws1.closeCalls).toEqual([{}]);
    ws2.accept();
    ws2.frame(READY(), END);
    await run.done;
    expect(run.error).toBeUndefined();
    expect(events.log).toEqual(["reconnecting(1,5)", "reconnected(1)"]);
  });
});

describe("the consumer leaving", () => {
  it("break sends CLOSE, closes the socket, emits closed and never reconnects", async () => {
    const h = createHarness();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS), 2);
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), DATA_I4(1), DATA_I4(2), DATA_I4(3));
    await run.done;

    expect(run.error).toBeUndefined();
    expect(run.items).toEqual([1, 2]);
    expect(ws.frames).toEqual([CLIENT_CLOSE]);
    expect(ws.closeCalls).toEqual([{ code: 1000, reason: "client closed" }]);
    await delay(30);
    expect(h.sockets.count).toBe(1);
    expect(events.log).toEqual(["closed"]);
  });

  it("break over WebTransport sends CLOSE, then FIN, then closes the session", async () => {
    const h = createHarness({}, { webTransport: true });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS), 1);
    const wt = await h.transports.next();
    wt.accept();
    wt.send([READY(), DATA_I4(1), DATA_I4(2)]);
    await run.done;

    expect(await wt.sent.next()).toEqual(ARGS);
    expect(await wt.sent.next()).toEqual(CLIENT_CLOSE);
    await until(() => wt.closeCalls.length === 1, "the session close");
    expect(wt.writerClosed).toBe(true);
    expect(wt.closeCalls).toEqual([{ closeCode: 0, reason: "client closed" }]);
    expect(events.log).toEqual(["closed"]);
  });

  it("abort mid-stream throws AbortError, sends CLOSE, emits closed once, no reconnect", async () => {
    const h = createHarness();
    const ac = new AbortController();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS, ac.signal));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), DATA_I4(1));
    await until(() => run.items.length === 1, "the first item");

    ac.abort();
    // The goodbye goes out at once, not on the consumer's next pull.
    expect(ws.frames).toEqual([CLIENT_CLOSE]);
    await run.done;

    expect(run.error).toBeInstanceOf(DOMException);
    expect((run.error as DOMException).name).toBe("AbortError");
    expect(ws.closeCalls).toEqual([{ code: 1000, reason: "client closed" }]);
    await delay(30);
    expect(h.sockets.count).toBe(1);
    expect(events.log).toEqual(["closed"]);
  });

  it("abort while connecting kills the socket without a goodbye", async () => {
    const h = createHarness();
    const ac = new AbortController();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS, ac.signal));
    const ws = await h.sockets.next();
    ac.abort();
    await run.done;
    expect((run.error as DOMException).name).toBe("AbortError");
    expect(ws.closeCalls).toEqual([{}]);
    expect(ws.sent.count).toBe(0);
    expect(events.log).toEqual(["closed"]);
  });

  it("abort during the reconnect backoff throws at once", async () => {
    const h = createHarness({ reconnect: { initialDelayMs: 10_000, jitter: false } });
    const ac = new AbortController();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS, ac.signal));
    (await h.sockets.next()).refuse();
    await until(() => events.log.length === 1, "the reconnecting event");

    const started = performance.now();
    ac.abort();
    await run.done;
    expect(performance.now() - started).toBeLessThan(500);
    expect((run.error as DOMException).name).toBe("AbortError");
    expect(events.log).toEqual(["reconnecting(1,10000)", "closed"]);
    expect(h.sockets.count).toBe(1);
  });

  it("an already-aborted signal throws before anything is opened", async () => {
    const h = createHarness();
    const ac = new AbortController();
    ac.abort();
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS, ac.signal));
    await run.done;
    expect((run.error as DOMException).name).toBe("AbortError");
    expect(h.tickets).toHaveLength(0);
    expect(h.sockets.count).toBe(0);
  });
});

describe("input streams (full duplex)", () => {
  it("sends each item as [0x00] array(1)[item] right after the arguments, then END", async () => {
    const h = createHarness();
    async function* input() {
      yield 10;
      yield 20;
    }
    const run = consume(
      h.client().callServerStreamingFullDuplex<number, number>("i4", ARGS, input(), "i4")
    );
    const ws = await h.sockets.next();
    ws.accept();

    // Before READY: a server's connect hooks may be waiting on the input.
    expect(await ws.sent.next()).toEqual(ARGS);
    expect(await ws.sent.next()).toEqual(INPUT_I4(10));
    expect(await ws.sent.next()).toEqual(INPUT_I4(20));
    expect(await ws.sent.next()).toEqual(END_OF_INPUT);

    ws.frame(READY(), DATA_I4(30), END);
    await run.done;
    expect(run.error).toBeUndefined();
    expect(run.items).toEqual([30]);
  });

  it("an input that throws sends INPUT_FAULTED, leaves, and surfaces the original error", async () => {
    const h = createHarness();
    const boom = new Error("input exploded");
    async function* input() {
      yield 1;
      throw boom;
    }
    const run = consume(
      h.client().callServerStreamingFullDuplex<number, number>("i4", ARGS, input(), "i4")
    );
    const ws = await h.sockets.next();
    ws.accept();
    await run.done;

    expect(run.error).toBe(boom);
    expect(ws.frames).toHaveLength(3);
    expect(ws.frames[0]).toEqual(INPUT_I4(1));
    expect(ws.frames[1][0]).toBe(0x02);
    const reader = new CborReader(ws.frames[1].subarray(1));
    reader.readStartArray();
    expect(reader.readTextString()).toBe("INPUT_FAULTED");
    expect(reader.readTextString()).toBe("input exploded");
    reader.readEndArray();
    expect(ws.frames[2]).toEqual(CLIENT_CLOSE);
    expect(ws.closeCalls).toEqual([{ code: 1000, reason: "client error" }]);

    await delay(30);
    expect(h.sockets.count).toBe(1);
    expect(events.log).toEqual([]);
  });

  it("an input that fails mid-stream interrupts a consumer that is waiting for items", async () => {
    const h = createHarness();
    const input = channel<number>();
    const run = consume(
      h.client().callServerStreamingFullDuplex<number, number>("i4", ARGS, input.iterable, "i4")
    );
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), DATA_I4(1));
    await until(() => run.items.length === 1, "the first item");

    const boom = new RangeError("the source went away");
    input.fail(boom);
    await run.done;
    expect(run.error).toBe(boom);
    expect(ws.frames.map((f) => f[0])).toEqual([0x02, 0x04]);
    expect(input.state.returned).toBe(false); // it failed; there is nothing to return
  });

  it("an input item the formatter cannot encode faults the input like a throwing iterable", async () => {
    const h = createHarness();
    const input = channel<unknown>();
    const run = consume(
      h.client().callServerStreamingFullDuplex<number, unknown>("i4", ARGS, input.iterable, "no-such-type")
    );
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY());
    input.push(1);
    await run.done;
    expect((run.error as Error).message).toContain("Formatter not found: no-such-type");
    expect(ws.frames.map((f) => f[0])).toEqual([0x02, 0x04]);
  });

  it("the same iterator continues across a reconnect; items already sent are not replayed", async () => {
    const h = createHarness();
    const input = channel<number>();
    const run = consume(
      h.client().callServerStreamingFullDuplex<number, number>("i4", ARGS, input.iterable, "i4")
    );

    const ws1 = await h.sockets.next();
    ws1.accept();
    ws1.frame(READY());
    input.push(1);
    expect(await ws1.sent.next()).toEqual(ARGS);
    expect(await ws1.sent.next()).toEqual(INPUT_I4(1));
    ws1.drop();

    // Pulled while no connection is open: held, and sent on the next one.
    input.push(2);
    const ws2 = await h.sockets.next();
    ws2.accept();
    expect(await ws2.sent.next()).toEqual(ARGS);
    expect(await ws2.sent.next()).toEqual(INPUT_I4(2));
    input.push(3);
    expect(await ws2.sent.next()).toEqual(INPUT_I4(3));
    input.end();
    expect(await ws2.sent.next()).toEqual(END_OF_INPUT);
    ws2.frame(READY(), END);

    await run.done;
    expect(run.error).toBeUndefined();
    expect(ws2.frames).toEqual([INPUT_I4(2), INPUT_I4(3), END_OF_INPUT]);
    expect(input.state.returned).toBe(false);
  });

  it("an input that completed before a drop is ended again on the new connection", async () => {
    const h = createHarness();
    async function* input() {
      yield 1;
    }
    const run = consume(
      h.client().callServerStreamingFullDuplex<number, number>("i4", ARGS, input(), "i4")
    );
    const ws1 = await h.sockets.next();
    ws1.accept();
    await until(() => ws1.frames.length === 2, "the input and its end");
    ws1.frame(READY());
    ws1.drop();

    const ws2 = await h.sockets.next();
    ws2.accept();
    expect(await ws2.sent.next()).toEqual(ARGS);
    expect(await ws2.sent.next()).toEqual(END_OF_INPUT);
    ws2.frame(READY(), DATA_I4(7), END);
    await run.done;
    expect(run.items).toEqual([7]);
    expect(ws2.frames).toEqual([END_OF_INPUT]);
  });

  it("the input is returned when the stream ends before the input does", async () => {
    const h = createHarness();
    const input = channel<number>();
    const run = consume(
      h.client().callServerStreamingFullDuplex<number, number>("i4", ARGS, input.iterable, "i4")
    );
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), DATA_I4(1), END);
    await run.done;
    expect(run.items).toEqual([1]);
    expect(input.state.returned).toBe(true);
  });
});

describe("transport selection and fallback", () => {
  const acceptWs = async (h: ReturnType<typeof createHarness>, ...frames: Uint8Array[]) => {
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), ...frames, END);
    return ws;
  };

  it("WebTransport is skipped silently when there is no implementation", async () => {
    const h = createHarness(); // no WebTransport factory, and Node has no global one
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    await acceptWs(h, DATA_I4(1));
    await run.done;
    expect(run.items).toEqual([1]);
    expect(h.tickets).toHaveLength(1);
  });

  it("WebTransport is skipped for a base URL that is not https:", async () => {
    const h = createHarness({}, { webTransport: true, baseUrl: "http://ion.test:8080/" });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await acceptWs(h);
    await run.done;
    expect(ws.url).toBe("ws://ion.test:8080/ion/IStreamService/Numbers.ws?sid=session-1&resume=1");
    expect(h.transports.count).toBe(0);
  });

  it("a refused WebTransport falls back to WebSocket at once, and is remembered", async () => {
    const h = createHarness({}, { webTransport: true });

    const run1 = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const wt = await h.transports.next();
    expect(wt.ticket).toBe(h.tickets[0]);
    wt.refuse();
    const ws1 = await h.sockets.next();
    expect(ws1.protocols).toEqual([`ion!ticket#${h.tickets[1]}!ver#2`]); // a fresh ticket
    ws1.accept();
    ws1.frame(READY(), DATA_I4(1), END);
    await run1.done;
    expect(run1.items).toEqual([1]);
    expect(events.log).toEqual([]); // a fallback is not a reconnect

    const run2 = consume(h.client().callServerStreaming<number>("i4", ARGS));
    await acceptWs(h, DATA_I4(2));
    await run2.done;
    expect(run2.items).toEqual([2]);
    expect(h.transports.count).toBe(1); // went straight to WebSocket

    IonWsClient.resetTransportHealth();
    const run3 = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const wt3 = await h.transports.next();
    wt3.accept();
    wt3.send([READY(), DATA_I4(3), END]);
    await run3.done;
    expect(run3.items).toEqual([3]);
  });

  it("the failure is remembered for webTransportRetryAfterMs only", async () => {
    const h = createHarness({ webTransportRetryAfterMs: 40 }, { webTransport: true });
    const run1 = consume(h.client().callServerStreaming<number>("i4", ARGS));
    (await h.transports.next()).refuse();
    await acceptWs(h);
    await run1.done;

    await delay(50);
    const run2 = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const wt2 = await h.transports.next();
    wt2.accept();
    wt2.send([READY(), END]);
    await run2.done;
    expect(run2.error).toBeUndefined();
  });

  it("a WebTransport stream that cannot be created falls back", async () => {
    const h = createHarness({}, { webTransport: true });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const wt = await h.transports.next();
    wt.failStream = new Error("no streams for you");
    wt.accept();
    await acceptWs(h, DATA_I4(1));
    await run.done;
    expect(run.items).toEqual([1]);
    await until(() => wt.closeCalls.length === 1, "the WebTransport session to be closed");
  });

  it("a WebTransport that hangs falls back after webTransportConnectTimeoutMs", async () => {
    const h = createHarness({ webTransportConnectTimeoutMs: 30 }, { webTransport: true });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    await h.transports.next(); // never accepted
    const started = performance.now();
    await acceptWs(h, DATA_I4(1));
    expect(performance.now() - started).toBeGreaterThanOrEqual(20);
    await run.done;
    expect(run.items).toEqual([1]);
  });

  it("a WebTransport session that opens but never sends READY falls back", async () => {
    const h = createHarness({ handshakeTimeoutMs: 30 }, { webTransport: true });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const wt = await h.transports.next();
    wt.accept();
    await acceptWs(h, DATA_I4(1));
    await run.done;
    expect(run.items).toEqual([1]);
    expect(events.log).toEqual([]);

    // …and is remembered.
    const run2 = consume(h.client().callServerStreaming<number>("i4", ARGS));
    await acceptWs(h);
    await run2.done;
    expect(h.transports.count).toBe(1);
  });

  it("a WebTransport-only client keeps using WebTransport after it failed", async () => {
    const h = createHarness({ transports: ["webtransport"] }, { webTransport: true });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    (await h.transports.next()).refuse();
    const wt2 = await h.transports.next();
    wt2.accept();
    wt2.send([READY(), DATA_I4(1), END]);
    await run.done;
    expect(run.items).toEqual([1]);
    expect(events.log).toEqual(["reconnecting(1,5)", "reconnected(1)"]);
    expect(h.sockets.count).toBe(0);
  });

  it("the configured order is respected: WebSocket first, then WebTransport", async () => {
    const h = createHarness({ transports: ["websocket", "webtransport"] }, { webTransport: true });
    const run1 = consume(h.client().callServerStreaming<number>("i4", ARGS));
    await acceptWs(h, DATA_I4(1));
    await run1.done;
    expect(h.transports.count).toBe(0);

    const run2 = consume(h.client().callServerStreaming<number>("i4", ARGS));
    (await h.sockets.next()).refuse();
    const wt = await h.transports.next();
    wt.accept();
    wt.send([READY(), DATA_I4(2), END]);
    await run2.done;
    expect(run2.items).toEqual([2]);
    expect(events.log).toEqual([]);
  });

  it("transports: ['websocket'] never touches WebTransport", async () => {
    const h = createHarness({ transports: ["websocket"] }, { webTransport: true });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    await acceptWs(h);
    await run.done;
    expect(h.transports.count).toBe(0);
  });

  it("no available transport at all is an immediate, explicit error", async () => {
    const h = createHarness({ transports: ["webtransport"] }); // and no WebTransport here
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    await run.done;
    expect((run.error as Error).message).toContain("No Ion stream transport is available");
    expect(h.tickets).toHaveLength(0);
  });

  it("a WebTransport constructor that throws falls back to WebSocket", async () => {
    const h = createHarness({
      webTransportFactory: () => {
        throw new TypeError("WebTransport is disabled by policy");
      },
    });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    await acceptWs(h, DATA_I4(1));
    await run.done;
    expect(run.items).toEqual([1]);
    expect(events.log).toEqual([]);
  });

  it("a transport that cannot even be constructed, with nothing to fall back to, is thrown as is", async () => {
    const bad = new SyntaxError("The URL is not valid");
    const h = createHarness({
      webSocketFactory: () => {
        throw bad;
      },
    });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    await run.done;
    expect(run.error).toBe(bad);
    await delay(30);
    expect(h.tickets).toHaveLength(1); // no reconnect loop on a local error
    expect(events.log).toEqual([]);
  });

  it("a connection that drops after READY is reconnected, not fallen back from", async () => {
    const h = createHarness({}, { webTransport: true });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const wt1 = await h.transports.next();
    wt1.accept();
    wt1.send([READY()]);
    await delay(5);
    wt1.reset();
    const wt2 = await h.transports.next();
    wt2.accept();
    wt2.send([READY(), END]);
    await run.done;
    expect(run.error).toBeUndefined();
    expect(h.sockets.count).toBe(0);
    expect(events.log).toEqual(["reconnecting(1,5)", "reconnected(1)"]);
  });
});

describe("ticket exchange", () => {
  const ticketResponse = () =>
    new Response(
      cbor((w) => {
        w.writeStartArray(1);
        w.writeByteString(Uint8Array.of(1, 2, 3));
        w.writeEndArray();
      }),
      { status: 200 }
    );
  const errorResponse = (status: number, code: string) =>
    new Response(
      cbor((w) => {
        w.writeStartArray(2);
        w.writeTextString(code);
        w.writeTextString("refused");
        w.writeEndArray();
      }),
      { status }
    );

  it("a 4xx exchange throws with its status: no fallback, no reconnect", async () => {
    const fetch = vi.fn(async () => errorResponse(401, "TICKET_REFUSED"));
    vi.stubGlobal("fetch", fetch);
    const h = createHarness({}, { webTransport: true, interceptors: [] });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    await run.done;

    expect(run.error).toBeInstanceOf(IonRequestException);
    const error = run.error as IonRequestException;
    expect(error.status).toBe(401);
    expect(error.error.code).toBe("TICKET_REFUSED");
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch.mock.calls[0]).toBeDefined();
    expect(h.transports.count).toBe(0);
    expect(h.sockets.count).toBe(0);
    expect(events.log).toEqual([]);
  });

  it("a 503 exchange is retried with backoff", async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(errorResponse(503, "UNAVAILABLE"))
      .mockImplementation(async () => ticketResponse());
    vi.stubGlobal("fetch", fetch);
    const h = createHarness({}, { interceptors: [] });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    expect(ws.protocols).toEqual([`ion!ticket#${IonWsClient.prototype.toBase56(Uint8Array.of(1, 2, 3))}!ver#2`]);
    ws.accept();
    ws.frame(READY(), END);
    await run.done;
    expect(run.error).toBeUndefined();
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(events.log).toEqual(["reconnecting(1,5)", "reconnected(1)"]);
  });

  it("an exchange that fails on the network is retried", async () => {
    const fetch = vi
      .fn()
      .mockRejectedValueOnce(new TypeError("fetch failed"))
      .mockImplementation(async () => ticketResponse());
    vi.stubGlobal("fetch", fetch);
    const h = createHarness({}, { interceptors: [] });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    const ws = await h.sockets.next();
    ws.accept();
    ws.frame(READY(), END);
    await run.done;
    expect(run.error).toBeUndefined();
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("an exchange failure is thrown when reconnecting is off", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => errorResponse(503, "UNAVAILABLE")));
    const h = createHarness({ reconnect: false }, { interceptors: [] });
    const run = consume(h.client().callServerStreaming<number>("i4", ARGS));
    await run.done;
    expect((run.error as IonRequestException).status).toBe(503);
  });
});
