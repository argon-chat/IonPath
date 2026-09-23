import { CborReader, CborWriter } from "../cbor";
import { CborReaderState } from "../cbor/CborReader";
import { type IonFormatter, IonFormatterStorage } from "../logic/IonFormatter";
import {
  type IonClientContext,
  type IonProtocolError,
  IonRequestException,
} from "../unary/IonUnaryRequest";
import {
  IonStreamClosedError,
  IonStreamDisconnectedError,
  type IonStreamDisconnectReason,
} from "./IonStreamErrors";
import type {
  IonStreamOptions,
  IonStreamTransportKind,
  WebSocketLike,
  WebTransportLike,
} from "./IonStreamOptions";
import {
  type StreamTransport,
  type TransportSink,
  WebSocketTransport,
  WebTransportTransport,
} from "./IonStreamTransports";
import {
  ACK_EVERY_BYTES,
  ACK_EVERY_FRAMES,
  ION_STREAM_VERSION,
  NOT_RESUMABLE_CODE,
  OP_ACK,
  OP_CLOSE,
  OP_DATA,
  OP_END,
  OP_ERROR,
  OP_PING,
  OP_READY,
  OP_RESUME,
  OP_RESUMED,
  RESUME_QUERY_PARAMETER,
} from "./IonStreamWire";

const noop = (): void => {};
const now = (): number => performance.now();

// ─── events ─────────────────────────────────────────────────────────────────

export type ReconnectEvents = {
  reconnecting: (attempt: number, delay: number) => void;
  /**
   * The call is connected again. `resumed` is true when its session was resumed — nothing lost,
   * nothing repeated — and false when it was started afresh: the stream method runs again.
   */
  reconnected: (attempt: number, resumed: boolean) => void;
  closed: () => void;
};

const listeners = new Map<keyof ReconnectEvents, Function[]>();

export function addStreamListener<K extends keyof ReconnectEvents>(
  event: K,
  cb: ReconnectEvents[K]
): void {
  let list = listeners.get(event);
  if (list === undefined) listeners.set(event, (list = []));
  list.push(cb);
}

export function removeStreamListener<K extends keyof ReconnectEvents>(
  event: K,
  cb: ReconnectEvents[K]
): void {
  const list = listeners.get(event);
  const at = list?.indexOf(cb) ?? -1;
  if (at >= 0) list!.splice(at, 1);
}

/**
 * Calls every listener, each in isolation: these run inside transport callbacks, and a listener
 * that throws must not decide how a stream ends.
 */
function emit<K extends keyof ReconnectEvents>(
  event: K,
  ...args: Parameters<ReconnectEvents[K]>
): void {
  const list = listeners.get(event);
  if (list === undefined || list.length === 0) return;
  for (const cb of list.slice()) {
    try {
      (cb as (...a: Parameters<ReconnectEvents[K]>) => void)(...args);
    } catch (e) {
      console.error(`An IonWsClient '${event}' listener threw`, e);
    }
  }
}

// ─── transport health ───────────────────────────────────────────────────────

/**
 * Origins whose WebTransport failed to connect, and until when to skip it.
 *
 * A network that drops UDP makes every WebTransport attempt cost the full connect timeout before
 * falling back; remembering the failure is what keeps that price to once per origin per window
 * instead of once per call.
 */
const webTransportBlockedUntil = new Map<string, number>();

export function resetTransportHealth(): void {
  webTransportBlockedUntil.clear();
}

function isWebTransportBlocked(origin: string): boolean {
  const until = webTransportBlockedUntil.get(origin);
  if (until === undefined) return false;
  if (now() < until) return true;
  webTransportBlockedUntil.delete(origin);
  return false;
}

function blockWebTransport(origin: string, forMs: number): void {
  if (forMs > 0) webTransportBlockedUntil.set(origin, now() + forMs);
}

// ─── options ────────────────────────────────────────────────────────────────

export interface ReconnectPolicy {
  readonly initialDelayMs: number;
  readonly maxDelayMs: number;
  readonly maxAttempts: number;
  readonly jitter: boolean;
}

export interface ResolvedStreamOptions {
  readonly transports: readonly IonStreamTransportKind[];
  readonly reconnect: ReconnectPolicy | null;
  /** Ask for a resumable session: `resume`, and a way back to use it. */
  readonly resume: boolean;
  readonly resumeBufferSize: number;
  readonly keepAliveIntervalMs: number;
  readonly serverTimeoutMs: number;
  readonly handshakeTimeoutMs: number;
  readonly webTransportConnectTimeoutMs: number;
  readonly webTransportRetryAfterMs: number;
  readonly webSocketFactory: ((url: string, protocols: string[]) => WebSocketLike) | null;
  readonly webTransportFactory: ((url: string) => WebTransportLike) | null;
}

const DEFAULT_TRANSPORTS: readonly IonStreamTransportKind[] = ["webtransport", "websocket"];

const globalWebSocket = (url: string, protocols: string[]): WebSocketLike =>
  new WebSocket(url, protocols);
const globalWebTransport = (url: string): WebTransportLike => new WebTransport(url);

/** Fills in the defaults. The globals are looked up per call, so a polyfill installed later is seen. */
export function resolveStreamOptions(o: IonStreamOptions | undefined): ResolvedStreamOptions {
  const rc = o?.reconnect;
  return {
    transports: o?.transports ?? DEFAULT_TRANSPORTS,
    reconnect:
      rc === false
        ? null
        : {
            initialDelayMs: rc?.initialDelayMs ?? 1000,
            maxDelayMs: rc?.maxDelayMs ?? 30000,
            maxAttempts: rc?.maxAttempts ?? Infinity,
            jitter: rc?.jitter ?? true,
          },
    resume: rc !== false && (o?.resume ?? true),
    resumeBufferSize: Math.max(o?.resumeBufferSize ?? 1 << 20, 2 * ACK_EVERY_BYTES),
    keepAliveIntervalMs: o?.keepAliveIntervalMs ?? 15000,
    serverTimeoutMs: o?.serverTimeoutMs ?? 30000,
    handshakeTimeoutMs: o?.handshakeTimeoutMs ?? 15000,
    webTransportConnectTimeoutMs: o?.webTransportConnectTimeoutMs ?? 3000,
    webTransportRetryAfterMs: o?.webTransportRetryAfterMs ?? 5 * 60_000,
    webSocketFactory:
      o?.webSocketFactory ?? (typeof WebSocket === "function" ? globalWebSocket : null),
    webTransportFactory:
      o?.webTransportFactory ?? (typeof WebTransport === "function" ? globalWebTransport : null),
  };
}

