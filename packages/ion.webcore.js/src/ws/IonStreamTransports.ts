import type { IonStreamTransportKind, WebSocketLike, WebTransportLike } from "./IonStreamOptions";
import {
  FrameAssembler,
  OP_CLOSE,
  OP_DATA,
  OP_END,
  OP_ERROR,
  OP_PING,
  VARINT_HEADROOM,
  type FrameSink,
  withVarintPrefix,
  writeVarint,
  varintSize,
} from "./IonStreamWire";

/** What a transport reports to the stream session that owns it. */
export interface TransportSink extends FrameSink {
  /** The transport can carry frames. Called at most once. */
  onOpen(): void;
  /** The transport failed, or ended without the session asking it to. */
  onLost(message: string, details?: { closeCode?: number; closeReason?: string; cause?: unknown }): void;
}

/**
 * Whole frames in, whole frames out, a graceful end of our side and a kill switch — the same
 * shape as `IonStreamTransport` on the server.
 *
 * Once the session calls {@link close} or {@link abort} the transport stops reporting anything:
 * the session has already decided how the connection ended, and a late close event must not
 * decide it again.
 */
export interface StreamTransport {
  readonly kind: IonStreamTransportKind;
  /** The sub-protocol the server selected; always empty for WebTransport. */
  readonly protocol: string;

  /** Spare bytes a built frame carries in front of its opcode (see {@link sendBuilt}). */
  readonly headroom: number;
  /** `headroom` bytes, then the opcode, for the frames that are encoded per send. */
  readonly dataPrefix: Uint8Array;
  readonly errorPrefix: Uint8Array;
  /** Complete, ready-framed constant frames. Never written to. */
  readonly pingFrame: Uint8Array;
  readonly endOfInputFrame: Uint8Array;
  /** CLOSE with no reason: `[0x04] array(1)[null]`. */
  readonly leaveFrame: Uint8Array;

  /** The argument message: the raw CBOR arguments, no opcode. */
  sendArgs(payload: Uint8Array): void;
  /** A complete frame as it goes on the wire — one of the constant frames above. */
  send(frame: Uint8Array): void;
  /**
   * One frame, `[opcode][payload]`, framed by the transport as it goes out — for frames kept apart
   * from any one transport: an ACK, and a resumed session's replay, which may go out on another
   * kind of transport than the one it was first sent on.
   */
  sendFrame(frame: Uint8Array): void;
  /** `headroom` spare bytes followed by one frame; the transport frames it in place. */
  sendBuilt(buffer: Uint8Array): void;
  /** Settles when the transport can take more; `undefined` when it can already. */
  backpressure(): Promise<unknown> | undefined;
  /** Ends our side gracefully: a WebSocket close handshake, or a FIN and then the session close. */
  close(reason: string): void;
  /** Kills the transport. */
  abort(): void;
}

const noop = (): void => {};

// ─── WebSocket ──────────────────────────────────────────────────────────────

const WS_OPEN = 1;

/** Above this many unsent bytes the input stream stops producing until the socket drains. */
const WS_HIGH_WATER = 1 << 20;
const WS_LOW_WATER = 1 << 18;
const WS_DRAIN_POLL_MS = 16;

const WS_DATA_PREFIX = Uint8Array.of(OP_DATA);
const WS_ERROR_PREFIX = Uint8Array.of(OP_ERROR);
const WS_PING = Uint8Array.of(OP_PING);
const WS_END_OF_INPUT = Uint8Array.of(OP_END);
const WS_LEAVE = Uint8Array.of(OP_CLOSE, 0x81, 0xf6);

/**
 * One WebSocket message is one frame; the WebSocket does the framing.
 *
 * `WebSocket.send` copies what it is given before returning, which is what lets the constant
 * frames be shared by every socket, and a built frame be sent straight out of its encoder's buffer.
 */
export class WebSocketTransport implements StreamTransport {
  readonly kind = "websocket";
  readonly headroom = 0;
  readonly dataPrefix = WS_DATA_PREFIX;
  readonly errorPrefix = WS_ERROR_PREFIX;
  readonly pingFrame = WS_PING;
  readonly endOfInputFrame = WS_END_OF_INPUT;
  readonly leaveFrame = WS_LEAVE;

  private ws: WebSocketLike | null;
  private opened = false;

  constructor(
    private readonly sink: TransportSink,
    factory: (url: string, protocols: string[]) => WebSocketLike,
    url: string,
    protocols: string[]
  ) {
    const ws = factory(url, protocols);
    this.ws = ws;
    ws.binaryType = "arraybuffer";
    ws.onopen = this.handleOpen;
    ws.onmessage = this.handleMessage;
    ws.onclose = this.handleClose;
    ws.onerror = this.handleError;
  }

