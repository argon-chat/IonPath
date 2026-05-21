use std::pin::Pin;
use std::task::{Context, Poll};

use futures_util::{Stream, SinkExt, StreamExt};
use tokio_tungstenite::{connect_async_with_config, tungstenite};
use tungstenite::protocol::Message;
use tungstenite::client::IntoClientRequest;

use crate::types::{IonError, IonProtocolError};
use crate::formatter::IonFormat;
use crate::interceptor::IonCallContext;
use crate::client::IonClientContext;

// ═══════════════════════════════════════════════════════════════════
// Wire protocol constants
// ═══════════════════════════════════════════════════════════════════

const OPCODE_DATA: u8 = 0x00;
const OPCODE_END: u8 = 0x01;
const OPCODE_ERROR: u8 = 0x02;

// ═══════════════════════════════════════════════════════════════════
// Base56 encoding for ticket sub-protocol
// ═══════════════════════════════════════════════════════════════════

const BASE56_ALPHABET: &[u8] = b"23456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";

fn base56_encode(data: &[u8]) -> String {
    if data.is_empty() {
        return String::new();
    }

    // Count leading zeros
    let leading_zeros = data.iter().take_while(|&&b| b == 0).count();

    // Convert bytes to base56 (big-endian integer division)
    let mut digits: Vec<u8> = Vec::new();
    for &byte in data {
        let mut carry = byte as u32;
        for d in digits.iter_mut() {
            carry += (*d as u32) * 256;
            *d = (carry % 56) as u8;
            carry /= 56;
        }
        while carry > 0 {
            digits.push((carry % 56) as u8);
            carry /= 56;
        }
    }

    let mut result = String::with_capacity(leading_zeros + digits.len());
    for _ in 0..leading_zeros {
        result.push(BASE56_ALPHABET[0] as char);
    }
    for &d in digits.iter().rev() {
        result.push(BASE56_ALPHABET[d as usize] as char);
    }
    result
}

// ═══════════════════════════════════════════════════════════════════
// Ticket exchange
// ═══════════════════════════════════════════════════════════════════

async fn exchange_ticket(ctx: &IonClientContext) -> Result<String, IonError> {
    // Build interceptor chain for the ticket exchange call
    let mut call_ctx = IonCallContext::new("ion.att", "exchange", Vec::new());

    // Execute through interceptors with a terminal that POSTs to /ion.att
    let interceptors = ctx.interceptors();
    let terminal: Box<dyn crate::interceptor::IonNext> = Box::new(TicketTerminalHandler { base_url: ctx.base_url().to_string() });

    // Build chain
    let mut chain: Box<dyn crate::interceptor::IonNext> = terminal;
    for interceptor in interceptors.iter().rev() {
        chain = Box::new(crate::interceptor::InterceptorChainLink {
            interceptor: interceptor.clone(),
            next: chain,
        });
    }

    chain.invoke(&mut call_ctx).await?;

    // Parse response — CBOR array(1) [bytes]
    let body = call_ctx.response_payload
        .ok_or_else(|| IonError::Decode("Empty ticket response".into()))?;
    let mut d = minicbor::Decoder::new(&body);
    let _len = d.array()?;
    let token_bytes = d.bytes()?;

    Ok(base56_encode(token_bytes))
}

struct TicketTerminalHandler {
    base_url: String,
}