// ─── ticket exchange classification ─────────────────────────────────────────

/** Filled in by the terminal of the ticket exchange, so a failure can be classified afterwards. */
export interface ExchangeProbe {
  /** The request never got a response: DNS, TLS, a dropped connection, CORS. */
  networkError: boolean;
}

/**
 * A ticket exchange that failed on the network or with 5xx/408/429 is worth retrying; one the
 * server refused (any other 4xx) is an authentication problem, and reconnecting — or falling back
 * to another transport, which needs a ticket just the same — would only hammer it.
 */
function isRetryableExchangeFailure(e: unknown, probe: ExchangeProbe): boolean {
  if (probe.networkError) return true;
  if (e instanceof IonRequestException) {
    const status = e.status;
    return status !== undefined && (status === 408 || status === 429 || status >= 500);
  }
  // What `fetch` throws for a network failure, when an interceptor made the request itself.
  return e instanceof TypeError;
}

// ─── URLs ───────────────────────────────────────────────────────────────────

function toWebSocketUrl(httpUrl: string): string {
  const u = new URL(httpUrl);
  switch (u.protocol) {
    case "http:":
      u.protocol = "ws:";
      break;
    case "https:":
      u.protocol = "wss:";
      break;
    case "ws:":
    case "wss:":
      break;
    default:
      throw new Error(`Invalid URL protocol: ${u.protocol}`);
  }
  if ((u.protocol === "ws:" && u.port === "80") || (u.protocol === "wss:" && u.port === "443")) {
    u.port = "";
  }
  return trimTrailingSlash(u.toString());
}

function trimTrailingSlash(url: string): string {
  return url.endsWith("/") ? url.slice(0, -1) : url;
}

// ─── resumable sessions ─────────────────────────────────────────────────────

/**
 * The part of a call that outlives its connections on a resumable session: the counts of reliable
 * frames each side has taken, and the client's frames the server has not acknowledged yet — kept
 * unframed, so a resume may send them on another kind of transport.
 *
 * A session numbers its reliable frames (DATA, END, ERROR, CLOSE) from 1 on each side; nothing on
 * the wire carries the number, both sides count. See `IonStreamProtocol` in C#.
 */
export class ResumeState {
  /** The secret a RESUME presents; `null` while there is no resumable session. */
  token: string | null = null;
  windowMs = 0;
  /** When the last connection was lost (`performance.now()`), 0 while connected. */
  lostAt = 0;
  /** The first READY's timings, which a resumed connection keeps. */
  keepAliveMs = 0;
  clientTimeoutMs = 0;

  /** The server's reliable frames taken, and their bytes. */
  received = 0;
  receivedBytes = 0;
  /** What the last ACK said. */
  ackSent = 0;
  ackSentBytes = 0;

  /** Our reliable frames sent, and how many of them the server acknowledged. */
  sent = 0;
  peerAcked = 0;

  /** True while frames are kept: from a fresh connection's first frame until its READY says whether the session is resumable. */
  keeping = false;
  /** END of input went out on this session: a session started afresh must hear it again. */
  endSent = false;

  private replay: Uint8Array[] = [];
  private head = 0;
  private bytes = 0;
  private waiters: (() => void)[] = [];

  /** The replay budget: ours, or the server's announced input budget if that is smaller. */
  budget: number;

  constructor(private readonly ownBudget: number) {
    this.budget = ownBudget;
  }

  /** The server holds no more unacknowledged input than it announced; neither may we send more. */
  adoptServerBudget(serverBudget: number): void {
    this.budget = serverBudget > 0 ? Math.min(this.ownBudget, serverBudget) : this.ownBudget;
  }

  /** A reliable frame went out: numbered, and kept until acknowledged when the session may be resumed. */
  sentReliable(frame: Uint8Array | null): void {
    this.sent++;
    if (this.keeping && frame !== null) {
      this.replay.push(frame);
      this.bytes += frame.length;
    }
  }

  /** What the server has not acknowledged, oldest first. */
  unacknowledged(): Uint8Array[] {
    return this.replay.slice(this.head);
  }

  /** The server has our frames up to `acked`. */
  acknowledge(acked: number): void {
    this.peerAcked = acked;
    let seq = this.sent - (this.replay.length - this.head) + 1;
    while (this.head < this.replay.length && seq <= acked) {
      this.bytes -= this.replay[this.head].length;
      this.replay[this.head++] = EMPTY;
      seq++;
    }
    if (this.head === this.replay.length) {
      this.replay.length = 0;
      this.head = 0;
    } else if (this.head >= 1024 && this.head * 2 >= this.replay.length) {
      this.replay.splice(0, this.head);
      this.head = 0;
    }
    this.wake();
  }

  /**
   * Whether one more frame would overspend the replay budget — its bytes, or its frame count: tiny
   * frames would otherwise put tens of thousands in flight under the byte budget alone.
   */
  get full(): boolean {
    const kept = this.replay.length - this.head;
    return this.keeping && kept > 0 && (this.bytes >= this.budget || kept >= MAX_UNACKNOWLEDGED_FRAMES);
  }

  /** Settles when an acknowledgement makes room, or the connection ends. */
  whenRoom(): Promise<void> {
    return new Promise<void>((resolve) => this.waiters.push(resolve));
  }

  wake(): void {
    const waiters = this.waiters;
    if (waiters.length === 0) return;
    this.waiters = [];
    for (const resolve of waiters) resolve();
  }

  /** READY said the session is not resumable: nothing to keep after all. */
  disable(): void {
    this.keeping = false;
    this.token = null;
    this.dropReplay();
  }

  /**
   * The session is gone; the next connection starts the call afresh. Input the dead session never
   * acknowledged is lost with it — the new stream method knows nothing of it — but an END it had
   * is said again.
   */
  forget(): void {
    this.token = null;
    this.received = this.receivedBytes = this.ackSent = this.ackSentBytes = 0;
    this.sent = this.peerAcked = 0;
    this.dropReplay();
  }

  private dropReplay(): void {
    this.replay.length = 0;
    this.head = 0;
    this.bytes = 0;
    this.wake();
  }
}

const EMPTY = new Uint8Array(0);

/** The most frames kept unacknowledged, whatever their size. Mirrors `IonStreamProtocol.MaxUnacknowledgedFrames`. */
const MAX_UNACKNOWLEDGED_FRAMES = 4096;
const END_OF_INPUT = Uint8Array.of(OP_END);

// ─── the call ───────────────────────────────────────────────────────────────