  get protocol(): string {
    return this.ws?.protocol ?? "";
  }

  private readonly handleOpen = (): void => {
    this.opened = true;
    this.sink.onOpen();
  };

  private readonly handleMessage = (ev: MessageEvent): void => {
    const data: unknown = ev.data;
    let frame: Uint8Array;
    if (data instanceof ArrayBuffer) {
      frame = new Uint8Array(data);
    } else if (ArrayBuffer.isView(data)) {
      frame = new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
    } else {
      this.sink.onViolation(
        typeof data === "string"
          ? "The server sent a text message; Ion frames are binary"
          : "The server sent a message that is not an ArrayBuffer"
      );
      return;
    }

    if (frame.length === 0) {
      this.sink.onViolation("The server sent an empty frame");
      return;
    }
    this.sink.onFrame(frame, 0, frame.length);
  };

  private readonly handleClose = (ev: CloseEvent): void => {
    this.detach();
    const closeReason = ev.reason || undefined;
    this.sink.onLost(
      this.opened
        ? `The WebSocket closed without an Ion goodbye (code ${ev.code}${closeReason ? `, '${closeReason}'` : ""})`
        : `The WebSocket could not be opened (code ${ev.code})`,
      { closeCode: ev.code, closeReason }
    );
  };

  /**
   * `error` is always followed by `close`, but the close that follows an error carries nothing
   * more than 1006, so there is nothing to wait for.
   */
  private readonly handleError = (): void => {
    this.detach();
    this.sink.onLost(
      this.opened ? "The WebSocket failed" : "The WebSocket could not be opened"
    );
  };

  sendArgs(payload: Uint8Array): void {
    this.send(payload);
  }

  send(frame: Uint8Array): void {
    const ws = this.ws;
    if (ws !== null && ws.readyState === WS_OPEN) ws.send(frame as Uint8Array<ArrayBuffer>);
  }

  sendBuilt(buffer: Uint8Array): void {
    this.send(buffer);
  }

  sendFrame(frame: Uint8Array): void {
    this.send(frame);
  }

  backpressure(): Promise<unknown> | undefined {
    const ws = this.ws;
    if (ws === null || !((ws.bufferedAmount ?? 0) > WS_HIGH_WATER)) return undefined;

    // The WebSocket API has no drain event; polling a queue that is this full costs nothing next
    // to what is waiting in it.
    return new Promise<void>((resolve) => {
      const poll = () => {
        const current = this.ws;
        if (current === null || (current.bufferedAmount ?? 0) <= WS_LOW_WATER) resolve();
        else setTimeout(poll, WS_DRAIN_POLL_MS);
      };
      setTimeout(poll, WS_DRAIN_POLL_MS);
    });
  }

  close(reason: string): void {
    const ws = this.detach();
    if (ws === null) return;
    try {
      ws.close(1000, reason);
    } catch {
      // Already closing; nothing left to end.
    }
  }

  /**
   * A browser WebSocket has no abort: `close()` with no code is the closest thing, and it no
   * longer matters when — or whether — the handshake completes, since nothing listens any more.
   */
  abort(): void {
    const ws = this.detach();
    if (ws === null) return;
    try {
      ws.close();
    } catch {
      // Already closed.
    }
  }

  private detach(): WebSocketLike | null {
    const ws = this.ws;
    if (ws !== null) {
      ws.onopen = null;
      ws.onmessage = null;
      ws.onclose = null;
      ws.onerror = null;
      this.ws = null;
    }
    return ws;
  }
}

// ─── WebTransport ───────────────────────────────────────────────────────────

/** How long a graceful close waits for our FIN to be flushed before tearing the session down. */
const WT_CLOSE_GRACE_MS = 2000;

/** `VARINT_HEADROOM` zero bytes, then `opcode`. */
function headroomPrefix(opcode: number): Uint8Array {
  const prefix = new Uint8Array(VARINT_HEADROOM + 1);
  prefix[VARINT_HEADROOM] = opcode;
  return prefix;
}

const WT_DATA_PREFIX = headroomPrefix(OP_DATA);
const WT_ERROR_PREFIX = headroomPrefix(OP_ERROR);
const WT_PING = withVarintPrefix(WS_PING);
const WT_END_OF_INPUT = withVarintPrefix(WS_END_OF_INPUT);
const WT_LEAVE = withVarintPrefix(WS_LEAVE);

