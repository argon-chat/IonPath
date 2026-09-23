import { CborReader } from "../cbor";
import { IonFormatterStorage } from "../logic/IonFormatter";
import { safeFetchBuffer } from "../yetAnotherFetch";
import {
  type IonCallContext,
  type IonClientContext,
  IonContentType,
  IonProtocolError,
  IonRequestException,
} from "../unary/IonUnaryRequest";
import {
  addStreamListener,
  type ExchangeProbe,
  IonStreamCall,
  type ReconnectEvents,
  removeStreamListener,
  resetTransportHealth,
} from "./IonStreamCall";

export type { ReconnectEvents };

/**
 * The client of one `stream` method. Generated code creates one per call.
 *
 * A call connects over WebTransport or WebSocket — in the order `IonClientContext.streamOptions`
 * gives, WebTransport first by default — speaks Ion stream protocol v2 on it, and reconnects when
 * the connection drops without a goodbye. See {@link IonStreamOptions} for the knobs and
 * {@link IonStreamCall} for how the pieces fit together.
 *
 * How a call ends, as seen by the consumer of the returned generator:
 *
 * - the server's END: the generator returns;
 * - the server's ERROR: `IonRequestException` carrying it;
 * - the server's CLOSE: `IonStreamClosedError` — or a reconnect, when the server allowed one;
 * - a lost, silent or misbehaving transport: `IonStreamDisconnectedError` once reconnecting gave
 *   up (or at once when it is off, or for a protocol violation);
 * - an aborted `signal`: a `DOMException` named `AbortError`.
 *
 * A consumer that stops early (`break`, `return()`) or aborts makes the client leave politely: a
 * CLOSE frame, then a graceful end of the transport.
 */
export class IonWsClient {
  constructor(
    private context: IonClientContext,
    private interfaceName: string,
    private methodName: string
  ) {}

  /**
   * Subscribes to reconnect progress of every stream call.
   *
   * - `reconnecting(attempt, delay)`: a connection failed and attempt `attempt` starts in `delay` ms;
   * - `reconnected(attempt)`: that attempt was accepted by the server (it sent READY);
   * - `closed()`: a call was ended by its consumer — an early `break`/`return()`, or an abort.
   */
  public static on<K extends keyof ReconnectEvents>(event: K, cb: ReconnectEvents[K]): void {
    addStreamListener(event, cb);
  }

  /** Removes a listener added with {@link on}. */
  public static off<K extends keyof ReconnectEvents>(event: K, cb: ReconnectEvents[K]): void {
    removeStreamListener(event, cb);
  }

  /**
   * Forgets every remembered WebTransport failure, so the next call tries WebTransport again even
   * inside `webTransportRetryAfterMs`. For tests, and for an app that knows the network changed.
   */
  public static resetTransportHealth(): void {
    resetTransportHealth();
  }

  private async terminalExchangeAsync(
    c: IonCallContext,
    signal: AbortSignal | undefined,
    probe: ExchangeProbe
  ): Promise<void> {
    // Inject correlation ID header if set by interceptor or user code
    if (c.correlationId) {
      (c.requestHeaders as Record<string, string>)["X-Ion-Correlation-Id"] = c.correlationId;
    }

    const resp = await safeFetchBuffer(`${this.context.baseUrl}/ion.att`, {
      body: c.requestPayload.buffer as any,
      headers: c.requestHeaders,
      signal: signal,
      method: "POST",
      credentials: "include",
    });

    if (resp.status === "network-error") probe.networkError = true;

    if (!resp.buffer)
      throw new IonRequestException(
        IonProtocolError.UPSTREAM_ERROR(`no buffer return, status: ${resp.status}`),
        typeof resp.status === "number" ? resp.status : undefined
      );

    const buf = new Uint8Array(await resp.buffer);
    c.responsePayload = buf;

    if (resp.status != 200) {
      try {
        const error = IonFormatterStorage.get<IonProtocolError>("IonProtocolError").read(
          new CborReader(buf)
        );
        throw new IonRequestException(error, resp.status as number);
      } catch (e) {
        if (e instanceof IonRequestException) {
          throw e;
        }
        throw new IonRequestException(
          IonProtocolError.UPSTREAM_ERROR(resp.status.toString()),
          resp.status as number
        );
      }
    }
  }