/** What a {@link StreamSession} needs from the call it serves. */
export interface SessionHost<TResponse> {
  readonly opts: ResolvedStreamOptions;
  /** The session state that outlives connections. */
  readonly resume: ResumeState;
  /** One reader for every payload of the call; see {@link CborReader.reset}. */
  readonly reader: CborReader;
  readonly requestPayload: Uint8Array;
  responseFormatter(): IonFormatter<TResponse>;
  /** The transport is open and the arguments are sent. */
  onSessionOpen(session: StreamSession<TResponse>): void;
  /** The server accepted the connection — READY, or RESUMED. */
  onReady(session: StreamSession<TResponse>): void;
}

export interface StreamCallInit<TRequest> {
  context: IonClientContext;
  interfaceName: string;
  methodName: string;
  exchangeTicket: (signal: AbortSignal | undefined, probe: ExchangeProbe) => Promise<string>;
  responseTypename: string;
  requestPayload: Uint8Array;
  input: { stream: AsyncIterable<TRequest>; typename: string } | null;
  signal: AbortSignal | undefined;
  correlationId: string | undefined;
}

/**
 * One `stream` call, across every connection it takes: transport selection and fallback, the
 * reconnect loop, the consumer-facing generator and the input pump.
 *
 * ## Who decides what
 *
 * A {@link StreamSession} owns exactly one connection attempt and decides how *it* ended — END,
 * ERROR, CLOSE, a lost transport, a client-side fault. It receives frames as they arrive, even
 * while the consumer is busy, and queues decoded items; so heartbeats and timeouts never wait on a
 * slow consumer. This class reads that outcome and decides what happens to the *call*: return,
 * throw, try the next transport, or back off and reconnect.
 *
 * ## The input stream
 *
 * One pump per call, not per connection: it pulls the next item only while a connection can take
 * it, and an item pulled while the connection died is held and sent on the next one. On a resumed
 * session nothing is lost: what the server did not acknowledge is sent again, exactly once. On a
 * call started afresh the same iterator simply continues — the server starts a fresh stream
 * method, so items that had already been sent on the dead connection are **not** replayed; they
 * are lost with it. An input that completed before the drop is ended again right after the new
 * connection's arguments.
 */
export class IonStreamCall<TResponse, TRequest> implements SessionHost<TResponse> {
  readonly opts: ResolvedStreamOptions;
  readonly reader = new CborReader(new Uint8Array(0));
  readonly requestPayload: Uint8Array;
  readonly resume: ResumeState;

  private readonly init: StreamCallInit<TRequest>;
  private formatter: IonFormatter<TResponse> | null = null;
  private baseUrl: URL | null = null;
  private wsUrl: string | null = null;

  private session: StreamSession<TResponse> | null = null;
  private aborted = false;
  /** True while the generator is parked at a `yield` — the only place a consumer can end it. */
  private suspended = false;
  private wakeSleep: (() => void) | null = null;

  private inputIterator: AsyncIterator<TRequest> | null = null;
  private inputFormatter: IonFormatter<TRequest> | null = null;
  private pumpStarted = false;
  private inputCompleted = false;
  private inputReleased = false;
  private inputFault: { error: unknown } | null = null;
  private sessionWaiter: ((session: StreamSession<TResponse> | null) => void) | null = null;
  /** The size of the last input frame, so the next one's buffer is allocated once, at the right size. */
  private inputSizeHint = 64;

  constructor(init: StreamCallInit<TRequest>) {
    this.init = init;
    this.requestPayload = init.requestPayload;
    this.opts = resolveStreamOptions(init.context.streamOptions);
    this.resume = new ResumeState(this.opts.resumeBufferSize);
  }

  async *run(): AsyncGenerator<TResponse, void, unknown> {
    const signal = this.init.signal;
    if (signal !== undefined) {
      if (signal.aborted) {
        this.aborted = true;
        emit("closed");
        throw abortError();
      }
      signal.addEventListener("abort", this.onAbort);
    }

    try {
      if (this.init.input !== null)
        this.inputIterator = this.init.input.stream[Symbol.asyncIterator]();

      let attempt = 0;
      for (;;) {
        let failure: unknown;
        let startAfresh = false;
        const kinds = this.transportOrder();

        transports: for (let i = 0; i < kinds.length; i++) {
          const kind = kinds[i];
          const resuming = this.canResume();
          if (!resuming) this.resume.forget();

          // A fresh ticket per transport attempt: tickets may be single-use.
          const probe: ExchangeProbe = { networkError: false };
          let ticket: string;
          try {
            ticket = await this.init.exchangeTicket(signal, probe);
          } catch (e) {
            this.throwIfEnded();
            if (this.opts.reconnect === null || !isRetryableExchangeFailure(e, probe)) throw e;
            failure = e;
            break;
          }
          this.throwIfEnded();

          const session = new StreamSession<TResponse>(
            this,
            kind,
            kind === "webtransport" ? this.webTransportUrl(ticket) : this.webSocketUrl(),
            ticket,
            attempt,
            resuming
          );
          this.session = session;
          try {
            session.start();
          } catch (e) {
            // Nothing was opened, so there is nothing to close; another transport may still work.
            this.session = null;
            if (kind === "webtransport")
              blockWebTransport(this.url().origin, this.opts.webTransportRetryAfterMs);
            if (i + 1 < kinds.length) continue;
            throw e;
          }

          while (!this.aborted) {
            if (session.hasItem) {
              this.suspended = true;
              yield session.take();
              this.suspended = false;
            } else if (session.outcome !== null) {
              break;
            } else {
              await session.wait();
            }
          }

          this.session = null;
          this.throwIfEnded();
          if (session.ready) attempt = 0;

          const outcome = session.outcome!;
          switch (outcome.kind) {
            case "end":
              return;
            case "notResumable":
              // An answer, not a failure: the session is gone, so the call starts afresh — now.
              this.resume.forget();
              startAfresh = true;
              break transports;
            case "error":
              throw new IonRequestException(outcome.error);
            case "fault":
              throw outcome.error;
            case "left":
              // Only an abort leaves a session from outside, and throwIfEnded handled that.
              throw abortError();
            case "closed": {
              const error = new IonStreamClosedError(outcome.reason, outcome.allowReconnect);
              if (!outcome.allowReconnect || this.opts.reconnect === null) throw error;
              failure = error;
              break transports;
            }
            case "failed": {
              const error = outcome.error;
              // Failing before the connection existed says something about the transport on this
              // network, not about the call: try the next transport now, without a backoff.
              // WebTransport counts as "before" until READY, because a session that opens and
              // then carries nothing is exactly what a proxy that breaks HTTP/3 produces.
              const beforeConnected =
                outcome.stage === "connect" ||
                (outcome.stage === "handshake" && kind === "webtransport");
              if (error.retryable && beforeConnected) {
                if (kind === "webtransport")
                  blockWebTransport(this.url().origin, this.opts.webTransportRetryAfterMs);
                if (i + 1 < kinds.length) continue transports;
              }
              if (!error.retryable || this.opts.reconnect === null) throw error;
              if (outcome.stage === "stream") this.resume.lostAt = now();
              failure = error;
              break transports;
            }
          }
        }

        if (startAfresh) continue;

        const policy = this.opts.reconnect!;
        attempt++;
        if (attempt > policy.maxAttempts) throw failure;

        const delay = backoffDelay(policy, attempt);
        emit("reconnecting", attempt, delay);
        await this.sleep(delay);
        this.throwIfEnded();
      }
    } finally {
      signal?.removeEventListener("abort", this.onAbort);
      const session = this.session;
      this.session = null;
      session?.leave();
      this.releaseInput();
      if (this.suspended && !this.aborted) emit("closed");
    }
  }

