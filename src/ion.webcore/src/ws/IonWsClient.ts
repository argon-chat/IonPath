import { CborReader } from "../cbor";
import { IonFormatterStorage } from "../logic/IonFormatter";
import { safeFetchBuffer } from "../yetAnotherFetch";
import {
  IonCallContext,
  IonClientContext,
  IonContentType,
  IonProtocolError,
  IonRequestException,
} from "../unary/IonUnaryRequest";

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
  if (
    (u.protocol === "ws:" && u.port === "80") ||
    (u.protocol === "wss:" && u.port === "443")
  ) {
    u.port = "";
  }
  const urlStr = u.toString();

  if (urlStr.endsWith("/")) return urlStr.slice(0, -1);
  return urlStr;
}
export type ReconnectEvents = {
  reconnecting: (attempt: number, delay: number) => void;
  reconnected: (attempt: number) => void;
  closed: () => void;
};

export class IonWsClient {
  constructor(
    private context: IonClientContext,
    private interfaceName: string,
    private methodName: string
  ) {}

  private static listeners = new Map<keyof ReconnectEvents, Function[]>();
  public static on<K extends keyof ReconnectEvents>(
    event: K,
    cb: ReconnectEvents[K]
  ): void {
    if (!this.listeners.has(event)) {
      this.listeners.set(event, []);
    }
    (this.listeners.get(event) as ReconnectEvents[K][]).push(cb);
  }

  private static emit<K extends keyof ReconnectEvents>(
    event: K,
    ...args: Parameters<ReconnectEvents[K]>
  ): void {
    const arr = this.listeners.get(event);
    if (!arr) return;
    type Args = Parameters<ReconnectEvents[K]>;
    (arr as ((...a: Args) => void)[]).forEach((cb) => cb(...args));
  }

  private async terminalExchangeAsync(
    c: IonCallContext,
    signal?: AbortSignal
  ): Promise<void> {
    const resp = await safeFetchBuffer(`${this.context.baseUrl}/ion.att`, {
      body: c.requestPayload.buffer as any,
      headers: c.requestHeadets,
      signal: signal,
      method: "POST",
    });

    if (!resp.buffer)
      throw new IonRequestException(IonProtocolError.UPSTREAM_ERROR("no buffer return"));
    
    
    const buf = new Uint8Array(await resp.buffer);
    c.responsePayload = buf;

    if (resp.status != 200) {
      try {
        const error = IonFormatterStorage.get<IonProtocolError>(
          "IonProtocolError"
        ).read(new CborReader(buf));
        throw new IonRequestException(error);
      } catch (e) {
        if (e instanceof IonRequestException) {
          throw e;
        }
        throw new IonRequestException(
          IonProtocolError.UPSTREAM_ERROR(resp.status.toString())
        );
      }
    }
  }

  async createExchangeToken(signal?: AbortSignal): Promise<string> {
    const ctx: IonCallContext = {
      client: fetch,
      interfaceName: this.interfaceName,
      methodName: this.methodName,
      requestPayload: new Uint8Array(),
      expectedType: undefined,
      requestHeadets: { "Content-Type": IonContentType },
    };

    let next: (c: IonCallContext, s?: AbortSignal) => Promise<void> =
      this.terminalExchangeAsync.bind(this);
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
      console.error("===== UNCATCH ION INTERNAL ERROR =====");
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

  async *callServerStreaming<TResponse>(
    responseTypename: string,
    requestPayload: Uint8Array,
    signal?: AbortSignal
  ): AsyncGenerator<TResponse, void, unknown> {
    if (typeof WebSocketStream === "undefined")
      throw new Error("WebSocketStream is not supported in this browser");

    const wsUrl = `${toWebSocketUrl(this.context.baseUrl)}/ion/${
      this.interfaceName
    }/${this.methodName}.ws`;

    let attempt = 0;

    while (true) {
      try {
        const exchangeToken = await this.createExchangeToken(signal);
        const wss = new WebSocketStream(wsUrl, {
          signal,
          protocols: [`ion!ticket#${exchangeToken}!ver#1`],
        });
        const { readable, writable } = await wss.opened;
        const reader = readable.getReader();
        const writer = writable.getWriter();

        await writer.write(requestPayload);

        if (attempt > 0) {
          IonWsClient.emit("reconnected", attempt);
        }
        attempt = 0;

        while (true) {
          const { value, done } = await reader.read();
          if (done) break;

          if (!(value instanceof Uint8Array)) {
            throw new Error(`Invalid frame type: ${typeof value}`);
          }

          const msg = new Uint8Array(value);
          const opcode = msg[0];
          const body = msg.slice(1);
          const cborReader = new CborReader(body);

          if (opcode === 0x00) {
            yield IonFormatterStorage.get<TResponse>(responseTypename).read(
              cborReader
            );
          } else if (opcode === 0x01) {
            wss.close();
            return;
          } else if (opcode === 0x02) {
            const err =
              IonFormatterStorage.get<IonProtocolError>(
                "IonProtocolError"
              ).read(cborReader);
            wss.close();
            throw new IonRequestException(err);
          } else {
            try {
              yield IonFormatterStorage.get<TResponse>(responseTypename).read(
                cborReader
              );
            } catch (ex: any) {
              wss.close();
              throw new IonRequestException({
                code: "-1",
                message: `Invalid WS frame: ${ex.message}`,
              });
            }
          }
        }
        wss.close();
        throw new Error("WebSocket closed unexpectedly");
      } catch (err) {
        attempt++;
        const delay = Math.min(1000 * 2 ** attempt, 30000);

        IonWsClient.emit("reconnecting", attempt, delay);

        await new Promise((res) => setTimeout(res, delay));

        if (signal?.aborted) {
          IonWsClient.emit("closed");
          throw new DOMException("Aborted", "AbortError");
        }

        continue;
      }
    }
  }
}
