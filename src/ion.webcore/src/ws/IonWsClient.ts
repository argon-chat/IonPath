import { CborReader } from "../cbor";
import { IonFormatterStorage } from "../logic/IonFormatter";
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

export class IonWsClient {
  constructor(
    private context: IonClientContext,
    private interfaceName: string,
    private methodName: string
  ) {}

  get baseUrl() {
    return this.context.baseUrl;
  }

  private async terminalExchangeAsync(
    c: IonCallContext,
    signal?: AbortSignal
  ): Promise<void> {
    const url = `${this.context.baseUrl}/ion.att`;

    const resp = await fetch(url, {
      method: "POST",
      body: c.requestPayload.buffer as unknown as BodyInit,
      headers: c.requestHeadets,
      signal,
    });

    const buf = new Uint8Array(await resp.arrayBuffer());
    c.responsePayload = buf;
    c.responseStatus = resp.status;
    c.responseStatusText = resp.statusText;
    c.response = resp;

    if (!resp.ok) {
      try {
        const error = IonFormatterStorage.get<IonProtocolError>(
          "IonProtocolError"
        ).read(new CborReader(buf));
        throw new IonRequestException(error);
      } catch {
        throw new IonRequestException(
          IonProtocolError.UPSTREAM_ERROR(
            resp.statusText || resp.status.toString()
          )
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
      this.terminalExchangeAsync;
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
    const exhangeToken = await this.createExchangeToken(signal);

    const wsUrl = `${toWebSocketUrl(this.baseUrl)}/ion/${this.interfaceName}/${
      this.methodName
    }.ws`;
    const wss = new WebSocketStream(wsUrl, {
      signal,
      protocols: [`ion; ticket=${exhangeToken}; ver=1`],
    });
    const { readable, writable } = await wss.opened;
    const reader = readable.getReader();
    const writer = writable.getWriter();

    await writer.write(requestPayload);

    try {
      while (true) {
        const { value, done } = await reader.read();
        if (!(value instanceof Uint8Array))
          throw new Error(
            `invalid operation exception, websocket return string type, not a buffer, value: ${value}, done: ${done}`
          );

        if (done) break;
        if (!value) continue;

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
            IonFormatterStorage.get<IonProtocolError>("IonProtocolError").read(
              cborReader
            );
          wss.close();
          throw new IonRequestException(err);
        } else {
          try {
            const item =
              IonFormatterStorage.get<TResponse>(responseTypename).read(
                cborReader
              );
            yield item;
          } catch (ex: any) {
            wss.close();
            throw new IonRequestException({
              code: "-1",
              message: `Invalid WS frame: ${ex.message}`,
            });
          }
        }
      }
    } finally {
      wss.close();
    }
  }
}