  // ─── SessionHost ───────────────────────────────────────────────────────

  responseFormatter(): IonFormatter<TResponse> {
    return (this.formatter ??= IonFormatterStorage.get<TResponse>(this.init.responseTypename));
  }

  onSessionOpen(session: StreamSession<TResponse>): void {
    if (this.inputIterator === null || this.inputReleased || this.inputFault !== null) return;
    if (this.inputCompleted) {
      // A resumed session has its END already, acknowledged or waiting in the replay.
      if (!session.resuming) session.sendEndOfInput();
      return;
    }
    if (!this.pumpStarted) {
      this.pumpStarted = true;
      void this.pump();
      return;
    }
    const waiter = this.sessionWaiter;
    if (waiter !== null) {
      this.sessionWaiter = null;
      waiter(session);
    }
  }

  onReady(session: StreamSession<TResponse>): void {
    this.resume.lostAt = 0;
    if (session.attempt > 0) emit("reconnected", session.attempt, session.resuming);
  }

  /** Whether the next connection should resume the session rather than start the call afresh. */
  private canResume(): boolean {
    const r = this.resume;
    if (!this.opts.resume || r.token === null) return false;
    return r.windowMs <= 0 || r.lostAt === 0 || now() - r.lostAt < r.windowMs;
  }

  // ─── abort, sleep ──────────────────────────────────────────────────────

  private readonly onAbort = (): void => {
    if (this.aborted) return;
    this.aborted = true;
    // Leave now rather than on the consumer's next pull: it may never come.
    this.session?.leave();
    this.releaseInput();
    this.wakeSleeper();
    emit("closed");
  };

  private throwIfEnded(): void {
    if (this.aborted) throw abortError();
    if (this.inputFault !== null) throw this.inputFault.error;
  }

  private sleep(ms: number): Promise<void> {
    return new Promise<void>((resolve) => {
      const timer = setTimeout(() => {
        this.wakeSleep = null;
        resolve();
      }, ms);
      this.wakeSleep = () => {
        this.wakeSleep = null;
        clearTimeout(timer);
        resolve();
      };
    });
  }

  private wakeSleeper(): void {
    this.wakeSleep?.();
  }

  // ─── input pump ────────────────────────────────────────────────────────

  private async pump(): Promise<void> {
    const iterator = this.inputIterator!;
    let pending: IteratorResult<TRequest> | null = null;

    try {
      for (;;) {
        if (this.inputReleased) return;

        const session = this.session !== null && this.session.canSend ? this.session : null;
        if (session === null) {
          const next = await new Promise<StreamSession<TResponse> | null>((resolve) => {
            this.sessionWaiter = resolve;
          });
          if (next === null) return;
          continue;
        }

        if (pending === null) {
          const full = session.backpressure();
          if (full !== undefined) {
            await full.then(noop, noop);
          } else {
            pending = await iterator.next();
          }
          // Either way the connection may have changed while this waited.
          continue;
        }

        if (pending.done) {
          this.inputCompleted = true;
          session.sendEndOfInput();
          return;
        }

        const size = session.sendInput(
          pending.value,
          (this.inputFormatter ??= IonFormatterStorage.get<TRequest>(this.init.input!.typename)),
          this.inputSizeHint
        );
        this.inputSizeHint = Math.min(Math.max(64, size + (size >> 2)), 64 * 1024);
        pending = null;
      }
    } catch (e) {
      if (!this.inputReleased) this.onInputFault(e);
    }
  }

  /**
   * The input iterable threw (or an item could not be encoded). The server is told with an
   * ERROR frame, and the consumer gets the original error — not a wrapper — from its next pull.
   */
  private onInputFault(error: unknown): void {
    if (this.inputFault !== null) return;
    this.inputFault = { error };
    this.session?.faultInput(error);
    this.wakeSleeper();
  }

  /** The call is over: stop the pump and let the input source clean up, as `for await` would. */
  private releaseInput(): void {
    if (this.inputReleased) return;
    this.inputReleased = true;

    const waiter = this.sessionWaiter;
    this.sessionWaiter = null;
    waiter?.(null);

    const iterator = this.inputIterator;
    if (iterator !== null && !this.inputCompleted && this.inputFault === null && iterator.return) {
      try {
        Promise.resolve(iterator.return()).then(noop, noop);
      } catch {
        // The input's own cleanup failed; nothing is waiting on it.
      }
    }
  }

  // ─── transports ────────────────────────────────────────────────────────

  private url(): URL {
    return (this.baseUrl ??= new URL(this.init.context.baseUrl));
  }

  private transportOrder(): IonStreamTransportKind[] {
    const order: IonStreamTransportKind[] = [];
    for (const kind of this.opts.transports)
      if (!order.includes(kind) && this.isAvailable(kind)) order.push(kind);

    if (order.length === 0)
      throw new Error(
        `No Ion stream transport is available (configured: ${this.opts.transports.join(", ") || "none"}). ` +
          "WebSocket needs a global WebSocket or streamOptions.webSocketFactory; WebTransport needs a " +
          "global WebTransport or streamOptions.webTransportFactory, and an https: base URL."
      );

    // Skipped only while something else is left to try; a WebTransport-only client keeps trying it.
    if (order.length > 1 && isWebTransportBlocked(this.url().origin)) {
      const at = order.indexOf("webtransport");
      if (at >= 0) order.splice(at, 1);
    }
    return order;
  }

