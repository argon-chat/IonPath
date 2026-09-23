import {
  CborWriter,
  type IonClientContext,
  type IonInterceptor,
  type IonStreamOptions,
  IonWsClient,
  type WebSocketLike,
  type WebTransportLike,
  type WebTransportStreamLike,
} from "../../src";
import { withVarintPrefix } from "../../src/ws/IonStreamWire";

/**
 * In-memory stand-ins for a WebSocket and a WebTransport session, with the server side scripted by
 * the test: accept or refuse, pick the sub-protocol, send frames (split into any chunks), drop
 * without a goodbye, go silent — and observe every frame the client sent.
 */

export const delay = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

export async function until(condition: () => boolean, what = "condition", timeoutMs = 3000) {
  const started = Date.now();
  while (!condition()) {
    if (Date.now() - started > timeoutMs) throw new Error(`Timed out waiting for ${what}`);
    await delay(2);
  }
}

/** Values in arrival order, awaitable one at a time. */
export class Inbox<T> {
  readonly all: T[] = [];
  private read = 0;
  private waiter: (() => void) | null = null;

  push(value: T): void {
    this.all.push(value);
    const waiter = this.waiter;
    this.waiter = null;
    waiter?.();
  }

  get count(): number {
    return this.all.length;
  }

  async next(what = "the next value", timeoutMs = 3000): Promise<T> {
    const started = Date.now();
    while (this.read >= this.all.length) {
      const left = timeoutMs - (Date.now() - started);
      if (left <= 0) throw new Error(`Timed out waiting for ${what}`);
      await new Promise<void>((resolve) => {
        const timer = setTimeout(resolve, left);
        this.waiter = () => {
          clearTimeout(timer);
          resolve();
        };
      });
    }
    return this.all[this.read++];
  }
}

// ─── frames ─────────────────────────────────────────────────────────────────

export const cbor = (write: (w: CborWriter) => void): Uint8Array => {
  const w = new CborWriter();
  write(w);
  return w.data.slice();
};

export const frame = (opcode: number, payload?: Uint8Array): Uint8Array => {
  const f = new Uint8Array(1 + (payload?.length ?? 0));
  f[0] = opcode;
  if (payload) f.set(payload, 1);
  return f;
};

/**
 * READY. With `resumeToken`, the five-element READY of a resumable session (window
 * `resumeWindowMs`); `trailing` elements a later version may add go after either form.
 */
export const READY = (
  keepAliveMs = 10_000,
  clientTimeoutMs = 0,
  trailing = 0,
  resumeToken?: string,
  resumeWindowMs = 30_000,
  inputBudget?: number
) =>
  frame(
    0x05,
    cbor((w) => {
      const base = resumeToken === undefined ? 3 : inputBudget === undefined ? 5 : 6;
      w.writeStartArray(base + trailing);
      w.writeTextString("conn-1");
      w.writeUInt32(keepAliveMs);
      w.writeUInt32(clientTimeoutMs);
      if (resumeToken !== undefined) {
        w.writeTextString(resumeToken);
        w.writeUInt32(resumeWindowMs);
        if (inputBudget !== undefined) w.writeUInt32(inputBudget);
      }
      for (let i = 0; i < trailing; i++) w.writeTextString(`added in v${3 + i}`);
      w.writeEndArray();
    })
  );

export const DATA_I4 = (n: number) => frame(0x00, cbor((w) => w.writeInt32(n)));
export const DATA_STR = (s: string) => frame(0x00, cbor((w) => w.writeTextString(s)));
export const END = Uint8Array.of(0x01);
export const PING = Uint8Array.of(0x03);

export const ERROR = (code: string, message: string) =>
  frame(
    0x02,
    cbor((w) => {
      w.writeStartArray(2);
      w.writeTextString(code);
      w.writeTextString(message);
      w.writeEndArray();
    })
  );

export const CLOSE = (reason: string | null, allowReconnect: boolean) =>
  frame(
    0x04,
    cbor((w) => {
      w.writeStartArray(2);
      if (reason === null) w.writeNull();
      else w.writeTextString(reason);
      w.writeBoolean(allowReconnect);
      w.writeEndArray();
    })
  );