#[async_trait::async_trait]
impl crate::interceptor::IonNext for TicketTerminalHandler {
    async fn invoke(&self, ctx: &mut IonCallContext) -> Result<(), IonError> {
        let url = format!("{}/ion.att", self.base_url);

        let req = reqwest::Client::new()
            .post(&url)
            .header("Content-Type", "application/ion");

        let response = req.body(Vec::new()).send().await?;
        ctx.response_status = Some(response.status().as_u16());
        ctx.response_payload = Some(response.bytes().await?.to_vec());
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonWsStream — server streaming
// ═══════════════════════════════════════════════════════════════════

/// A stream of items received from a server-streaming RPC call.
pub struct IonWsStream<T: IonFormat> {
    rx: tokio::sync::mpsc::Receiver<Result<T, IonError>>,
    _task: tokio::task::JoinHandle<()>,
}

impl<T: IonFormat + Send + 'static> IonWsStream<T> {
    /// Open a server-streaming call.
    /// `payload` is the CBOR-encoded initial arguments.
    pub async fn open(
        ctx: &IonClientContext,
        interface_name: &str,
        method_name: &str,
        payload: &[u8],
    ) -> Result<Self, IonError> {
        // Get ticket
        let ticket = exchange_ticket(ctx).await?;
        let sub_protocol = format!("ion!ticket#{}!ver#1", ticket);

        // Build WebSocket URL
        let ws_base = ctx.base_url()
            .replace("http://", "ws://")
            .replace("https://", "wss://");
        let url = format!("{}/ion/{}/{}.ws", ws_base, interface_name, method_name);

        // Connect with sub-protocol
        let mut request = url.into_client_request()
            .map_err(|e| IonError::Decode(e.to_string()))?;
        request.headers_mut().insert(
            "Sec-WebSocket-Protocol",
            sub_protocol.parse().map_err(|e| IonError::Decode(format!("{}", e)))?,
        );

        let (ws_stream, _response) = connect_async_with_config(request, None, false)
            .await
            .map_err(|e| IonError::Decode(format!("WebSocket connect failed: {}", e)))?;

        let (mut write, mut read) = ws_stream.split();

        // Send initial payload
        write.send(Message::Binary(payload.to_vec().into())).await
            .map_err(|e| IonError::Encode(format!("WebSocket send failed: {}", e)))?;

        // Spawn reader task
        let (tx, rx) = tokio::sync::mpsc::channel::<Result<T, IonError>>(32);
        let task = tokio::spawn(async move {
            while let Some(msg) = read.next().await {
                match msg {
                    Ok(Message::Binary(data)) => {
                        if data.is_empty() {
                            continue;
                        }
                        let opcode = data[0];
                        let payload = &data[1..];
                        match opcode {
                            OPCODE_DATA => {
                                let mut d = minicbor::Decoder::new(payload);
                                match T::ion_read(&mut d) {
                                    Ok(item) => {
                                        if tx.send(Ok(item)).await.is_err() {
                                            break; // receiver dropped
                                        }
                                    }
                                    Err(e) => {
                                        let _ = tx.send(Err(e)).await;
                                        break;
                                    }
                                }
                            }
                            OPCODE_END => break,
                            OPCODE_ERROR => {
                                let mut d = minicbor::Decoder::new(payload);
                                let err = IonProtocolError::ion_read(&mut d)
                                    .unwrap_or_else(|_| IonProtocolError::upstream_error("Unknown stream error"));
                                let _ = tx.send(Err(IonError::Protocol(err))).await;
                                break;
                            }
                            _ => {
                                let _ = tx.send(Err(IonError::Decode(
                                    format!("Unknown opcode: 0x{:02x}", opcode),
                                ))).await;
                                break;
                            }
                        }
                    }
                    Ok(Message::Close(_)) => break,
                    Err(e) => {
                        let _ = tx.send(Err(IonError::Decode(format!("WebSocket error: {}", e)))).await;
                        break;
                    }
                    _ => {} // ignore ping/pong/text
                }
            }
        });

        Ok(Self { rx, _task: task })
    }
}

impl<T: IonFormat> Stream for IonWsStream<T> {
    type Item = Result<T, IonError>;