  private isAvailable(kind: IonStreamTransportKind): boolean {
    switch (kind) {
      case "websocket":
        // Built here so that a base URL no WebSocket can reach throws, rather than retrying forever.
        return this.opts.webSocketFactory !== null && this.webSocketUrl() !== "";
      case "webtransport":
        return this.opts.webTransportFactory !== null && this.url().protocol === "https:";
      default:
        return false;
    }
  }

  private webSocketUrl(): string {
    if (this.wsUrl === null) {
      const url = `${toWebSocketUrl(this.init.context.baseUrl)}/ion/${this.init.interfaceName}/${this.init.methodName}.ws`;
      // Browsers cannot set headers on an upgrade, so the query string stands in for them.
      const query = this.identity(new URLSearchParams()).toString();
      this.wsUrl = query ? `${url}?${query}` : url;
    }
    return this.wsUrl;
  }

  /** WebTransport cannot carry sub-protocols either; the ticket and version go in the query too. */
  private webTransportUrl(ticket: string): string {
    const params = new URLSearchParams();
    params.set("ticket", ticket);
    params.set("ver", String(ION_STREAM_VERSION));
    this.identity(params);
    return `${trimTrailingSlash(this.url().toString())}/ion/${this.init.interfaceName}/${this.init.methodName}.wt?${params}`;
  }

  private identity(params: URLSearchParams): URLSearchParams {
    if (this.init.context.sessionId) params.set("sid", this.init.context.sessionId);
    if (this.init.correlationId) params.set("cid", this.init.correlationId);
    if (this.opts.resume) params.set(RESUME_QUERY_PARAMETER, "1");
    return params;
  }
}

function abortError(): DOMException {
  return new DOMException("Aborted", "AbortError");
}

/** Exponential, capped, with full jitter; `attempt` counts from 1. */
function backoffDelay(policy: ReconnectPolicy, attempt: number): number {
  const bound = Math.min(policy.maxDelayMs, policy.initialDelayMs * 2 ** (attempt - 1));
  return policy.jitter ? Math.floor(Math.random() * bound) : bound;
}

// ─── one connection attempt ─────────────────────────────────────────────────

export type Stage = "connect" | "handshake" | "stream";

export type SessionOutcome =
  /** END: the stream method completed. */
  | { readonly kind: "end" }
  /** ERROR: the stream method (or the server) failed the call. */
  | { readonly kind: "error"; readonly error: IonProtocolError }
  /** CLOSE: the server ended the stream on purpose. */
  | { readonly kind: "closed"; readonly reason: string | null; readonly allowReconnect: boolean }
  /** No goodbye: the transport failed, timed out or broke the protocol, during `stage`. */
  | { readonly kind: "failed"; readonly error: IonStreamDisconnectedError; readonly stage: Stage }
  /** This client failed: an item it could not decode, an input stream that threw. */
  | { readonly kind: "fault"; readonly error: unknown }
  /** This client left: the consumer stopped reading, or the call was aborted. */
  | { readonly kind: "left" }
  /** A RESUME the server could not honour: the session is gone, and the call starts afresh. */
  | { readonly kind: "notResumable" };

const END: SessionOutcome = { kind: "end" };
const LEFT: SessionOutcome = { kind: "left" };
const NOT_RESUMABLE: SessionOutcome = { kind: "notResumable" };

// Phases of a session. Plain numbers rather than an enum: nothing outside this file sees them.
const OPENING = 0;
const HANDSHAKE = 1;
const STREAMING = 2;
const DONE = 3;

/** The longest input-failure message sent to the server; the frame must stay well inside its receive limit. */
const MAX_ERROR_MESSAGE = 1024;

/**
 * One connection attempt over one transport, from opening it to the first thing that ends it.
 *
 * Frames are handled as they arrive: DATA is decoded on the spot with the call's one reader and
 * queued, so the transport's buffers are free again as soon as the handler returns, and control
 * frames — PING, READY, the goodbyes — are never stuck behind items the consumer has not read yet.
 *
 * One timer serves the whole session. It is re-armed only when it fires, to the next deadline
 * computed from `lastSentAt`/`lastReceivedAt`; a frame costs a timestamp, never a timer.
 */
export class StreamSession<TResponse> implements TransportSink {
  /** How the session ended; `null` while it runs. Set once. */
  outcome: SessionOutcome | null = null;
  /** READY arrived: the server accepted the connection. */
  ready = false;

  private phase = OPENING;
  private opened = false;
  private transport: StreamTransport | null = null;
  /** The transport was closed or aborted; the session has nothing left to end. */
  private released = false;

  private readonly queue: TResponse[] = [];
  private head = 0;
  private waiter: (() => void) | null = null;

  private timer: ReturnType<typeof setTimeout> | undefined = undefined;
  private lastSentAt = 0;
  private lastReceivedAt = 0;
  private keepAliveMs = 0;
  private serverTimeoutMs = 0;

  constructor(
    private readonly host: SessionHost<TResponse>,
    readonly kind: IonStreamTransportKind,
    private readonly url: string,
    private readonly ticket: string,
    /** The reconnect attempt this session is, 0 for the first connection. */
    readonly attempt: number,
    /** This connection resumes the call's session rather than starting it. */
    readonly resuming: boolean = false
  ) {}

  /**
   * Creates the transport and arms the connect deadline. Throws what the factory throws: a
   * transport that cannot even be constructed is a local problem — a malformed URL, a missing
   * implementation — and no amount of reconnecting fixes it.
   */
  start(): void {
    const opts = this.host.opts;
    this.transport =
      this.kind === "webtransport"
        ? new WebTransportTransport(this, opts.webTransportFactory!, this.url)
        : new WebSocketTransport(this, opts.webSocketFactory!, this.url, [
            // v2 only. A v1-only server either selects nothing or echoes this entry, and then
            // sends DATA without READY — a protocol violation here, never run as a v1 stream this
            // client has no disconnect semantics for.
            `ion!ticket#${this.ticket}!ver#${ION_STREAM_VERSION}`,
          ]);
    this.arm(
      this.kind === "webtransport" ? opts.webTransportConnectTimeoutMs : opts.handshakeTimeoutMs
    );
  }

  // ─── the consumer's side ───────────────────────────────────────────────

  get hasItem(): boolean {
    return this.head < this.queue.length;
  }

  take(): TResponse {
    const queue = this.queue;
    const item = queue[this.head];
    queue[this.head++] = undefined as TResponse;
    if (this.head === queue.length) {
      queue.length = 0;
      this.head = 0;
    } else if (this.head >= 1024 && this.head * 2 >= queue.length) {
      // A consumer that never quite catches up would otherwise grow a queue of holes.
      queue.splice(0, this.head);
      this.head = 0;
    }
    return item;
  }