/** What the client sends to leave: CLOSE `[null]`. */
export const CLIENT_CLOSE = Uint8Array.of(0x04, 0x81, 0xf6);
/** A client PING. */
export const CLIENT_PING = Uint8Array.of(0x03);
/** The client's end of its input stream: END. */
export const END_OF_INPUT = Uint8Array.of(0x01);
/** `[0x00] array(1)[n]` for a small non-negative `n`. */
export const INPUT_I4 = (n: number) => Uint8Array.of(0x00, 0x81, ...cbor((w) => w.writeInt32(n)));

/** The argument message: `[7, 3]`. */
export const ARGS = cbor((w) => {
  w.writeStartArray(2);
  w.writeInt32(7);
  w.writeInt32(3);
  w.writeEndArray();
});

export const isPing = (f: Uint8Array) => f.length === 1 && f[0] === 0x03;

// ─── WebSocket ──────────────────────────────────────────────────────────────

export class FakeWebSocket implements WebSocketLike {
  binaryType: BinaryType = "blob";
  protocol = "";
  readyState = 0;
  bufferedAmount = 0;
  onopen: ((ev: Event) => void) | null = null;
  onmessage: ((ev: MessageEvent) => void) | null = null;
  onclose: ((ev: CloseEvent) => void) | null = null;
  onerror: ((ev: Event) => void) | null = null;

  /** Every message the client sent, copied; the first is the argument message. */
  readonly sent = new Inbox<Uint8Array>();
  readonly closeCalls: { code?: number; reason?: string }[] = [];

  constructor(
    readonly url: string,
    readonly protocols: string[]
  ) {}

  /** Client frames after the argument message. */
  get frames(): Uint8Array[] {
    return this.sent.all.slice(1);
  }

  send(data: Uint8Array): void {
    if (this.readyState !== 1) throw new Error(`send() while readyState is ${this.readyState}`);
    this.sent.push(new Uint8Array(data));
  }

  close(code?: number, reason?: string): void {
    this.closeCalls.push(code === undefined && reason === undefined ? {} : { code, reason });
    this.readyState = 3;
  }

  // ─── server side ───────────────────────────────────────────────────────

  /** Completes the upgrade, selecting `protocol` (by default the one the client offered last). */
  accept(protocol = this.protocols[this.protocols.length - 1] ?? ""): void {
    this.protocol = protocol;
    this.readyState = 1;
    this.onopen?.({} as Event);
  }

  /** Fails the upgrade the way a browser reports it: error, then close 1006. */
  refuse(): void {
    this.readyState = 3;
    this.onerror?.({} as Event);
    this.onclose?.({ code: 1006, reason: "", wasClean: false } as CloseEvent);
  }

  /** Sends each frame as one binary message. */
  frame(...frames: Uint8Array[]): void {
    for (const f of frames) this.onmessage?.({ data: f.slice().buffer } as MessageEvent);
  }

  text(message: string): void {
    this.onmessage?.({ data: message } as MessageEvent);
  }

  /** Closes the socket without an Ion goodbye. */
  drop(code = 1006, reason = ""): void {
    this.readyState = 3;
    this.onclose?.({ code, reason, wasClean: code !== 1006 } as CloseEvent);
  }
}

// ─── WebTransport ───────────────────────────────────────────────────────────

export class FakeWebTransport implements WebTransportLike {
  readonly ready: Promise<void>;
  readonly closed: Promise<unknown>;

  /** Every frame the client wrote, reassembled from its varint framing; the first is the argument message. */
  readonly sent = new Inbox<Uint8Array>();
  readonly closeCalls: ({ closeCode?: number; reason?: string } | undefined)[] = [];
  writerClosed = false;
  writerAborted = false;
  /** Makes `createBidirectionalStream` reject with this. */
  failStream: unknown = null;

  private resolveReady!: () => void;
  private rejectReady!: (e: unknown) => void;
  private resolveClosed!: (v: unknown) => void;
  private rejectClosed!: (e: unknown) => void;
  private controller: ReadableStreamDefaultController<Uint8Array> | null = null;
  /** Server-side actions scripted before the client opened its stream; replayed once it does. */
  private backlog: ((c: ReadableStreamDefaultController<Uint8Array>) => void)[] = [];
  private clientBytes = new Uint8Array(0);

