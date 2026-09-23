import { afterAll, beforeAll, describe, it } from "vitest";
import {
  type IonCallContext,
  type IonInterceptor,
  IonRequestException,
  IonStreamClosedError,
  IonStreamDisconnectedError,
  IonWsClient,
} from "@argon-chat/ion.webcore";
import {
  createClient,
  type ILabSignal,
  Joined,
  type LabEvent,
  Left,
  Said,
} from "../../../../src/tests/Contracts/gen-ts/contracts";

/**
 * The stream client against the real .NET server, over a real WebSocket.
 *
 * Driven by `ForeignClientInteropTests` (src/tests/IonTestClientServer/Streaming/Interop), whose
 * doc comment is the contract: it starts the server, runs this file with `ION_INTEROP_URL` and
 * `ION_INTEROP_RUN`, plays the server's half of the scenarios that need one (the kick, the pushes,
 * the long silence) while it runs, and afterwards asserts what the server saw — how each
 * connection ended, and exactly one connection per scenario user. Without `ION_INTEROP_URL` the
 * whole file is skipped.
 *
 * Each scenario is its own client, as its own user: the user name travels in the `x-lab-user`
 * header of the ticket exchange, set by an interceptor, and becomes the ticket — so the server
 * tells the scenarios apart by it, and every connection attempt costs exactly one exchange.
 */

const URL_ = process.env.ION_INTEROP_URL;
const RUN = process.env.ION_INTEROP_RUN ?? "js-local";

const SCENARIO_TIMEOUT = 20_000;

/**
 * Sets the scenario's user on the ticket exchange, and counts the exchanges.
 *
 * Every connection attempt starts with an exchange, and no scenario may retry or reconnect — so a
 * second exchange is refused here, which fails the scenario at once with a message that says so
 * rather than letting it spin in the reconnect loop until its timeout.
 */
class LabUser implements IonInterceptor {
  exchanges = 0;

  constructor(readonly user: string) {}

  async invokeAsync(
    ctx: IonCallContext,
    next: (ctx: IonCallContext, signal?: AbortSignal) => Promise<void>,
    signal?: AbortSignal
  ): Promise<void> {
    if (++this.exchanges > 1)
      throw new Error(`${this.user}: ticket exchange #${this.exchanges}: the client is retrying or reconnecting`);
    (ctx.requestHeaders as Record<string, string>)["x-lab-user"] = this.user;
    await next(ctx, signal);
  }
}

/** A `StreamLab` client of its own, as `user`, built by the generated `createClient`. */
function labAs(user: string) {
  const who = new LabUser(user);
  const controller = new AbortController();
  const lab = createClient(URL_!, [who], {
    // Node has no WebTransport, and the URL is plain http anyway.
    streamOptions: { transports: ["websocket"] },
    signal: controller.signal,
  }).StreamLab;
  return { lab, who, controller };
}

/** Runs `body` and returns what it threw; fails if it did not throw. */
async function thrown(body: () => Promise<unknown>): Promise<unknown> {
  try {
    await body();
  } catch (e) {
    return e;
  }
  throw new Error("expected the stream to throw, but it ended normally");
}

const range = (from: number, count: number) => Array.from({ length: count }, (_, i) => from + i);

/** The first byte of `payload` that is not `(n + i) % 251`, or -1. */
function firstBadByte(payload: Uint8Array, n: number): number {
  for (let i = 0; i < payload.length; i++) if (payload[i] !== (n + i) % 251) return i;
  return -1;
}

