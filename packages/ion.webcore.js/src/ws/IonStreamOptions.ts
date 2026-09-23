/**
 * Client-side settings for `stream` calls, set through `IonClientContext.streamOptions`.
 *
 * Every field is optional. The timing defaults mirror the server's (`IonStreamOptions` in
 * `ion.runtime.network`: 15 s keep-alive, 30 s client timeout, 15 s handshake), which in turn
 * mirror SignalR's, so both ends detect a dead peer in the same time. Any timeout set to `0`
 * disables that check.
 */
export interface IonStreamOptions {
  /**
   * Transports to try, in order. Default `["webtransport", "websocket"]`.
   *
   * A transport this environment cannot provide is skipped silently: WebTransport needs an
   * implementation (a {@link webTransportFactory} or `globalThis.WebTransport`) **and** an
   * `https:` base URL. The next transport is tried only when the previous one failed *before*
   * the connection was established; a connection that dropped later is reconnected, starting
   * again from the top of this list.
   */
  transports?: IonStreamTransportKind[];

  /**
   * How a stream that dropped comes back. Default enabled with the defaults of
   * {@link IonReconnectOptions}; `false` turns reconnecting off, and every disconnect is then
   * thrown to the consumer.
   *
   * Only failures that retrying can fix are retried: a lost or silent transport, a server CLOSE
   * that allows it, a ticket exchange that failed on the network or with 5xx/408/429. A normal
   * END, a server ERROR, a CLOSE that does not allow it, a refused (4xx) ticket exchange, a
   * payload this client cannot decode and a consumer that stopped reading are final.
   */
  reconnect?: false | IonReconnectOptions;

  /**
   * Ask the server for a resumable session. Default `true`; needs {@link reconnect}.
   *
   * A dropped connection is then picked up exactly where it left off: the server keeps the session
   * for its resume window, both sides resend what the other did not acknowledge, and the consumer
   * sees nothing but the pause — no item lost, none twice, the input stream included. Only when the
   * session is gone (the server restarted, the window ran out) is the call started afresh, which
   * runs the stream method again from the start; the `reconnected` event says which happened.
   */
  resume?: boolean;

  /**
   * How many bytes of sent but unacknowledged input a resumable call keeps for replay. Default
   * 1 MiB. When they are spent the input stream waits for the server's acknowledgements.
   */
  resumeBufferSize?: number;

  /**
   * How long the client may send nothing before it sends a PING. Default 15000 ms.
   *
   * Lowered to half the server's announced client timeout when that is shorter, so a slow
   * setting here cannot get the client dropped. `0` sends a PING only as often as the server
   * requires, and never when the server announces no client timeout.
   */
  keepAliveIntervalMs?: number;

  /**
   * How long the server may send nothing before the connection is treated as dead
   * (`IonStreamDisconnectedError` with reason `"Timeout"`). Default 30000 ms.
   *
   * Raised to twice the server's announced keep-alive when that is longer. A server that
   * announces no keep-alive never pings an idle stream, so no timeout applies to it at all —
   * silence there is not evidence of anything.
   */
  serverTimeoutMs?: number;

  /**
   * How long opening a WebSocket, and receiving READY once a transport is open, may take.
   * Default 15000 ms.
   */
  handshakeTimeoutMs?: number;

  /**
   * How long a WebTransport session may take to open (`ready` plus the stream). Default 3000 ms —
   * much shorter than {@link handshakeTimeoutMs}, because a network that blocks UDP makes
   * WebTransport hang rather than fail, and every millisecond here is spent before falling back.
   */
  webTransportConnectTimeoutMs?: number;

  /**
   * How long a WebTransport failure to connect is remembered for the origin, during which
   * WebTransport is skipped and calls go straight to the next transport. Default 300000 ms.
   * See `IonWsClient.resetTransportHealth()`.
   */
  webTransportRetryAfterMs?: number;

  /**
   * Creates the WebSocket. Default `new WebSocket(url, protocols)`. For Node without a global
   * WebSocket, for proxies, and for tests.
   */
  webSocketFactory?: (url: string, protocols: string[]) => WebSocketLike;

  /** Creates the WebTransport session. Default `new WebTransport(url)`. */
  webTransportFactory?: (url: string) => WebTransportLike;
}

export type IonStreamTransportKind = "webtransport" | "websocket";

export interface IonReconnectOptions {
  /** Upper bound of the first delay. Default 1000 ms. */
  initialDelayMs?: number;
  /** Upper bound of any delay. Default 30000 ms. */
  maxDelayMs?: number;
  /**
   * Reconnect attempts in a row before the last failure is thrown. Default `Infinity`. The count
   * starts over once a connection is established.
   */
  maxAttempts?: number;
  /**
   * Full jitter: each delay is uniform in `[0, min(maxDelayMs, initialDelayMs * 2^(n-1)))`
   * rather than exactly the bound. Default `true` — it keeps a server restart from being met by
   * every client at the same instant.
   */
  jitter?: boolean;
}

/**
 * The part of the WHATWG `WebSocket` a stream uses. The browser's and Node's global `WebSocket`
 * satisfy it as they are.
 *
 * Events are taken through the `on*` handler properties rather than `addEventListener`: one
 * handler per event is all a stream needs, and setting them back to `null` is how the client
 * lets go of a socket it no longer wants to hear from.
 */
export interface WebSocketLike {
  /** Set to `"arraybuffer"` by the client. */
  binaryType: BinaryType;
  /** The sub-protocol the server selected; empty when it selected none. */
  readonly protocol: string;
  /** `1` (OPEN) is the only state the client sends in. */
  readonly readyState: number;
  /** Optional: when present, the input stream waits for it to drain instead of queueing without bound. */
  readonly bufferedAmount?: number;
  onopen: ((ev: Event) => void) | null;
  onmessage: ((ev: MessageEvent) => void) | null;
  onclose: ((ev: CloseEvent) => void) | null;
  onerror: ((ev: Event) => void) | null;
  send(data: Uint8Array<ArrayBuffer>): void;
  close(code?: number, reason?: string): void;
}

/** One bidirectional WebTransport stream: a byte stream each way. */
export interface WebTransportStreamLike {
  readonly readable: ReadableStream<Uint8Array>;
  readonly writable: WritableStream<Uint8Array>;
}

/** The part of the W3C `WebTransport` a stream uses; the browser's `WebTransport` satisfies it. */
export interface WebTransportLike {
  readonly ready: Promise<unknown>;
  readonly closed: Promise<unknown>;
  createBidirectionalStream(): Promise<WebTransportStreamLike>;
  close(closeInfo?: { closeCode?: number; reason?: string }): void;
}