  constructor(readonly url: string) {
    this.ready = new Promise<void>((resolve, reject) => {
      this.resolveReady = resolve;
      this.rejectReady = reject;
    });
    this.closed = new Promise((resolve, reject) => {
      this.resolveClosed = resolve;
      this.rejectClosed = reject;
    });
  }

  get ticket(): string | null {
    return new URL(this.url).searchParams.get("ticket");
  }

  get frames(): Uint8Array[] {
    return this.sent.all.slice(1);
  }

  createBidirectionalStream(): Promise<WebTransportStreamLike> {
    if (this.failStream !== null) return Promise.reject(this.failStream);
    const readable = new ReadableStream<Uint8Array>({
      start: (c) => {
        this.controller = c;
        for (const action of this.backlog.splice(0)) action(c);
      },
    });
    const writable = new WritableStream<Uint8Array>({
      write: (chunk) => this.receive(chunk),
      close: () => {
        this.writerClosed = true;
      },
      abort: () => {
        this.writerAborted = true;
      },
    });
    return Promise.resolve({ readable, writable });
  }

  close(closeInfo?: { closeCode?: number; reason?: string }): void {
    this.closeCalls.push(closeInfo);
    try {
      this.controller?.error(new Error("The WebTransport session was closed"));
    } catch {
      // already closed
    }
    this.resolveClosed(closeInfo);
  }

  // ─── server side ───────────────────────────────────────────────────────

  accept(): void {
    this.resolveReady();
  }

  refuse(error: unknown = new Error("WebTransport connection refused")): void {
    this.rejectReady(error);
    this.rejectClosed(error);
  }

  /**
   * Sends the frames, each prefixed by its varint length, as one byte stream cut into chunks of
   * `chunkSizes` bytes (cycled) — or as a single chunk.
   */
  send(frames: Uint8Array[], chunkSizes?: number[]): void {
    const bytes = concat(frames.map(withVarintPrefix));
    this.act((c) => {
      if (chunkSizes === undefined) {
        c.enqueue(bytes);
        return;
      }
      for (let at = 0, i = 0; at < bytes.length; i++) {
        const size = Math.max(1, chunkSizes[i % chunkSizes.length]);
        c.enqueue(bytes.slice(at, at + size));
        at += size;
      }
    });
  }

  raw(bytes: Uint8Array): void {
    const copy = bytes.slice();
    this.act((c) => c.enqueue(copy));
  }

  /** The server's FIN. */
  finish(): void {
    this.act((c) => c.close());
  }

  /** The stream dies under the client. */
  reset(error: unknown = new Error("stream reset")): void {
    this.act((c) => c.error(error));
  }

  private act(action: (c: ReadableStreamDefaultController<Uint8Array>) => void): void {
    if (this.controller === null) this.backlog.push(action);
    else action(this.controller);
  }

  private receive(chunk: Uint8Array): void {
    this.clientBytes = concat([this.clientBytes, chunk]);
    for (;;) {
      const bytes = this.clientBytes;
      if (bytes.length === 0) return;
      const size = 1 << (bytes[0] >> 6);
      if (bytes.length < size) return;
      let length = bytes[0] & 0x3f;
      for (let i = 1; i < size; i++) length = length * 256 + bytes[i];
      if (bytes.length < size + length) return;
      this.sent.push(bytes.slice(size, size + length));
      this.clientBytes = bytes.slice(size + length);
    }
  }
}