/**
 * One bidirectional stream on a WebTransport session of its own; every frame is prefixed by its
 * length as a QUIC varint. The stream's FIN is the graceful end of a side, an error or reset is a
 * dead transport.
 *
 * The constant frames are shared by every session: a WebTransport send stream copies each chunk
 * it is given (W3C WebTransport, "write a chunk"), and none of them is ever written to.
 */
export class WebTransportTransport implements StreamTransport {
  readonly kind = "webtransport";
  readonly protocol = "";
  readonly headroom = VARINT_HEADROOM;
  readonly dataPrefix = WT_DATA_PREFIX;
  readonly errorPrefix = WT_ERROR_PREFIX;
  readonly pingFrame = WT_PING;
  readonly endOfInputFrame = WT_END_OF_INPUT;
  readonly leaveFrame = WT_LEAVE;

  private readonly wt: WebTransportLike;
  private writer: WritableStreamDefaultWriter<Uint8Array> | null = null;
  private readonly assembler = new FrameAssembler();
  /** Set once the session decided the transport's fate; nothing is reported after it. */
  private done = false;
  private sessionClosed = false;

  constructor(
    private readonly sink: TransportSink,
    factory: (url: string) => WebTransportLike,
    url: string
  ) {
    const wt = factory(url);
    this.wt = wt;
    // Both reject when the session fails; the stream reader reports that, but a rejection nobody
    // observes is still an unhandled rejection.
    wt.ready.then(noop, noop);
    wt.closed.then(noop, noop);
    void this.open();
  }

  private async open(): Promise<void> {
    let reader: ReadableStreamDefaultReader<Uint8Array>;
    try {
      await this.wt.ready;
      if (this.done) return;
      const stream = await this.wt.createBidirectionalStream();
      if (this.done) return;
      this.writer = stream.writable.getWriter();
      reader = stream.readable.getReader();
    } catch (e) {
      if (!this.done) this.sink.onLost("The WebTransport session could not be opened", { cause: e });
      return;
    }

    this.sink.onOpen();
    if (!this.done) void this.receive(reader);
  }

  private async receive(reader: ReadableStreamDefaultReader<Uint8Array>): Promise<void> {
    try {
      for (;;) {
        const { value, done } = await reader.read();
        if (this.done) return;

        if (done) {
          if (this.assembler.pending > 0)
            this.sink.onViolation(
              `The WebTransport stream ended inside a frame (${this.assembler.pending} bytes left over)`
            );
          else this.sink.onLost("The WebTransport stream ended without an Ion goodbye");
          return;
        }

        if (!this.assembler.push(value, this.sink)) return;
      }
    } catch (e) {
      if (!this.done) this.sink.onLost("The WebTransport stream failed", { cause: e });
    }
  }

  private readonly onWriteFailed = (e: unknown): void => {
    if (!this.done) this.sink.onLost("A write to the WebTransport stream failed", { cause: e });
  };

  sendArgs(payload: Uint8Array): void {
    this.send(withVarintPrefix(payload));
  }

  send(frame: Uint8Array): void {
    const writer = this.writer;
    if (writer !== null && !this.done) writer.write(frame).catch(this.onWriteFailed);
  }

  sendBuilt(buffer: Uint8Array): void {
    const length = buffer.length - VARINT_HEADROOM;
    const at = VARINT_HEADROOM - varintSize(length);
    writeVarint(buffer, at, length);
    this.send(buffer.subarray(at));
  }

  sendFrame(frame: Uint8Array): void {
    this.send(withVarintPrefix(frame));
  }

  backpressure(): Promise<unknown> | undefined {
    const writer = this.writer;
    if (writer === null || this.done) return undefined;
    const desired = writer.desiredSize;
    return desired !== null && desired <= 0 ? writer.ready : undefined;
  }

  close(reason: string): void {
    if (this.done) return;
    this.done = true;

    const writer = this.writer;
    if (writer === null) {
      this.closeSession(reason);
      return;
    }

    // FIN first, so the server reads a clean end of our side rather than a reset; then the
    // session, which is this call's alone. The grace timer covers a FIN stuck behind flow control.
    const grace = setTimeout(() => this.closeSession(reason), WT_CLOSE_GRACE_MS);
    const finish = () => {
      clearTimeout(grace);
      this.closeSession(reason);
    };
    writer.close().then(finish, finish);
  }

  private closeSession(reason?: string): void {
    if (this.sessionClosed) return;
    this.sessionClosed = true;
    try {
      this.wt.close(reason === undefined ? undefined : { closeCode: 0, reason });
    } catch {
      // Already closed.
    }
  }

  abort(): void {
    if (this.done) return;
    this.done = true;
    this.writer?.abort().catch(noop);
    this.closeSession();
  }
}