  /**
   * Exchanges the context's credentials for a stream ticket (`POST {baseUrl}/ion.att`, through
   * the context's interceptors) and returns it base56-encoded. Stream calls do this before every
   * connection attempt; tickets may be single-use.
   */
  async createExchangeToken(signal?: AbortSignal): Promise<string> {
    return this.exchangeTicket(signal, { networkError: false });
  }

  private async exchangeTicket(
    signal: AbortSignal | undefined,
    probe: ExchangeProbe
  ): Promise<string> {
    const ctx: IonCallContext = {
      client: fetch,
      interfaceName: this.interfaceName,
      methodName: this.methodName,
      requestPayload: new Uint8Array(),
      expectedType: undefined,
      requestHeaders: {
        "Content-Type": IonContentType,
        "X-Ion-Session-Id": this.context.sessionId,
      },
    };

    let next: (c: IonCallContext, s?: AbortSignal) => Promise<void> = (c, s) =>
      this.terminalExchangeAsync(c, s, probe);
    for (let i = this.context.interceptors.length - 1; i >= 0; i--) {
      const interceptor = this.context.interceptors[i];
      const currentNext = next;
      next = (c, s) => interceptor.invokeAsync(c, currentNext, s);
    }

    await next(ctx, signal);

    if (!ctx.responsePayload) {
      throw new Error("No response payload");
    }

    try {
      const reader = new CborReader(ctx.responsePayload);

      reader.readStartArray();
      const token = reader.readByteString();
      reader.readEndArray();

      return this.toBase56(token);
    } catch (e) {
      console.error("===== UNCAUGHT ION INTERNAL ERROR =====");
      console.error(e);
      console.error(`Procedure: ${ctx.interfaceName}/${ctx.methodName}()`);
      console.error("===== ========================== =====");
      throw e;
    }
  }

  toBase56(uint8Array: Uint8Array<ArrayBufferLike>) {
    const alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";
    const base = alphabet.length;

    let value = 0n;
    for (let i = 0; i < uint8Array.length; i++) {
      value = (value << 8n) + BigInt(uint8Array[i]);
    }
    let leadingZeroes = 0;
    for (let i = 0; i < uint8Array.length && uint8Array[i] === 0; i++) {
      leadingZeroes++;
    }
    let result = "";
    while (value > 0n) {
      const rem = Number(value % BigInt(base));
      value /= BigInt(base);
      result = alphabet[rem] + result;
    }
    return alphabet[0].repeat(leadingZeroes) + result;
  }

  /** Calls a server-streaming method; each item of the returned generator is one DATA frame. */
  callServerStreaming<TResponse>(
    responseTypename: string,
    requestPayload: Uint8Array,
    signal?: AbortSignal,
    correlationId?: string
  ): AsyncGenerator<TResponse, void, unknown> {
    return new IonStreamCall<TResponse, never>({
      context: this.context,
      interfaceName: this.interfaceName,
      methodName: this.methodName,
      exchangeTicket: (s, p) => this.exchangeTicket(s, p),
      responseTypename,
      requestPayload,
      input: null,
      signal,
      correlationId,
    }).run();
  }

  /**
   * Calls a method that streams in both directions. `inputStream` is pumped to the server as soon
   * as the connection is open, one DATA frame per item and END once it completes; if it throws,
   * the server gets an `INPUT_FAULTED` ERROR frame and the returned generator throws the same
   * error. Across a reconnect the same iterator continues — see {@link IonStreamCall} for what
   * that means for items in flight.
   */
  callServerStreamingFullDuplex<TResponse, TRequest>(
    responseTypename: string,
    requestPayload: Uint8Array,
    inputStream: AsyncIterable<TRequest>,
    inputStreamTypeName: string,
    signal?: AbortSignal,
    correlationId?: string
  ): AsyncGenerator<TResponse, void, unknown> {
    return new IonStreamCall<TResponse, TRequest>({
      context: this.context,
      interfaceName: this.interfaceName,
      methodName: this.methodName,
      exchangeTicket: (s, p) => this.exchangeTicket(s, p),
      responseTypename,
      requestPayload,
      input: { stream: inputStream, typename: inputStreamTypeName },
      signal,
      correlationId,
    }).run();
  }
}