  /** Settles when an item is queued or the session ends. */
  wait(): Promise<void> {
    return new Promise<void>(this.park);
  }

  private readonly park = (resolve: () => void): void => {
    this.waiter = resolve;
  };

  private wake(): void {
    const waiter = this.waiter;
    if (waiter !== null) {
      this.waiter = null;
      waiter();
    }
  }

  // ─── the input pump's side ─────────────────────────────────────────────

  /**
   * The transport is open and nothing has ended the session. A resuming connection sends nothing
   * new until RESUMED: the replay goes first.
   */
  get canSend(): boolean {
    return this.phase === STREAMING || (this.phase === HANDSHAKE && !this.resuming);
  }

  backpressure(): Promise<unknown> | undefined {
    const waiting = this.transport!.backpressure();
    if (waiting !== undefined) return waiting;
    // A resumable session keeps what it sent until the server acknowledges it; when that budget is
    // spent the input waits — it slows down, it never drops.
    return this.host.resume.full ? this.host.resume.whenRoom() : undefined;
  }

  /** Encodes `[0x00] array(1)[item]` straight into the buffer it is sent from; returns its size. */
  sendInput<TRequest>(item: TRequest, formatter: IonFormatter<TRequest>, sizeHint: number): number {
    const transport = this.transport!;
    const writer = new CborWriter(sizeHint);
    writer.writeEncodedValue(transport.dataPrefix);
    writer.writeStartArray(1);
    formatter.write(writer, item);
    writer.writeEndArray();
    const frame = writer.data;
    // Kept unframed before it is sent: the transport writes its length prefix into the headroom,
    // not into the frame, and copies what it sends.
    this.host.resume.sentReliable(frame.subarray(transport.headroom));
    transport.sendBuilt(frame);
    this.lastSentAt = now();
    return frame.length;
  }

  sendEndOfInput(): void {
    const transport = this.transport!;
    this.host.resume.sentReliable(END_OF_INPUT);
    this.host.resume.endSent = true;
    transport.send(transport.endOfInputFrame);
    this.lastSentAt = now();
  }

  // ─── endings the call asks for ─────────────────────────────────────────

  /** The consumer stopped reading, or the call was aborted: say goodbye if the server can hear it. */
  leave(): void {
    if (this.outcome === null) this.settle(LEFT);
    this.goodbye("client closed");
  }

  /** The input stream failed: tell the server with an ERROR frame, then leave. */
  faultInput(error: unknown): void {
    if (this.outcome !== null) return;
    if (this.canSend) this.sendInputError(error);
    this.fault(error);
  }

  // ─── TransportSink ─────────────────────────────────────────────────────

  onOpen(): void {
    if (this.outcome !== null) return;
    const transport = this.transport!;

    if (transport.kind === "websocket") {
      const problem = negotiationProblem(transport.protocol);
      if (problem !== null) {
        this.violate(problem);
        return;
      }
    }

    this.opened = true;
    this.phase = HANDSHAKE;
    const state = this.host.resume;
    if (this.resuming) {
      transport.sendArgs(resumeFrame(state.token!, state.received));
    } else {
      // Kept from the first frame on, until READY says whether the session is resumable.
      state.keeping = this.host.opts.resume;
      transport.sendArgs(this.host.requestPayload);
    }
    this.lastSentAt = this.lastReceivedAt = now();
    this.arm(this.host.opts.handshakeTimeoutMs);
    if (!this.resuming) this.host.onSessionOpen(this);
  }

  onFrame(bytes: Uint8Array, start: number, end: number): boolean {
    if (this.outcome !== null) return false;
    this.lastReceivedAt = now();

    const opcode = bytes[start];
    switch (opcode) {
      case OP_DATA:
        if (this.phase !== STREAMING) return this.violate("The server sent DATA before READY");
        this.count(end - start);
        return this.receiveData(bytes, start + 1, end);
      case OP_PING:
        return true;
      case OP_READY:
        return this.receiveReady(bytes, start + 1, end);
      case OP_RESUMED:
        return this.receiveResumed(bytes, start + 1, end);
      case OP_ACK:
        return this.receiveAck(bytes, start + 1, end);
      case OP_END:
        if (this.phase !== STREAMING) return this.violate("The server sent END before READY");
        this.count(end - start);
        this.finish(END, "completed");
        return false;
      case OP_ERROR:
        return this.receiveError(bytes, start + 1, end);
      case OP_CLOSE:
        if (this.phase === STREAMING) this.count(end - start);
        this.finish(this.readClose(bytes, start + 1, end), "closed");
        return false;
      default:
        return this.violate(
          `The server sent unknown opcode 0x${opcode.toString(16).padStart(2, "0")}`
        );
    }
  }

  onLost(
    message: string,
    details?: { closeCode?: number; closeReason?: string; cause?: unknown }
  ): void {
    this.lose("TransportLost", message, details);
  }

  onViolation(message: string): void {
    this.violate(message);
  }

  // ─── frames ────────────────────────────────────────────────────────────

  private receiveData(bytes: Uint8Array, start: number, end: number): boolean {
    let item: TResponse;
    try {
      const reader = this.host.reader;
      reader.reset(bytes, start, end);
      item = this.host.responseFormatter().read(reader);
    } catch (e) {
      // Not the transport's fault and not something a reconnect fixes: the next connection would
      // bring the same bytes. The decode error itself goes to the consumer.
      this.fault(e);
      return false;
    }
    this.queue.push(item);
    this.wake();
    return true;
  }

  private receiveReady(bytes: Uint8Array, start: number, end: number): boolean {
    if (this.phase !== HANDSHAKE || this.resuming)
      return this.violate(this.resuming ? "The server sent READY to a RESUME" : "The server sent READY twice");

    let keepAlive: number;
    let clientTimeout: number;
    let token: string | null = null;
    let windowMs = 0;
    let inputBudget = 0;
    try {
      const reader = this.host.reader;
      reader.reset(bytes, start, end);
      const size = reader.readStartArray();
      if (size === null || size < 3)
        throw new Error(
          `expected at least 3 elements, got ${size === null ? "an indefinite-length array" : size}`
        );
      reader.readTextString(); // the connection id; the server logs it, the client has no use for it
      keepAlive = Number(reader.readUInt64());
      clientTimeout = Number(reader.readUInt64());
      // Positions 4 and 5 are the resume token and window, read tolerantly: a server that adds
      // fields of its own without offering a resume puts anything else there.
      let read = 3;
      if (size >= 4) {
        read++;
        if (reader.peekState() === CborReaderState.TextString) token = reader.readTextString();
        else reader.skipValue();
        if (token === "") throw new Error("an empty resume token");
      }
      if (size >= 5) {
        read++;
        if (token !== null && reader.peekState() === CborReaderState.UnsignedInteger)
          windowMs = Number(reader.readUInt64());
        else reader.skipValue();
      }
      if (size >= 6) {
        read++;
        if (token !== null && reader.peekState() === CborReaderState.UnsignedInteger)
          inputBudget = Number(reader.readUInt64());
        else reader.skipValue();
      }
      // Elements a later protocol revision appends.
      reader.readEndArrayAndSkip(size - read);
    } catch (e) {
      return this.violate(`The server sent a malformed READY: ${messageOf(e)}`, e);
    }

    const state = this.host.resume;
    state.keepAliveMs = keepAlive;
    state.clientTimeoutMs = clientTimeout;
    if (token !== null && this.host.opts.resume) {
      state.token = token;
      state.windowMs = windowMs;
      state.adoptServerBudget(inputBudget);
    } else {
      state.disable();
    }

    return this.accepted(keepAlive, clientTimeout);
  }

