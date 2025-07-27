# IonPath

**IonPath** is a unified communication protocol and contract language designed for modern distributed applications. It combines a compact binary message format (based on **CBOR**) with a strongly typed DSL for defining interfaces and data contracts.

IonPath enables seamless code generation, efficient serialization, and clear separation of protocol boundaries — supporting both **C#** and **TypeScript** out of the box.

---

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

---
