import { CborReader } from "../cbor";
import { IonFormatterStorage } from "../logic/IonFormatter";
import { safeFetchBuffer } from "../yetAnotherFetch";

export const IonContentType = "application/ion";

export interface IonInterceptor {
  invokeAsync(
    ctx: IonCallContext,
    next: (ctx: IonCallContext, signal?: AbortSignal) => Promise<void>,
    signal?: AbortSignal
  ): Promise<void>;
}

export interface IonCallContext {
  client: typeof fetch;
  interfaceName: string;
  methodName: string;
  requestPayload: Uint8Array;
  responsePayload?: Uint8Array;
  responseStatus?: number;
  responseStatusText?: string;
  response?: Response;
  expectedType?: any;
  requestHeadets: HeadersInit | undefined;
}

export class IonRequest {
  constructor(
    private context: IonClientContext,
    private interfaceName: string,
    private methodName: string
  ) {}

  async callAsync(payload: Uint8Array, signal?: AbortSignal): Promise<void> {
    const ctx: IonCallContext = {
      client: fetch,
      interfaceName: this.interfaceName,
      methodName: this.methodName,
      requestPayload: payload,
      expectedType: undefined,
      requestHeadets: { "Content-Type": IonContentType },
    };

    let next: (c: IonCallContext, s?: AbortSignal) => Promise<void> =
      this.terminalAsync.bind(this);
    for (let i = this.context.interceptors.length - 1; i >= 0; i--) {
      const interceptor = this.context.interceptors[i];
      const currentNext = next;
      next = (c, s) => interceptor.invokeAsync(c, currentNext, s);
    }

    await next(ctx, signal);
  }

  async callAsyncT<TResponse>(
    responseTypename: string,
    payload: Uint8Array,
    signal?: AbortSignal
  ): Promise<TResponse> {
    const normalizedTypename = this.extractTypeName(responseTypename);
    const ctx: IonCallContext = {
      client: fetch,
      interfaceName: this.interfaceName,
      methodName: this.methodName,
      requestPayload: payload,
      expectedType: null as any as TResponse,
      requestHeadets: { "Content-Type": IonContentType },
    };

    let next: (c: IonCallContext, s?: AbortSignal) => Promise<void> =
      this.terminalAsync.bind(this);
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
      if (normalizedTypename.isArray)
        return IonFormatterStorage.readArray<TResponse>(
          reader,
          normalizedTypename.typeName
        ) as TResponse;
      if (normalizedTypename.isMaybe)
        return IonFormatterStorage.readMaybe<TResponse>(
          reader,
          normalizedTypename.typeName
        ) as TResponse;
      return IonFormatterStorage.get<TResponse>(
        normalizedTypename.typeName
      ).read(reader);
    } catch (e) {
      console.error("===== UNCATCH ION INTERNAL ERROR =====");
      console.error(e);
      console.error(`Procedure: ${ctx.interfaceName}/${ctx.methodName}()`);
      console.error(`ResponseTypename: ${responseTypename}`);
      console.error(`Payload: ${this.toBase64(ctx.responsePayload)}`);
      console.error("===== ========================== =====");
      throw e;
    }
  }

  extractTypeName(typeName: string): {
    typeName: string;
    isArray: boolean;
    isMaybe: boolean;
  } {
    let isArray = false;
    let isMaybe = false;
    let inner = typeName.trim();

    if (inner.startsWith("IonArray<") && inner.endsWith(">")) {
      isArray = true;
      inner = inner.slice("IonArray<".length, -1).trim();
    } else if (inner.startsWith("IonMaybe<") && inner.endsWith(">")) {
      isMaybe = true;
      inner = inner.slice("IonMaybe<".length, -1).trim();
    }

    return {
      typeName: inner,
      isArray,
      isMaybe,
    };
  }

  toBase64(u8: Uint8Array): string {
    if (typeof Buffer !== "undefined") {
      return Buffer.from(u8).toString("base64");
    } else {
      let binary = "";
      const chunkSize = 0x8000;
      for (let i = 0; i < u8.length; i += chunkSize) {
        const chunk = u8.subarray(i, i + chunkSize);
        binary += String.fromCharCode(...chunk);
      }
      return btoa(binary);
    }
  }

  private async terminalAsync(
    c: IonCallContext,
    signal?: AbortSignal
  ): Promise<void> {
    const resp = await safeFetchBuffer(`${this.context.baseUrl}/ion/${c.interfaceName}/${c.methodName}.unary`, {
      body: c.requestPayload.buffer as any,
      headers: c.requestHeadets,
      signal: signal,
      method: "POST",
    });

    if (!resp.buffer)
      throw new IonRequestException(
        IonProtocolError.UPSTREAM_ERROR(`no buffer return, status: ${resp.status}`)
      );

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
}

export interface IonClientContext {
  baseUrl: string;
  interceptors: IonInterceptor[];
}

export type IonProtocolError = { code: string; message: string };

export class IonRequestException extends Error {
  constructor(public error: IonProtocolError) {
    super(`Ion Transport Error: ${error.message}`);
  }
}
IonFormatterStorage.register<IonProtocolError>("IonProtocolError", {
  read(reader) {
    reader.readStartArray();
    const code = reader.readTextString();
    const msg = reader.readTextString();
    reader.readEndArray();
    return { code, message: msg };
  },
  write(writer, value) {
    writer.writeStartArray(2);
    writer.writeTextString(value.code);
    writer.writeTextString(value.message);
    writer.writeEndArray();
  },
});

export const IonProtocolError = {
  UPSTREAM_ERROR: (msg: string): IonProtocolError => ({
    code: "-1",
    message: msg,
  }),
};
