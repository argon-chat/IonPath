import { CborReader } from "../cbor";
import { IonFormatterStorage } from "../logic/IonFormatter";

type IonProtocolError = { code: number; message: string };
class IonRequestException extends Error {
  constructor(public error: IonProtocolError) {
    super(error.message);
  }
}

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
  return u.toString();
}

export class IonWsClient {
  constructor(
    private baseUrl: string,
    private interfaceName: string,
    private methodName: string
  ) {}

  async *callServerStreaming<TResponse>(
    responseTypename: string,
    requestPayload: Uint8Array,
    signal?: AbortSignal
  ): AsyncGenerator<TResponse, void, unknown> {
    if (typeof WebSocketStream === "undefined") {
      throw new Error("WebSocketStream не поддерживается в этом браузере");
    }

    const wsUrl = `${toWebSocketUrl(this.baseUrl)}/ion/${this.interfaceName}/${this.methodName}.ws`;
    const wss = new WebSocketStream(wsUrl, { signal });
    const { readable, writable } = await wss.opened;
    const reader = readable.getReader();
    const writer = writable.getWriter();

    await writer.write(requestPayload);

    try {
      while (true) {
        const { value, done } = await reader.read();
        if (value instanceof String)
            throw new Error("invalid operation exception, websocket return string type, not a buffer");
        if (!(value instanceof Uint8Array))
            throw new Error("invalid operation exception, websocket return string type, not a buffer");

        if (done) break;
        if (!value) continue;

        const msg = new Uint8Array(value);
        const opcode = msg[0];
        const body = msg.slice(1);
        const cborReader = new CborReader(body);

        

        if (opcode === 0x00) {
          yield IonFormatterStorage.get<TResponse>(responseTypename).read(cborReader);
        } else if (opcode === 0x01) {
          wss.close();
          return;
        } else if (opcode === 0x02) {
          const err = IonFormatterStorage.get<IonProtocolError>("IonProtocolError").read(cborReader);
          wss.close();
          throw new IonRequestException(err);
        } else {
          try {
            const item = IonFormatterStorage.get<TResponse>(responseTypename).read(cborReader);
            yield item;
          } catch (ex: any) {
            wss.close();
            throw new IonRequestException({
              code: -1,
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