  /**
   * `[m]`: the server has our frames up to `m`. What it does not have goes out again, before
   * anything new; then the connection carries on where the last one left off.
   */
  private receiveResumed(bytes: Uint8Array, start: number, end: number): boolean {
    if (this.phase !== HANDSHAKE || !this.resuming)
      return this.violate("The server sent an unexpected RESUMED");

    const state = this.host.resume;
    let serverReceived: number;
    try {
      const reader = this.host.reader;
      reader.reset(bytes, start, end);
      const size = reader.readStartArray();
      if (size === null || size < 1) throw new Error("no count");
      serverReceived = Number(reader.readUInt64());
      reader.readEndArrayAndSkip(size - 1);
    } catch (e) {
      return this.violate(`The server sent a malformed RESUMED: ${messageOf(e)}`, e);
    }

    if (serverReceived < state.peerAcked || serverReceived > state.sent)
      return this.violate(
        `RESUMED claims ${serverReceived} frames received; the client sent ${state.sent}`
      );

    state.acknowledge(serverReceived);
    const transport = this.transport!;
    for (const frame of state.unacknowledged()) transport.sendFrame(frame);
    this.lastSentAt = now();

    if (!this.accepted(state.keepAliveMs, state.clientTimeoutMs)) return false;
    this.host.onSessionOpen(this);
    return this.outcome === null;
  }

  /** `uint n`: the server has our reliable frames up to `n`. */
  private receiveAck(bytes: Uint8Array, start: number, end: number): boolean {
    const state = this.host.resume;
    if (state.token === null || this.phase !== STREAMING)
      return this.violate("The server sent ACK on a stream that is not resumable");

    let acked: number;
    try {
      const reader = this.host.reader;
      reader.reset(bytes, start, end);
      acked = Number(reader.readUInt64());
    } catch (e) {
      return this.violate(`The server sent a malformed ACK: ${messageOf(e)}`, e);
    }

    if (acked < state.peerAcked || acked > state.sent)
      return this.violate(`The server acknowledged ${acked} frames; the client sent ${state.sent}`);

    state.acknowledge(acked);
    return true;
  }

  /** One more of the server's reliable frames taken; acknowledged in batches. */
  private count(bytes: number): void {
    const state = this.host.resume;
    state.received++;
    state.receivedBytes += bytes;
    if (
      state.token !== null &&
      (state.received - state.ackSent >= ACK_EVERY_FRAMES ||
        state.receivedBytes - state.ackSentBytes >= ACK_EVERY_BYTES)
    )
      this.sendAck();
  }

  private sendAck(): void {
    const state = this.host.resume;
    if (state.token === null || this.released) return;
    const transport = this.transport!;
    const writer = new CborWriter(12);
    writer.writeEncodedValue(ACK_PREFIX);
    writer.writeUInt64(BigInt(state.received));
    transport.sendFrame(writer.data);
    state.ackSent = state.received;
    state.ackSentBytes = state.receivedBytes;
    this.lastSentAt = now();
  }

  /** READY or RESUMED: the server has the connection; items flow. */
  private accepted(keepAlive: number, clientTimeout: number): boolean {
    this.ready = true;
    this.phase = STREAMING;

    const opts = this.host.opts;
    let interval = opts.keepAliveIntervalMs > 0 ? opts.keepAliveIntervalMs : Infinity;
    if (clientTimeout > 0) interval = Math.min(interval, clientTimeout / 2);
    this.keepAliveMs = interval === Infinity ? 0 : interval;
    // A server that does not ping says nothing on an idle stream; its silence proves nothing.
    this.serverTimeoutMs =
      keepAlive > 0 && opts.serverTimeoutMs > 0 ? Math.max(opts.serverTimeoutMs, 2 * keepAlive) : 0;

    this.heartbeat(this.lastReceivedAt);
    this.host.onReady(this);
    return this.outcome === null;
  }

  private receiveError(bytes: Uint8Array, start: number, end: number): boolean {
    let error: IonProtocolError;
    try {
      const reader = this.host.reader;
      reader.reset(bytes, start, end);
      error = IonFormatterStorage.get<IonProtocolError>("IonProtocolError").read(reader);
    } catch (e) {
      return this.violate(`The server sent a malformed ERROR: ${messageOf(e)}`, e);
    }
    if (this.resuming && this.phase === HANDSHAKE && error.code === NOT_RESUMABLE_CODE) {
      this.finish(NOT_RESUMABLE, "not resumable");
      return false;
    }
    if (this.phase === STREAMING) this.count(end - start + 1);
    this.finish({ kind: "error", error }, "error");
    return false;
  }

  /**
   * `[reason?, allowReconnect]`, tolerantly: an empty payload is a close with no reason, and a
   * payload that cannot be read is still a goodbye — just not an invitation to come back.
   */
  private readClose(bytes: Uint8Array, start: number, end: number): SessionOutcome {
    let reason: string | null = null;
    let allowReconnect = false;
    if (end > start) {
      try {
        const reader = this.host.reader;
        reader.reset(bytes, start, end);
        const size = reader.readStartArray() ?? 0;
        if (size > 0) {
          if (reader.peekState() === CborReaderState.Null) reader.readNull();
          else reason = reader.readTextString();
        }
        if (size > 1) allowReconnect = reader.readBoolean();
      } catch {
        reason = null;
        allowReconnect = false;
      }
    }
    return { kind: "closed", reason, allowReconnect };
  }

  // ─── endings ───────────────────────────────────────────────────────────

