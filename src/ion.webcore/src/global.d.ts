interface WebSocketStreamOptions {
  signal?: AbortSignal;
}

interface WebSocketStreamOpenEvent {
  readable: ReadableStream<Uint8Array | string>;
  writable: WritableStream<ArrayBuffer | ArrayBufferView | string>;
  extensions: string;
  protocol: string;
}

interface WebSocketStreamCloseEvent {
  closeCode: number;
  reason: string;
}

declare class WebSocketStream {
  constructor(url: string, options?: WebSocketStreamOptions);
  url: string;
  opened: Promise<WebSocketStreamOpenEvent>;
  closed: Promise<WebSocketStreamCloseEvent>;
  close(options?: WebSocketStreamCloseEvent): void;
}