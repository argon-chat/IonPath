import { IonRequestException } from "../unary/IonUnaryRequest";

/**
 * Why a stream ended without the server saying so. The names match `ion.runtime.IonDisconnectReason`
 * in C#, so a log line reads the same on both ends.
 */
export type IonStreamDisconnectReason = "TransportLost" | "Timeout" | "ProtocolViolation";

/**
 * The server closed the stream on purpose with a CLOSE frame — a kick, a revoked session, a
 * restart. {@link allowReconnect} says whether coming straight back is welcome; the client
 * reconnects on its own when it is and reconnecting is enabled, so a consumer only sees this
 * error when the server said no, or when reconnecting is off.
 *
 * Mirrors `IonStreamClosedException` in C#.
 */
export class IonStreamClosedError extends IonRequestException {
  constructor(
    /** The reason the server gave, or `null` when it gave none. */
    readonly reason: string | null,
    /** True when the server expects the client to reconnect (a restart, a rebalance). */
    readonly allowReconnect: boolean
  ) {
    super({ code: "STREAM_CLOSED", message: reason ?? "The server closed the stream." });
    this.name = "IonStreamClosedError";
  }
}

/**
 * The stream ended without an Ion goodbye: the transport died or could not be opened, the server
 * went silent past the timeout, or it broke the protocol.
 *
 * Mirrors `IonStreamDisconnectedException` in C#. The error codes are the same three:
 * `STREAM_DISCONNECTED`, `STREAM_TIMEOUT` and `PROTOCOL_VIOLATION`.
 */
export class IonStreamDisconnectedError extends IonRequestException {
  /** The WebSocket close code, when the server closed the socket without an Ion goodbye. */
  readonly closeCode?: number;

  /** The WebSocket close reason sent alongside {@link closeCode}. */
  readonly closeReason?: string;

  constructor(
    readonly reason: IonStreamDisconnectReason,
    message: string,
    details?: { closeCode?: number; closeReason?: string; cause?: unknown }
  ) {
    super(
      { code: codeFor(reason), message },
      undefined,
      details?.cause === undefined ? undefined : { cause: details.cause }
    );
    this.name = "IonStreamDisconnectedError";
    if (details?.closeCode !== undefined) this.closeCode = details.closeCode;
    if (details?.closeReason) this.closeReason = details.closeReason;
  }

  /**
   * Whether trying again can help. A protocol violation cannot: the server that sent it will
   * send it again, and retrying would turn one bug into a reconnect loop.
   */
  get retryable(): boolean {
    return this.reason !== "ProtocolViolation";
  }
}

function codeFor(reason: IonStreamDisconnectReason): string {
  switch (reason) {
    case "Timeout":
      return "STREAM_TIMEOUT";
    case "ProtocolViolation":
      return "PROTOCOL_VIOLATION";
    default:
      return "STREAM_DISCONNECTED";
  }
}