  private settle(outcome: SessionOutcome): void {
    this.outcome = outcome;
    this.phase = DONE;
    if (this.timer !== undefined) {
      clearTimeout(this.timer);
      this.timer = undefined;
    }
    // An input waiting for replay room re-evaluates on the next connection.
    this.host.resume.wake();
    this.wake();
  }

  /**
   * The server said goodbye (END, ERROR, CLOSE); end our side the same way, without another. A
   * resumable server holds its goodbye until it is acknowledged, so the ACK goes first.
   */
  private finish(outcome: SessionOutcome, reason: string): void {
    if (outcome.kind !== "notResumable" && this.phase === STREAMING) this.sendAck();
    this.settle(outcome);
    this.release(reason);
  }

  /** No goodbye: the transport is dead or untrustworthy, so it is killed rather than closed. */
  private lose(
    reason: IonStreamDisconnectReason,
    message: string,
    details?: { closeCode?: number; closeReason?: string; cause?: unknown }
  ): false {
    if (this.outcome !== null) return false;
    const stage: Stage =
      this.phase === OPENING ? "connect" : this.phase === HANDSHAKE ? "handshake" : "stream";
    this.settle({
      kind: "failed",
      error: new IonStreamDisconnectedError(reason, message, details),
      stage,
    });
    this.release(null);
    return false;
  }

  private violate(message: string, cause?: unknown): false {
    return this.lose("ProtocolViolation", message, cause === undefined ? undefined : { cause });
  }

  /** This client failed; the server did nothing wrong and gets a goodbye. */
  private fault(error: unknown): void {
    if (this.outcome !== null) return;
    this.settle({ kind: "fault", error });
    this.goodbye("client error");
  }

  private goodbye(reason: string): void {
    if (this.released) return;
    if (this.opened) {
      const transport = this.transport!;
      this.host.resume.sentReliable(null);
      transport.send(transport.leaveFrame);
      this.release(reason);
    } else {
      this.release(null);
    }
  }

  /** Closes the transport gracefully with `reason`, or aborts it when `reason` is null. */
  private release(reason: string | null): void {
    if (this.released) return;
    this.released = true;
    const transport = this.transport;
    if (transport === null) return;
    if (reason === null) transport.abort();
    else transport.close(reason);
  }

  private sendInputError(error: unknown): void {
    const transport = this.transport!;
    let message = messageOf(error);
    if (message.length > MAX_ERROR_MESSAGE) message = `${message.slice(0, MAX_ERROR_MESSAGE)}...`;

    const writer = new CborWriter(32 + message.length * 3);
    writer.writeEncodedValue(transport.errorPrefix);
    writer.writeStartArray(2);
    writer.writeTextString("INPUT_FAULTED");
    writer.writeTextString(message);
    writer.writeEndArray();
    const frame = writer.data;
    this.host.resume.sentReliable(frame.subarray(transport.headroom));
    transport.sendBuilt(frame);
    this.lastSentAt = now();
  }

  // ─── timer ─────────────────────────────────────────────────────────────

  /** Arms the phase deadline; `0` (or less) disables it. */
  private arm(ms: number): void {
    if (this.timer !== undefined) clearTimeout(this.timer);
    this.timer = ms > 0 && ms < Infinity ? setTimeout(this.onTimer, ms) : undefined;
  }

  private readonly onTimer = (): void => {
    this.timer = undefined;
    if (this.outcome !== null) return;

    const opts = this.host.opts;
    switch (this.phase) {
      case OPENING:
        this.lose(
          "Timeout",
          this.kind === "webtransport"
            ? `The WebTransport session did not open within ${opts.webTransportConnectTimeoutMs} ms`
            : `The WebSocket did not open within ${opts.handshakeTimeoutMs} ms`
        );
        return;
      case HANDSHAKE:
        this.lose(
          "Timeout",
          `The server did not send ${this.resuming ? "RESUMED" : "READY"} within ${opts.handshakeTimeoutMs} ms`
        );
        return;
      case STREAMING:
        this.heartbeat(now());
        return;
    }
  };

  /** Times out a silent server, pings when we have been silent, and sleeps until the next of the two deadlines. */
  private heartbeat(at: number): void {
    if (this.timer !== undefined) {
      clearTimeout(this.timer);
      this.timer = undefined;
    }

    let next = Infinity;
    if (this.serverTimeoutMs > 0) {
      const due = this.lastReceivedAt + this.serverTimeoutMs;
      if (at >= due) {
        this.lose("Timeout", `The server sent nothing for ${this.serverTimeoutMs} ms`);
        return;
      }
      next = due;
    }

    // Whatever the server sent since the last ACK is acknowledged on this timer at the latest.
    const state = this.host.resume;
    if (state.token !== null && state.received > state.ackSent) this.sendAck();

    if (this.keepAliveMs > 0) {
      let due = this.lastSentAt + this.keepAliveMs;
      if (at >= due) {
        const transport = this.transport!;
        transport.send(transport.pingFrame);
        this.lastSentAt = at;
        due = at + this.keepAliveMs;
      }
      if (due < next) next = due;
    }

    if (state.token !== null) next = Math.min(next, at + ACK_TIMER_MS);

    if (next !== Infinity) this.timer = setTimeout(this.onTimer, Math.max(1, Math.ceil(next - at)));
  }
}

/**
 * Why the sub-protocol the server selected is not one this client can talk, or `null` if it is.
 * An empty selection is let through: READY then decides, and a server that sends anything else
 * first is a protocol violation.
 */
function negotiationProblem(protocol: string): string | null {
  if (protocol === "") return null;
  if (!protocol.startsWith("ion"))
    return "The server selected a sub-protocol that is not an Ion stream protocol";
  const marker = protocol.lastIndexOf("ver#");
  const version = marker < 0 ? 1 : Number.parseInt(protocol.slice(marker + 4), 10) || 1;
  return version === ION_STREAM_VERSION
    ? null
    : `The server negotiated Ion stream protocol v${version}; this client speaks v${ION_STREAM_VERSION} only`;
}

const ACK_PREFIX = Uint8Array.of(OP_ACK);

/** How often, at the latest, a resumable session acknowledges what it received. */
const ACK_TIMER_MS = 1000;

/** `[0x07] [resumeToken, received]`. */
function resumeFrame(token: string, received: number): Uint8Array {
  const writer = new CborWriter(token.length + 16);
  writer.writeEncodedValue(Uint8Array.of(OP_RESUME));
  writer.writeStartArray(2);
  writer.writeTextString(token);
  writer.writeUInt64(BigInt(received));
  writer.writeEndArray();
  return writer.data;
}

function messageOf(e: unknown): string {
  if (e instanceof Error) return e.message;
  return String(e);
}
