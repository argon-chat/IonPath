import { CborReader } from "../cbor";
import { IonFormatterStorage } from "../logic/IonFormatter";

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
      requestHeadets: { "Content-Type": IonContentType }
    };

    let next: (c: IonCallContext, s?: AbortSignal) 
        => Promise<void> = this.terminalAsync;
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
    const ctx: IonCallContext = {
      client: fetch,
      interfaceName: this.interfaceName,
      methodName: this.methodName,
      requestPayload: payload,
      expectedType: null as any as TResponse,
      requestHeadets: { "Content-Type": IonContentType }
    };

    let next: (c: IonCallContext, s?: AbortSignal) 
        => Promise<void> = this.terminalAsync;
    for (let i = this.context.interceptors.length - 1; i >= 0; i--) {
      const interceptor = this.context.interceptors[i];
      const currentNext = next;
      next = (c, s) => interceptor.invokeAsync(c, currentNext, s);
    }

    await next(ctx, signal);

    if (!ctx.responsePayload) {
      throw new Error("No response payload");
    }

    const reader = new CborReader(ctx.responsePayload);
    return IonFormatterStorage.get<TResponse>(responseTypename).read(reader);
  }

  private async terminalAsync(
    c: IonCallContext,
    signal?: AbortSignal
  ): Promise<void> {
    const url = `${this.context.baseUrl}/ion/${c.interfaceName}/${c.methodName}.unary`;

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
        const error = IonFormatterStorage
            .get<IonProtocolError>("IonProtocolError")
            .read(new CborReader(buf));
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
}

export interface IonClientContext {
  baseUrl: string;
  interceptors: IonInterceptor[];
}

export type IonProtocolError = { code: number; message: string };

export class IonRequestException extends Error {
  constructor(public error: IonProtocolError) {
    super(error.message);
  }
}
export const IonProtocolError = {
  UPSTREAM_ERROR: (msg: string): IonProtocolError => ({
    code: -1,
    message: msg,
  }),
};