describe.skipIf(!URL_).concurrent("the stream client against the real server", () => {
  // Every scenario ends in a way that must not be retried; a reconnect anywhere is a bug.
  const reconnects: string[] = [];
  const onReconnecting = (attempt: number, delay: number) =>
    reconnects.push(`reconnecting attempt ${attempt} in ${delay} ms`);

  beforeAll(() => IonWsClient.on("reconnecting", onReconnecting));
  afterAll(() => {
    IonWsClient.off("reconnecting", onReconnecting);
    if (reconnects.length > 0) throw new Error(`a stream reconnected: ${reconnects.join("; ")}`);
  });

  it(`${RUN}-complete: Count(10, 50, 0) yields 10..59, then ends normally`, { timeout: SCENARIO_TIMEOUT }, async ({ expect }) => {
    const { lab, who } = labAs(`${RUN}-complete`);

    const items: number[] = [];
    for await (const n of lab.Count(10, 50, 0)) items.push(n);

    expect(items).toEqual(range(10, 50));
    expect(who.exchanges).toBe(1);
  });

  it(`${RUN}-break: the consumer stops after three items`, { timeout: SCENARIO_TIMEOUT }, async ({ expect }) => {
    const { lab, who } = labAs(`${RUN}-break`);

    const items: number[] = [];
    for await (const n of lab.Count(0, 1_000_000, 5)) {
      items.push(n);
      if (items.length === 3) break;
    }

    expect(items).toEqual([0, 1, 2]);
    expect(who.exchanges).toBe(1);
  });

  it(`${RUN}-abort: aborted through the AbortSignal after three items`, { timeout: SCENARIO_TIMEOUT }, async ({ expect }) => {
    const { lab, who, controller } = labAs(`${RUN}-abort`);

    const items: number[] = [];
    const error = await thrown(async () => {
      for await (const n of lab.Count(0, 1_000_000, 5)) {
        items.push(n);
        if (items.length === 3) controller.abort();
      }
    });

    expect(error).toBeInstanceOf(DOMException);
    expect((error as DOMException).name).toBe("AbortError");
    expect(items).toEqual([0, 1, 2]);
    expect(who.exchanges).toBe(1);
  });

  it(`${RUN}-error: Explode(2) yields 0 and 1, then fails with INTERNAL_ERROR`, { timeout: SCENARIO_TIMEOUT }, async ({ expect }) => {
    const { lab, who } = labAs(`${RUN}-error`);

    const items: number[] = [];
    const error = await thrown(async () => {
      for await (const n of lab.Explode(2)) items.push(n);
    });

    expect(items).toEqual([0, 1]);
    expect(error).toBeInstanceOf(IonRequestException);
    expect(error).not.toBeInstanceOf(IonStreamClosedError);
    expect(error).not.toBeInstanceOf(IonStreamDisconnectedError);
    expect((error as IonRequestException).error.code).toBe("INTERNAL_ERROR");
    expect(who.exchanges).toBe(1);
  });

  it(`banned-${RUN}: refused with BANNED, at once and without a retry`, { timeout: SCENARIO_TIMEOUT }, async ({ expect }) => {
    const { lab, who } = labAs(`banned-${RUN}`);

    const items: number[] = [];
    const started = performance.now();
    const error = await thrown(async () => {
      for await (const n of lab.Count(0, 1, 0)) items.push(n);
    });
    const elapsed = performance.now() - started;

    expect(error).toBeInstanceOf(IonRequestException);
    expect(error).not.toBeInstanceOf(IonStreamClosedError);
    expect(error).not.toBeInstanceOf(IonStreamDisconnectedError);
    expect((error as IonRequestException).error.code).toBe("BANNED");
    expect(items).toEqual([]);
    expect(who.exchanges).toBe(1);
    // A retry would sit in the reconnect backoff first; a refusal takes one round trip.
    expect(elapsed).toBeLessThan(5_000);
  });

  it(`${RUN}-kicked: the server closes the stream, "kicked", no reconnect`, { timeout: SCENARIO_TIMEOUT }, async ({ expect }) => {
    const { lab, who } = labAs(`${RUN}-kicked`);

    const items: LabEvent[] = [];
    const error = await thrown(async () => {
      for await (const e of lab.Listen(`kick-${RUN}`)) items.push(e);
    });

    expect(error).toBeInstanceOf(IonStreamClosedError);
    const closed = error as IonStreamClosedError;
    expect(closed.reason).toBe("kicked");
    expect(closed.allowReconnect).toBe(false);
    expect(closed.error.code).toBe("STREAM_CLOSED");
    expect(items).toEqual([]);
    expect(who.exchanges).toBe(1);
  });

  it(`${RUN}-push: three pushed LabEvents, then the consumer stops`, { timeout: SCENARIO_TIMEOUT }, async ({ expect }) => {
    const { lab, who } = labAs(`${RUN}-push`);
    const topic = `push-${RUN}`;

    const items: LabEvent[] = [];
    for await (const e of lab.Listen(topic)) {
      items.push(e);
      if (items.length === 3) break;
    }

    expect(items).toEqual([
      { seq: 1n, topic, body: "p1" },
      { seq: 2n, topic, body: "p2" },
      { seq: 3n, topic, body: "p3" },
    ]);
    expect(who.exchanges).toBe(1);
  });

  it(`${RUN}-union: Joined, Said, Left pushed to a stream of the union`, { timeout: SCENARIO_TIMEOUT }, async ({ expect }) => {
    const { lab, who } = labAs(`${RUN}-union`);

    const items: ILabSignal[] = [];
    for await (const s of lab.Signals(RUN)) {
      items.push(s);
      if (items.length === 3) break;
    }

    expect(items.map((s) => s.UnionKey)).toEqual(["Joined", "Said", "Left"]);
    expect(items.map((s) => s.UnionIndex)).toEqual([0, 2, 1]);

    const [joined, said, left] = items;
    expect(joined).toBeInstanceOf(Joined);
    expect(joined.isJoined() && joined.user).toBe("ann");
    expect(said).toBeInstanceOf(Said);
    expect(said.isSaid() && [said.user, said.text]).toEqual(["ann", "hi"]);
    expect(left).toBeInstanceOf(Left);
    expect(left.isLeft() && left.user).toBe("ann");
    expect(who.exchanges).toBe(1);
  });

  it(`${RUN}-echo: "a".."j" in, "A".."J" out, then a normal end`, { timeout: SCENARIO_TIMEOUT }, async ({ expect }) => {
    const { lab, who } = labAs(`${RUN}-echo`);
    const letters = "abcdefghij".split("");

    let inputDone = false;
    async function* input() {
      for (const letter of letters) yield letter;
      inputDone = true;
    }

    const items: string[] = [];
    for await (const s of lab.Echo(input())) items.push(s);

    expect(items).toEqual(letters.map((l) => l.toUpperCase()));
    expect(inputDone).toBe(true);
    expect(who.exchanges).toBe(1);
  });

  it(`${RUN}-blobs: three 1 MiB+3 payloads, byte for byte`, { timeout: SCENARIO_TIMEOUT }, async ({ expect }) => {
    const { lab, who } = labAs(`${RUN}-blobs`);
    const size = 1_048_579;

    const payloads: Uint8Array[] = [];
    for await (const b of lab.Blobs(size, 3)) payloads.push(b);

    expect(payloads).toHaveLength(3);
    payloads.forEach((payload, n) => {
      expect(payload).toBeInstanceOf(Uint8Array);
      expect(payload.length, `payload ${n} length`).toBe(size);
      expect(firstBadByte(payload, n), `payload ${n}: first byte that is not (n + i) % 251`).toBe(-1);
    });
    expect(who.exchanges).toBe(1);
  });

  it(`${RUN}-idle: survives 4 s of silence on heartbeats alone, then gets "still-here"`, { timeout: SCENARIO_TIMEOUT }, async ({ expect }) => {
    const { lab, who } = labAs(`${RUN}-idle`);
    const topic = `idle-${RUN}`;

    const started = performance.now();
    const items: LabEvent[] = [];
    for await (const e of lab.Listen(topic)) {
      items.push(e);
      break;
    }
    const elapsed = performance.now() - started;

    expect(items).toEqual([{ seq: 1n, topic, body: "still-here" }]);
    // The push comes 4 s after the stream started: well past the 1.5 s client timeout the server
    // announced, so only the client's pings — at its adapted interval — kept the connection up.
    expect(elapsed).toBeGreaterThan(3_500);
    expect(who.exchanges).toBe(1);
  });
});
