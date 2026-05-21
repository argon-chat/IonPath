# ion-rustcore

IonPath runtime library for Rust clients — CBOR-based RPC with unary and streaming support.

## Features

- **Unary calls** — HTTP POST with CBOR serialization
- **Server streaming** — WebSocket-based server-to-client streaming
- **Bidirectional streaming** — Full-duplex WebSocket communication
- **Interceptor pipeline** — Middleware chain for auth, logging, etc.
- **Code generation** — Used with `ionc` compiler to generate typed clients from `.ion` contracts

## Usage

This crate is typically used with generated code from the IonPath compiler (`ionc`).

```rust
use ion_rustcore::IonClient;

let ctx = IonClient::new("http://localhost:5000")
    .build();

let math = ctx.service::<MathInteractionClient>();
let result = math.add(2, 3).await?;
```

## License

MIT