    fn poll_next(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        self.rx.poll_recv(cx)
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonWsDuplexStream — bidirectional streaming
// ═══════════════════════════════════════════════════════════════════

/// A bidirectional streaming RPC handle.
/// Read items from the response stream, and send input items via `send`.
pub struct IonWsDuplexStream<TIn: IonFormat, TOut: IonFormat> {
    rx: tokio::sync::mpsc::Receiver<Result<TOut, IonError>>,
    input_tx: tokio::sync::mpsc::Sender<TIn>,
    _read_task: tokio::task::JoinHandle<()>,
    _write_task: tokio::task::JoinHandle<()>,
}

impl<TIn: IonFormat + Send + 'static, TOut: IonFormat + Send + 'static> IonWsDuplexStream<TIn, TOut> {
    /// Open a bidirectional streaming call.
    pub async fn open(
        ctx: &IonClientContext,
        interface_name: &str,
        method_name: &str,
        payload: &[u8],
    ) -> Result<Self, IonError> {
        let ticket = exchange_ticket(ctx).await?;
        let sub_protocol = format!("ion!ticket#{}!ver#1", ticket);

        let ws_base = ctx.base_url()
            .replace("http://", "ws://")
            .replace("https://", "wss://");
        let url = format!("{}/ion/{}/{}.ws", ws_base, interface_name, method_name);

        let mut request = url.into_client_request()
            .map_err(|e| IonError::Decode(e.to_string()))?;
        request.headers_mut().insert(
            "Sec-WebSocket-Protocol",
            sub_protocol.parse().map_err(|e| IonError::Decode(format!("{}", e)))?,
        );

        let (ws_stream, _response) = connect_async_with_config(request, None, false)
            .await
            .map_err(|e| IonError::Decode(format!("WebSocket connect failed: {}", e)))?;

        let (mut write, mut read) = ws_stream.split();

        // Send initial payload
        write.send(Message::Binary(payload.to_vec().into())).await
            .map_err(|e| IonError::Encode(format!("WebSocket send failed: {}", e)))?;

        // Spawn reader task
        let (out_tx, rx) = tokio::sync::mpsc::channel::<Result<TOut, IonError>>(32);
        let read_task = tokio::spawn(async move {
            while let Some(msg) = read.next().await {
                match msg {
                    Ok(Message::Binary(data)) => {
                        if data.is_empty() {
                            continue;
                        }
                        let opcode = data[0];
                        let payload = &data[1..];
                        match opcode {
                            OPCODE_DATA => {
                                let mut d = minicbor::Decoder::new(payload);
                                match TOut::ion_read(&mut d) {
                                    Ok(item) => {
                                        if out_tx.send(Ok(item)).await.is_err() {
                                            break;
                                        }
                                    }
                                    Err(e) => {
                                        let _ = out_tx.send(Err(e)).await;
                                        break;
                                    }
                                }
                            }
                            OPCODE_END => break,
                            OPCODE_ERROR => {
                                let mut d = minicbor::Decoder::new(payload);
                                let err = IonProtocolError::ion_read(&mut d)
                                    .unwrap_or_else(|_| IonProtocolError::upstream_error("Unknown stream error"));
                                let _ = out_tx.send(Err(IonError::Protocol(err))).await;
                                break;
                            }
                            _ => {
                                let _ = out_tx.send(Err(IonError::Decode(
                                    format!("Unknown opcode: 0x{:02x}", opcode),
                                ))).await;
                                break;
                            }
                        }
                    }
                    Ok(Message::Close(_)) => break,
                    Err(e) => {
                        let _ = out_tx.send(Err(IonError::Decode(format!("WebSocket error: {}", e)))).await;
                        break;
                    }
                    _ => {}
                }
            }
        });

        // Spawn writer task for input stream
        let (input_tx, mut input_rx) = tokio::sync::mpsc::channel::<TIn>(32);
        let write_task = tokio::spawn(async move {
            while let Some(item) = input_rx.recv().await {
                let mut e = minicbor::Encoder::new(Vec::new());
                // Frame: [0x00][CBOR array(1) [item]]
                let mut frame = vec![OPCODE_DATA];
                if e.array(1).is_err() {
                    break;
                }
                if item.ion_write(&mut e).is_err() {
                    break;
                }
                frame.extend_from_slice(&e.into_writer());
                if write.send(Message::Binary(frame.into())).await.is_err() {
                    break;
                }
            }
            // Signal end of input stream — empty DATA frame (just opcode)
            let _ = write.send(Message::Binary(vec![OPCODE_DATA].into())).await;
        });

        Ok(Self {
            rx,
            input_tx,
            _read_task: read_task,
            _write_task: write_task,
        })
    }

    /// Send an item to the server input stream.
    pub async fn send(&self, item: TIn) -> Result<(), IonError> {
        self.input_tx.send(item).await
            .map_err(|_| IonError::Encode("Input stream closed".into()))
    }

    /// Signal that no more input items will be sent.
    /// This drops the sender, causing the write task to send the END signal.
    pub fn close_input(self) -> IonWsStream<TOut> {
        drop(self.input_tx);
        IonWsStream {
            rx: self.rx,
            _task: self._read_task,
        }
    }
}

impl<TIn: IonFormat, TOut: IonFormat> Stream for IonWsDuplexStream<TIn, TOut> {
    type Item = Result<TOut, IonError>;

    fn poll_next(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        self.rx.poll_recv(cx)
    }
}
