
<img alt="image" src="/images/header.png" />

**IonPath** is a unified communication protocol and contract language designed for modern distributed applications. It combines a compact binary message format (based on **CBOR**) with a strongly typed DSL for defining interfaces and data contracts.

IonPath enables seamless code generation, efficient serialization, and clear separation of protocol boundaries — supporting both **C#** and **TypeScript** out of the box.

---


> [!CAUTION]
> Currently not suitable for use in a production environment, API and language have not yet been stabilized.

## 🔧 What is IonPath?

IonPath consists of two parts:

1. **Ion Contract Language (.ion)** — A compact DSL for defining types, messages, RPC methods, and event schemas
2. **IonPath Protocol** — A binary, CBOR-based messaging layer for exchanging typed data across systems

---

## ✨ Features

- **Declarative contract** (`.ion`) for defining types, services, messages
- **Code generation** for C# and TypeScript — usable in both backend and frontend
- **Compact CBOR encoding** — fast, binary, schema-based
- **Streaming & RPC ready** — defines request/response/event shapes
- **Designed for low-latency systems** — suitable for WebSocket, QUIC, or custom transports
- **Strong static typing & contract lockfiles** — lockfiles pin contract versions across services/CI, guarantee consistency, and prevent accidental breaking changes
- **Backward-compatible evolution** — strict compatibility checks; the protocol safely ignores unknown fields when a consumer uses an older contract version

---

## 📣 Project Roadmap 

| 📜 Language             | Status | 🛠 Code Generation           | Status | 🌐 Transports & Platforms    | Status |
|--------------------------|--------|------------------------------|--------|--------------------------|--------|
| **Core DSL Grammar**     | ✅     | **C# \ Server**              | 🚧     | **HTTP Transport**       | ✅     |
| **Services**             | ✅     | **C# \ Client**              | 🚧     | **QUIC Transport**       | 🔻     |
| **POCO**                 | ✅     | **TypeScript \ Client**      | 🚧     | **NATS**                 | 🔻     |
| **IonPath Protocol**     | ✅     | **TypeScript \ Server**      | 🔻     | **WebSocket Streaming**  | 🔻     |
| **Unions**               | 🔻     | **Rust \ Client**            | 🔻     | **SteamNetworking**      | 🔻     |
| **Unary Calls**          | ✅     | **Rust \ Server**            | 🔻     | **Unity Platform**       | 🔻     |
| **Streaming Calls**      | 🔻     | **Go \ Client**              | 🔻     | **Orleans Platform**     | 🔻     |
| **Streaming Hubs**       | 🔻     | **Go \ Server**              | 🔻     |
|                          |         | **Json Serialization**       | 🔻     |
|                          |         | **MsgPack Serialization**    | 🔻     |
|                          |         | **CBOR Serialization**       | ✅     |
---

*Legend: 🚧 – in progress, ✅ – implemented, 🔻 – planned*