export function concat(parts: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

// ─── harness ────────────────────────────────────────────────────────────────

export interface Harness {
  ctx: IonClientContext;
  sockets: Inbox<FakeWebSocket>;
  transports: Inbox<FakeWebTransport>;
  /** Base56 tickets in the order they were issued. */
  tickets: string[];
  client(): IonWsClient;
}

/**
 * A client context whose ticket exchange never touches HTTP — an interceptor answers it without
 * calling `next` — with a distinct ticket per exchange, and fake transports behind the factories.
 */
export function createHarness(
  options: IonStreamOptions = {},
  init: { webTransport?: boolean; baseUrl?: string; interceptors?: IonInterceptor[] } = {}
): Harness {
  const sockets = new Inbox<FakeWebSocket>();
  const transports = new Inbox<FakeWebTransport>();
  const tickets: string[] = [];
  let issued = 0;

  const ticketInterceptor: IonInterceptor = {
    async invokeAsync(c) {
      issued++;
      const bytes = Uint8Array.of(0x42, issued >> 8, issued & 0xff);
      c.responsePayload = cbor((w) => {
        w.writeStartArray(1);
        w.writeByteString(bytes);
        w.writeEndArray();
      });
      tickets.push(IonWsClient.prototype.toBase56(bytes));
    },
  };

  const ctx: IonClientContext = {
    baseUrl: init.baseUrl ?? "https://ion.test",
    sessionId: "session-1",
    interceptors: init.interceptors ?? [ticketInterceptor],
    streamOptions: {
      webSocketFactory: (url, protocols) => {
        const ws = new FakeWebSocket(url, protocols);
        sockets.push(ws);
        return ws;
      },
      webTransportFactory: init.webTransport
        ? (url) => {
            const wt = new FakeWebTransport(url);
            transports.push(wt);
            return wt;
          }
        : undefined,
      reconnect: { initialDelayMs: 5, maxDelayMs: 40, jitter: false },
      ...options,
    },
  };

  return {
    ctx,
    sockets,
    transports,
    tickets,
    client: () => new IonWsClient(ctx, "IStreamService", "Numbers"),
  };
}

/** Consumes a stream in the background, optionally stopping (`break`) after `stopAfter` items. */
export function consume<T>(stream: AsyncIterable<T>, stopAfter?: number) {
  const state = {
    items: [] as T[],
    error: undefined as unknown,
    settled: false,
    done: undefined as unknown as Promise<void>,
  };
  state.done = (async () => {
    try {
      for await (const item of stream) {
        state.items.push(item);
        if (stopAfter !== undefined && state.items.length >= stopAfter) break;
      }
    } catch (e) {
      state.error = e;
    } finally {
      state.settled = true;
    }
  })();
  return state;
}

/** Records the static reconnect events as strings, in order. */
export function recordEvents() {
  const log: string[] = [];
  const reconnecting = (attempt: number, delayMs: number) =>
    log.push(`reconnecting(${attempt},${delayMs})`);
  const reconnected = (attempt: number, resumed: boolean) =>
    log.push(resumed ? `reconnected(${attempt},resumed)` : `reconnected(${attempt})`);
  const closed = () => log.push("closed");
  IonWsClient.on("reconnecting", reconnecting);
  IonWsClient.on("reconnected", reconnected);
  IonWsClient.on("closed", closed);
  return {
    log,
    dispose() {
      IonWsClient.off("reconnecting", reconnecting);
      IonWsClient.off("reconnected", reconnected);
      IonWsClient.off("closed", closed);
    },
  };
}

/** A hand-fed async iterable: items are pushed by the test, and `return()` is observable. */
export function channel<T>() {
  type Step = { result: IteratorResult<T> } | { error: unknown };
  const queue: Step[] = [];
  let waiter: ((step: Step) => void) | null = null;
  const state = { returned: false, pulls: 0 };

  const deliver = (step: Step) => {
    if (waiter !== null) {
      const w = waiter;
      waiter = null;
      w(step);
    } else queue.push(step);
  };
  const settle = (step: Step): Promise<IteratorResult<T>> =>
    "error" in step ? Promise.reject(step.error) : Promise.resolve(step.result);

  const iterable: AsyncIterable<T> = {
    [Symbol.asyncIterator]: () => ({
      next: () => {
        state.pulls++;
        const step = queue.shift();
        if (step !== undefined) return settle(step);
        return new Promise<Step>((resolve) => {
          waiter = resolve;
        }).then(settle);
      },
      return: () => {
        state.returned = true;
        return Promise.resolve({ value: undefined, done: true } as IteratorResult<T>);
      },
    }),
  };

  return {
    iterable,
    state,
    push: (value: T) => deliver({ result: { value, done: false } }),
    end: () => deliver({ result: { value: undefined, done: true } as IteratorResult<T> }),
    fail: (error: unknown) => deliver({ error }),
  };
}
