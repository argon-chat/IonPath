//! `stream` calls: the client half of Ion stream protocol v2, over WebSocket.
//!
//! The contract is `ion.runtime.IonStreamProtocol` (C#), and the reference client is
//! `ion.runtime.client.IonStreamCall`. In short:
//!
//! * The client offers exactly one sub-protocol, `ion!ticket#<base56>!ver#2`.
//! * Its first message is the CBOR argument array, with no opcode. From then on every WebSocket
//!   message, in either direction, is one `[opcode][payload]` frame.
//! * Once its connect hooks accepted the call the server sends READY
//!   `[connectionId, keepAliveMs, clientTimeoutMs]`. `open` returns only after READY, so a server
//!   that refuses the call makes `open` fail rather than the first item.
//! * Either side PINGs when it has sent nothing for its keep-alive interval, and gives up on a peer
//!   that has sent nothing for its timeout.
//! * The server ends the call with END (completed), ERROR `[code, msg]` or CLOSE
//!   `[reason, allowReconnect]`. The client ends its input with END and leaves with CLOSE
//!   `[reason]`. Each goodbye is followed by the WebSocket close handshake.
//!
//! **One reader, one writer, no locks.** After READY the socket is split. The reader task owns the
//! read half and turns frames into items. The writer task owns the write half and is the only code
//! that writes to it: input items, pings and the goodbye queue up for it in order.

use std::future::Future;
use std::marker::PhantomData;
use std::pin::Pin;
use std::task::{Context, Poll};
use std::time::Duration;

use futures_util::stream::{SplitSink, SplitStream};
use futures_util::{SinkExt, Stream, StreamExt};
use minicbor::Decoder;
use tokio::net::TcpStream;
use tokio::sync::{mpsc, oneshot};
use tokio::task::{AbortHandle, JoinHandle};
use tokio::time::{sleep_until, timeout_at, Instant};
use tokio_tungstenite::{connect_async_with_config, tungstenite, MaybeTlsStream, WebSocketStream};
use tungstenite::client::IntoClientRequest;
use tungstenite::protocol::frame::coding::CloseCode;
use tungstenite::protocol::frame::Utf8Bytes;
use tungstenite::protocol::{CloseFrame, Message};

use crate::client::IonClientContext;
use crate::formatter::{skip_value, IonFormat};
use crate::interceptor::{InterceptorChainLink, IonCallContext, IonNext};
use crate::types::{DisconnectReason, IonError, IonProtocolError};

type Ws = WebSocketStream<MaybeTlsStream<TcpStream>>;

// ═══════════════════════════════════════════════════════════════════
// Wire protocol constants
// ═══════════════════════════════════════════════════════════════════

/// The protocol version this runtime speaks: the only one there is.
const PROTOCOL_VERSION: u32 = 2;

/// One item. Server to client: the CBOR item. Client to server: `array(1)[item]`.
const OP_DATA: u8 = 0x00;
/// Server to client: the stream method completed. Client to server: end of the input stream.
const OP_END: u8 = 0x01;
/// An `IonProtocolError`. Server to client it ends the stream.
const OP_ERROR: u8 = 0x02;
/// Heartbeat, either direction, no payload.
const OP_PING: u8 = 0x03;
/// Deliberate close. Server to client: `[reason, allowReconnect]`. Client to server: `[reason]`.
const OP_CLOSE: u8 = 0x04;
/// Server to client, once: `[connectionId, keepAliveMs, clientTimeoutMs]`.
const OP_READY: u8 = 0x05;

/// CLOSE `[null]`: the client's goodbye, without a reason.
const CLIENT_CLOSE_FRAME: [u8; 3] = [OP_CLOSE, 0x81, 0xf6];

/// Input items a duplex call may queue ahead of the writer before `send` waits.
const INPUT_QUEUE_CAPACITY: usize = 64;

// ═══════════════════════════════════════════════════════════════════
// IonStreamOptions
// ═══════════════════════════════════════════════════════════════════

/// Client-side settings for `stream` calls, set with
/// [`IonClient::with_stream_options`](crate::IonClient::with_stream_options). This is the
/// counterpart of C#'s `IonStreamClientOptions`.
///
/// A `Duration::ZERO` disables that check.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct IonStreamOptions {
    /// How long the client may stay silent before it sends a PING. Lowered to half the client
    /// timeout the server announces in READY when that is shorter. Default 15 s.
    pub keep_alive_interval: Duration,

    /// How long the server may stay silent before the call fails with
    /// [`DisconnectReason::Timeout`]. Raised to twice the keep-alive the server announces in
    /// READY when that is longer. Default 30 s.
    pub server_timeout: Duration,

    /// How long connecting may take, and then how long waiting for READY may take. Default 15 s.
    pub handshake_timeout: Duration,

    /// How long a graceful close may take before the connection is dropped. Default 5 s.
    pub close_timeout: Duration,

    /// How many received items may wait for the consumer before the client stops reading.
    /// Default 64.
    pub receive_queue_capacity: usize,
}

impl Default for IonStreamOptions {
    fn default() -> Self {
        Self {
            keep_alive_interval: Duration::from_secs(15),
            server_timeout: Duration::from_secs(30),
            handshake_timeout: Duration::from_secs(15),
            close_timeout: Duration::from_secs(5),
            receive_queue_capacity: 64,
        }
    }
}

/// The timings a call actually runs with: the client's options adjusted by what READY announced.
/// `None` means disabled.
#[derive(Debug, Clone, Copy)]
struct Timings {
    keep_alive: Option<Duration>,
    server_timeout: Option<Duration>,
    close_timeout: Option<Duration>,
}

impl Timings {
    fn adopt(options: &IonStreamOptions, ready: &Ready) -> Self {
        // Ping at least twice per server-side client timeout.
        let mut keep_alive = enabled(options.keep_alive_interval);
        if let Some(client_timeout) = ready.client_timeout {
            let half = client_timeout / 2;
            keep_alive = Some(keep_alive.map_or(half, |k| k.min(half)));
        }

        // Give the server at least two of its own keep-alive intervals before calling it dead.
        let mut server_timeout = enabled(options.server_timeout);
        if let (Some(server_keep_alive), Some(timeout)) = (ready.keep_alive, server_timeout) {
            server_timeout = Some(timeout.max(server_keep_alive.saturating_mul(2)));
        }

        Self {
            keep_alive: keep_alive.and_then(enabled),
            server_timeout,
            close_timeout: enabled(options.close_timeout),
        }
    }
}

fn enabled(value: Duration) -> Option<Duration> {
    (!value.is_zero()).then_some(value)
}

/// `from + after`. `None` when disabled, or so far away that it cannot be represented.
fn deadline_after(from: Instant, after: Option<Duration>) -> Option<Instant> {
    after.and_then(|after| from.checked_add(after))
}

async fn sleep_until_opt(deadline: Option<Instant>) {
    match deadline {
        Some(deadline) => sleep_until(deadline).await,
        None => std::future::pending().await,
    }
}

/// Runs `future` bounded by `limit`; `None` when it timed out.
async fn within<F: Future>(limit: Option<Duration>, future: F) -> Option<F::Output> {
    match deadline_after(Instant::now(), limit) {
        Some(deadline) => timeout_at(deadline, future).await.ok(),
        None => Some(future.await),
    }
}

// ═══════════════════════════════════════════════════════════════════
// Control payloads
// ═══════════════════════════════════════════════════════════════════

/// What the server announces in READY.
struct Ready {
    connection_id: String,
    /// How often the server pings when idle.
    keep_alive: Option<Duration>,
    /// How long the server waits for any frame before dropping the client.
    client_timeout: Option<Duration>,
}

fn wire_ms(value: u64) -> Option<Duration> {
    (value != 0).then(|| Duration::from_millis(value))
}

/// Reads a READY payload. Trailing elements a later version may add are skipped.
fn read_ready(payload: &[u8]) -> Result<Ready, IonError> {
    let mut d = Decoder::new(payload);
    let size = d
        .array()?
        .ok_or_else(|| IonError::Decode("READY must be a definite-length array".into()))?;
    if size < 3 {
        return Err(IonError::Decode(format!("READY carries {size} elements, expected at least 3")));
    }

    let connection_id = d.str()?.to_owned();
    let keep_alive = wire_ms(d.u64()?);
    let client_timeout = wire_ms(d.u64()?);
    for _ in 3..size {
        skip_value(&mut d)?;
    }

    Ok(Ready { connection_id, keep_alive, client_timeout })
}

/// Reads the server's CLOSE payload, `[reason?, allowReconnect]`. An empty payload is a close
/// without a reason; trailing elements are skipped.
fn read_close(payload: &[u8]) -> Result<(Option<String>, bool), IonError> {
    if payload.is_empty() {
        return Ok((None, false));
    }

    let mut d = Decoder::new(payload);
    let size = d
        .array()?
        .ok_or_else(|| IonError::Decode("CLOSE must be a definite-length array".into()))?;

    let mut reason = None;
    let mut allow_reconnect = false;

    if size > 0 {
        if matches!(d.datatype()?, minicbor::data::Type::Null | minicbor::data::Type::Undefined) {
            d.skip()?;
        } else {
            reason = Some(d.str()?.to_owned());
        }
    }

    if size > 1 {
        allow_reconnect = d.bool()?;
    }

    for _ in 2..size {
        skip_value(&mut d)?;
    }

    Ok((reason, allow_reconnect))
}

fn read_error(payload: &[u8]) -> Result<IonProtocolError, IonError> {
    IonProtocolError::ion_read(&mut Decoder::new(payload))
}

/// `[0x00][array(1)[item]]`: one input item.
fn encode_input<T: IonFormat>(item: &T) -> Result<Vec<u8>, IonError> {
    let mut e = minicbor::Encoder::new(vec![OP_DATA]);
    e.array(1)?;
    item.ion_write(&mut e)?;
    Ok(e.into_writer())
}

fn close_message() -> Message {
    Message::Close(Some(CloseFrame {
        code: CloseCode::Normal,
        reason: Utf8Bytes::from_static("client closed"),
    }))
}

fn describe_close(frame: &Option<CloseFrame>) -> String {
    match frame {
        Some(frame) if frame.reason.is_empty() => format!(" (close code {})", u16::from(frame.code)),
        Some(frame) => format!(" (close code {}, \"{}\")", u16::from(frame.code), frame.reason.as_str()),
        None => String::new(),
    }
}

fn disconnected(reason: DisconnectReason, message: impl Into<String>) -> IonError {
    IonError::StreamDisconnected { reason, message: message.into() }
}

fn violation(message: impl Into<String>) -> IonError {
    disconnected(DisconnectReason::ProtocolViolation, message)
}

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

/// The one sub-protocol entry the client offers.
fn sub_protocol(ticket: &str) -> String {
    if ticket.is_empty() {
        format!("ion!ver#{PROTOCOL_VERSION}")
    } else {
        format!("ion!ticket#{ticket}!ver#{PROTOCOL_VERSION}")
    }
}

// ═══════════════════════════════════════════════════════════════════
// Ticket exchange
// ═══════════════════════════════════════════════════════════════════

/// `POST {base}/ion.att` through the context's interceptors, as a unary call goes: whatever
/// headers they put on the call context (credentials, a user name, ...) travel with the request,
/// and it is sent with the context's HTTP client. Returns the ticket, base56-encoded.
async fn exchange_ticket(ctx: &IonClientContext) -> Result<String, IonError> {
    let mut call_ctx = IonCallContext::new("ion.att", "exchange", Vec::new());

    let mut chain: Box<dyn IonNext> = Box::new(TicketTerminalHandler {
        base_url: ctx.base_url().to_string(),
        session_id: ctx.session_id.clone(),
        http_client: ctx.http_client.clone(),
    });
    for interceptor in ctx.interceptors().iter().rev() {
        chain = Box::new(InterceptorChainLink { interceptor: interceptor.clone(), next: chain });
    }

    chain.invoke(&mut call_ctx).await?;

    let body = call_ctx.response_payload.unwrap_or_default();

    // A refused exchange carries the server's `IonProtocolError` in its body; anything else that
    // is not a success is reported with its status.
    if let Some(status) = call_ctx.response_status.filter(|s| !(200..300).contains(s)) {
        return Err(IonError::Protocol(read_error(&body).unwrap_or_else(|_| {
            IonProtocolError::upstream_error(format!(
                "The ticket exchange failed with HTTP {status}, and its body is not an Ion error ({} bytes).",
                body.len()
            ))
        })));
    }

    if body.is_empty() {
        return Err(IonError::Protocol(IonProtocolError::upstream_error("Empty response from ion.att")));
    }

    read_ticket(&body).map(|token| base56_encode(&token))
}

/// The exchange's answer: `array(1)[bytes]`.
fn read_ticket(body: &[u8]) -> Result<Vec<u8>, IonError> {
    let mut d = Decoder::new(body);
    let size = d
        .array()?
        .ok_or_else(|| IonError::Decode("The ticket response must be a definite-length array".into()))?;
    if size < 1 {
        return Err(IonError::Decode("The ticket response carries no ticket".into()));
    }
    Ok(d.bytes()?.to_vec())
}

struct TicketTerminalHandler {
    base_url: String,
    session_id: String,
    http_client: reqwest::Client,
}

#[async_trait::async_trait]
impl IonNext for TicketTerminalHandler {
    async fn invoke(&self, ctx: &mut IonCallContext) -> Result<(), IonError> {
        let url = format!("{}/ion.att", self.base_url);

        let mut request = self
            .http_client
            .post(&url)
            .header(reqwest::header::CONTENT_TYPE, "application/ion")
            .header("X-Ion-Session-Id", &self.session_id)
            .body(ctx.request_payload.clone());

        if let Some(ref correlation_id) = ctx.correlation_id {
            request = request.header("X-Ion-Correlation-Id", correlation_id.as_str());
        }

        // What the interceptors asked for; it replaces a default of the same name.
        request = request.headers(ctx.request_headers.clone());

        let response = request.send().await?;
        ctx.response_status = Some(response.status().as_u16());
        ctx.response_payload = Some(response.bytes().await?.to_vec());
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// Handshake: connect, send the arguments, wait for READY
// ═══════════════════════════════════════════════════════════════════

/// How the handshake failed, which decides how the socket is left.
enum HandshakeEnd {
    /// The server said goodbye (ERROR, CLOSE, END or a WebSocket close): answer it gracefully.
    Goodbye(IonError),
    /// The connection is broken or the server broke the protocol: drop it.
    Abort(IonError),
}

async fn handshake(
    ctx: &IonClientContext,
    interface_name: &str,
    method_name: &str,
    payload: &[u8],
    options: &IonStreamOptions,
) -> Result<(Ws, Ready, Instant), IonError> {
    let ticket = exchange_ticket(ctx).await?;

    let ws_base = ctx.base_url()
        .replace("http://", "ws://")
        .replace("https://", "wss://");
    let url = format!("{}/ion/{}/{}.ws", ws_base, interface_name, method_name);

    let mut request = url.as_str().into_client_request()
        .map_err(|e| IonError::Decode(format!("Invalid stream URL '{url}': {e}")))?;
    request.headers_mut().insert(
        "Sec-WebSocket-Protocol",
        sub_protocol(&ticket).parse().map_err(|e| IonError::Decode(format!("{}", e)))?,
    );

    let handshake_timeout = enabled(options.handshake_timeout);
    let close_timeout = enabled(options.close_timeout);

    // tungstenite fails the upgrade when the server echoes a sub-protocol other than the one
    // offered, so a server that negotiated anything but v2 never gets this far.
    let mut ws = match within(handshake_timeout, connect_async_with_config(request, None, true)).await {
        Some(Ok((ws, _response))) => ws,
        Some(Err(e)) => return Err(connect_failure(&url, e)),
        None => {
            return Err(disconnected(
                DisconnectReason::Timeout,
                format!("The WebSocket to {url} did not open within {:?}.", options.handshake_timeout),
            ))
        }
    };

    let args = Message::Binary(payload.to_vec().into());
    match within(handshake_timeout, ws.send(args)).await {
        Some(Ok(())) => {}
        Some(Err(e)) => {
            return Err(disconnected(
                DisconnectReason::TransportLost,
                format!("Sending the arguments to {url} failed: {e}"),
            ))
        }
        None => {
            return Err(disconnected(
                DisconnectReason::Timeout,
                format!("Sending the arguments to {url} did not finish within {:?}.", options.handshake_timeout),
            ))
        }
    }
    let args_sent_at = Instant::now();

    match within(handshake_timeout, await_ready(&mut ws)).await {
        Some(Ok(ready)) => Ok((ws, ready, args_sent_at)),
        Some(Err(HandshakeEnd::Goodbye(err))) => {
            close_socket(&mut ws, false, close_timeout).await;
            Err(err)
        }
        Some(Err(HandshakeEnd::Abort(err))) => Err(err),
        None => {
            // Still waiting on the server: leave the way a dropped call does.
            close_socket(&mut ws, true, close_timeout).await;
            Err(disconnected(
                DisconnectReason::Timeout,
                format!("The server did not accept the stream within {:?}.", options.handshake_timeout),
            ))
        }
    }
}

/// A refused upgrade carries the server's `IonProtocolError` in its body (`TICKET_BROKEN`,
/// `UNSUPPORTED_PROTOCOL_VERSION`, a rejected ticket, ...); anything else is a transport failure.
fn connect_failure(url: &str, error: tungstenite::Error) -> IonError {
    if let tungstenite::Error::Http(response) = &error {
        if let Some(Ok(err)) = response.body().as_deref().map(read_error) {
            return IonError::Protocol(err);
        }

        return disconnected(
            DisconnectReason::TransportLost,
            format!("The WebSocket to {url} was refused with HTTP {}.", response.status()),
        );
    }

    disconnected(DisconnectReason::TransportLost, format!("The WebSocket to {url} did not open: {error}"))
}

/// Reads until READY. Whatever the server says instead is how the call was refused.
async fn await_ready(ws: &mut Ws) -> Result<Ready, HandshakeEnd> {
    use HandshakeEnd::{Abort, Goodbye};

    loop {
        let data = match ws.next().await {
            Some(Ok(Message::Binary(data))) => data,
            Some(Ok(Message::Close(frame))) => {
                return Err(Goodbye(disconnected(
                    DisconnectReason::TransportLost,
                    format!("The server closed the WebSocket before accepting the stream{}.", describe_close(&frame)),
                )))
            }
            Some(Ok(Message::Text(_))) => return Err(Abort(violation("The server sent a text frame."))),
            Some(Ok(_)) => continue, // WebSocket-level ping/pong
            Some(Err(e)) => {
                return Err(Abort(disconnected(
                    DisconnectReason::TransportLost,
                    format!("The WebSocket failed before the server accepted the stream: {e}"),
                )))
            }
            None => {
                return Err(Abort(disconnected(
                    DisconnectReason::TransportLost,
                    "The connection closed before the server accepted the stream.",
                )))
            }
        };

        let Some((&opcode, payload)) = data.split_first() else {
            return Err(Abort(violation("The server sent an empty frame.")));
        };

        return match opcode {
            OP_READY => read_ready(payload)
                .map_err(|e| Abort(violation(format!("The server sent a malformed READY frame: {e}")))),
            OP_PING => continue,
            OP_ERROR => Err(match read_error(payload) {
                Ok(err) => Goodbye(IonError::Protocol(err)),
                Err(e) => Abort(violation(format!("The server sent a malformed ERROR frame: {e}"))),
            }),
            OP_CLOSE => Err(match read_close(payload) {
                Ok((reason, allow_reconnect)) => Goodbye(IonError::StreamClosed { reason, allow_reconnect }),
                Err(e) => Abort(violation(format!("The server sent a malformed CLOSE frame: {e}"))),
            }),
            OP_END => Err(Goodbye(violation("The server ended the stream before accepting it."))),
            OP_DATA => Err(Abort(violation("The server sent DATA before READY."))),
            other => Err(Abort(violation(format!("The server sent unknown opcode 0x{other:02x}.")))),
        };
    }
}

/// Leaves a socket that is not split yet: CLOSE (when the server has not said goodbye), the
/// WebSocket close, then the server's close — all within `close_timeout`.
async fn close_socket(ws: &mut Ws, say_close: bool, close_timeout: Option<Duration>) {
    within(close_timeout, async {
        if say_close && ws.send(Message::Binary(CLIENT_CLOSE_FRAME.to_vec().into())).await.is_err() {
            return;
        }
        // After the server's close this only flushes the reply tungstenite already queued.
        let _ = ws.send(close_message()).await;
        while let Some(Ok(_)) = ws.next().await {}
    })
    .await;
}

// ═══════════════════════════════════════════════════════════════════
// The running call: one reader task, one writer task
// ═══════════════════════════════════════════════════════════════════

/// The reader's instructions to the writer.
enum Ctrl {
    /// The server said goodbye: answer with the WebSocket close and stop.
    ServerGoodbye,
    /// The client leaves on its own (an unreadable item): CLOSE, then the WebSocket close.
    Leave,
}

type ItemSender<T> = mpsc::Sender<Result<T, IonError>>;

/// The client's side of a running call. Dropping it is the goodbye: the writer sends CLOSE and
/// the WebSocket close, and the reader waits (up to the close timeout) for the server's close.
struct StreamConnection {
    connection_id: String,
    /// Held for as long as the call is wanted. The writer says goodbye when it is dropped.
    life: Option<oneshot::Sender<()>>,
    reader: Option<JoinHandle<()>>,
    writer: Option<JoinHandle<()>>,
    close_timeout: Option<Duration>,
}

impl StreamConnection {
    /// Says goodbye and waits for both tasks, aborting what does not finish within the close timeout.
    async fn close(mut self) {
        drop(self.life.take());

        let deadline = deadline_after(Instant::now(), self.close_timeout);
        for handle in [self.writer.take(), self.reader.take()].into_iter().flatten() {
            let mut handle = handle;
            match deadline {
                Some(deadline) => {
                    if timeout_at(deadline, &mut handle).await.is_err() {
                        handle.abort();
                    }
                }
                None => {
                    let _ = handle.await;
                }
            }
        }
    }
}

/// Handshakes, then starts the reader and the writer.
async fn open_call<T: IonFormat + Send + 'static>(
    ctx: &IonClientContext,
    interface_name: &str,
    method_name: &str,
    payload: &[u8],
    with_input: bool,
) -> Result<(mpsc::Receiver<Result<T, IonError>>, StreamConnection, Option<mpsc::Sender<Vec<u8>>>), IonError> {
    let options = ctx.stream_options().clone();
    let (ws, ready, args_sent_at) = handshake(ctx, interface_name, method_name, payload, &options).await?;
    let timings = Timings::adopt(&options, &ready);

    let (sink, read) = ws.split();
    let (items_tx, items_rx) = mpsc::channel(options.receive_queue_capacity.max(1));
    let (ctrl_tx, ctrl_rx) = mpsc::unbounded_channel();
    let (failed_tx, failed_rx) = oneshot::channel();
    let (life_tx, life_rx) = oneshot::channel();
    let (input_tx, input_rx) = if with_input {
        let (tx, rx) = mpsc::channel(INPUT_QUEUE_CAPACITY);
        (Some(tx), Some(rx))
    } else {
        (None, None)
    };

    let writer = tokio::spawn(write_loop(
        sink,
        input_rx,
        life_rx,
        ctrl_rx,
        failed_tx,
        timings.keep_alive,
        args_sent_at,
    ));
    let reader = tokio::spawn(read_loop::<T>(read, items_tx, ctrl_tx, failed_rx, writer.abort_handle(), timings));

    let connection = StreamConnection {
        connection_id: ready.connection_id,
        life: Some(life_tx),
        reader: Some(reader),
        writer: Some(writer),
        close_timeout: timings.close_timeout,
    };

    Ok((items_rx, connection, input_tx))
}

async fn consumer_gone<T>(items: &Option<ItemSender<T>>) {
    match items {
        Some(items) => items.closed().await,
        None => std::future::pending().await,
    }
}

/// Hands the consumer its last result, which ends the item stream after it.
async fn finish<T>(items: &mut Option<ItemSender<T>>, error: IonError) {
    if let Some(items) = items.take() {
        let _ = items.send(Err(error)).await;
    }
}

/// The only reader. Frames become items; END, ERROR, CLOSE, a dead transport or the server's
/// silence end the item stream, and the first of them wins.
async fn read_loop<T: IonFormat + Send + 'static>(
    mut read: SplitStream<Ws>,
    items: ItemSender<T>,
    ctrl: mpsc::UnboundedSender<Ctrl>,
    mut writer_failed: oneshot::Receiver<IonError>,
    writer: AbortHandle,
    timings: Timings,
) {
    let mut items = Some(items);
    let mut writer_running = true;
    let mut last_received = Instant::now();

    // Set once the call is over, from either side. From then on frames are ignored and only the
    // WebSocket's own close matters, which has until `closing_deadline` to arrive.
    let mut closing = false;
    let mut closing_deadline = None;

    macro_rules! start_closing {
        () => {{
            closing = true;
            closing_deadline = deadline_after(Instant::now(), timings.close_timeout);
        }};
    }

    let failure: Option<IonError> = loop {
        let deadline = if closing {
            closing_deadline
        } else {
            deadline_after(last_received, timings.server_timeout)
        };

        let message = tokio::select! {
            biased;
            failed = &mut writer_failed, if writer_running => {
                writer_running = false;
                match failed {
                    Ok(error) => break (!closing).then_some(error),
                    Err(_) => continue, // the writer finished without failing
                }
            }
            _ = consumer_gone(&items), if !closing => {
                // The stream was dropped or closed, so the writer is saying goodbye.
                items = None;
                start_closing!();
                continue;
            }
            // Ahead of the deadline: a frame that arrived in time counts.
            message = read.next() => message,
            _ = sleep_until_opt(deadline) => {
                if closing {
                    break None;
                }
                let silence = timings.server_timeout.unwrap_or_default().as_millis();
                break Some(disconnected(
                    DisconnectReason::Timeout,
                    format!("The server sent nothing for {silence} ms."),
                ));
            }
        };
        last_received = Instant::now();

        let data = match message {
            Some(Ok(Message::Binary(data))) => data,
            Some(Ok(Message::Close(frame))) => {
                if !closing {
                    start_closing!();
                    let _ = ctrl.send(Ctrl::ServerGoodbye);
                    finish(&mut items, disconnected(
                        DisconnectReason::TransportLost,
                        format!("The server closed the WebSocket without ending the stream{}.", describe_close(&frame)),
                    )).await;
                }
                // Keep reading: the next read flushes the reply to the server's close.
                continue;
            }
            Some(Ok(Message::Text(_))) if !closing => break Some(violation("The server sent a text frame.")),
            Some(Ok(_)) => continue, // WebSocket-level ping/pong, or anything once closing
            Some(Err(e)) => {
                break (!closing).then(|| disconnected(
                    DisconnectReason::TransportLost,
                    format!("The WebSocket failed: {e}"),
                ))
            }
            None => {
                break (!closing).then(|| disconnected(
                    DisconnectReason::TransportLost,
                    "The connection closed without the server ending the stream.",
                ))
            }
        };

        if closing {
            continue;
        }

        let Some((&opcode, payload)) = data.split_first() else {
            break Some(violation("The server sent an empty frame."));
        };

        match opcode {
            OP_DATA => {
                let decoded = T::ion_read(&mut Decoder::new(payload));
                match decoded {
                    Ok(item) => {
                        let delivered = match &items {
                            Some(items) => items.send(Ok(item)).await.is_ok(),
                            None => false,
                        };
                        // Waiting on a slow consumer is not the server being silent.
                        last_received = Instant::now();
                        if !delivered {
                            items = None;
                            start_closing!();
                        }
                    }
                    Err(e) => {
                        // An unreadable item ends the call from our side: leave gracefully, as
                        // the C# client does when its decoder throws.
                        start_closing!();
                        let _ = ctrl.send(Ctrl::Leave);
                        finish(&mut items, e).await;
                    }
                }
            }
            OP_PING | OP_READY => {}
            OP_END => {
                start_closing!();
                let _ = ctrl.send(Ctrl::ServerGoodbye);
                items = None; // the item stream ends cleanly
            }
            OP_ERROR => match read_error(payload) {
                Ok(error) => {
                    start_closing!();
                    let _ = ctrl.send(Ctrl::ServerGoodbye);
                    finish(&mut items, IonError::Protocol(error)).await;
                }
                Err(e) => break Some(violation(format!("The server sent a malformed ERROR frame: {e}"))),
            },
            OP_CLOSE => match read_close(payload) {
                Ok((reason, allow_reconnect)) => {
                    start_closing!();
                    let _ = ctrl.send(Ctrl::ServerGoodbye);
                    finish(&mut items, IonError::StreamClosed { reason, allow_reconnect }).await;
                }
                Err(e) => break Some(violation(format!("The server sent a malformed CLOSE frame: {e}"))),
            },
            other => break Some(violation(format!("The server sent unknown opcode 0x{other:02x}."))),
        }
    };

    // The reader is done, so the connection is. A graceful close has already happened by now;
    // otherwise this drops it, and the writer may be stuck behind a peer that stopped reading.
    writer.abort();
    drop(ctrl);
    drop(read);

    if let Some(error) = failure {
        finish(&mut items, error).await;
    }
}

async fn next_input(input: &mut Option<mpsc::Receiver<Vec<u8>>>) -> Option<Vec<u8>> {
    match input {
        Some(input) => input.recv().await,
        None => std::future::pending().await,
    }
}

/// CLOSE `[null]`, then the WebSocket close.
async fn say_goodbye(sink: &mut SplitSink<Ws, Message>) {
    if sink.send(Message::Binary(CLIENT_CLOSE_FRAME.to_vec().into())).await.is_ok() {
        let _ = sink.send(close_message()).await;
    }
}

/// The only writer after the argument frame.
async fn write_loop(
    mut sink: SplitSink<Ws, Message>,
    mut input: Option<mpsc::Receiver<Vec<u8>>>,
    mut life: oneshot::Receiver<()>,
    mut ctrl: mpsc::UnboundedReceiver<Ctrl>,
    failed: oneshot::Sender<IonError>,
    keep_alive: Option<Duration>,
    mut last_sent: Instant,
) {
    loop {
        let ping_at = deadline_after(last_sent, keep_alive);

        let frame = tokio::select! {
            biased;
            command = ctrl.recv() => {
                match command {
                    Some(Ctrl::ServerGoodbye) => {
                        let _ = sink.send(close_message()).await;
                    }
                    Some(Ctrl::Leave) => say_goodbye(&mut sink).await,
                    None => {} // the reader is gone, and with it the connection
                }
                return;
            }
            // Queued input goes out before a goodbye queued behind it.
            frame = next_input(&mut input) => match frame {
                Some(frame) => frame,
                None => {
                    input = None;
                    // Every input sender is gone. Either `close_input` ended the input and the
                    // call lives on — the server is owed END — or the whole stream was dropped.
                    if let Err(oneshot::error::TryRecvError::Empty) = life.try_recv() {
                        vec![OP_END]
                    } else {
                        say_goodbye(&mut sink).await;
                        return;
                    }
                }
            },
            _ = &mut life => {
                say_goodbye(&mut sink).await;
                return;
            }
            _ = sleep_until_opt(ping_at) => vec![OP_PING],
        };

        if let Err(e) = sink.send(Message::Binary(frame.into())).await {
            let _ = failed.send(disconnected(
                DisconnectReason::TransportLost,
                format!("Sending on the WebSocket failed: {e}"),
            ));
            return;
        }
        last_sent = Instant::now();
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonWsStream — server streaming
// ═══════════════════════════════════════════════════════════════════

/// The items of a `stream` call.
///
/// How the stream ends says how the call ended: `None` after the server completed it,
/// `Err(IonError::Protocol)` when it failed, [`IonError::StreamClosed`] when the server closed it
/// on purpose, and [`IonError::StreamDisconnected`] when the transport died, went silent or broke
/// the protocol.
///
/// Dropping it leaves gracefully: CLOSE, then the WebSocket close handshake, so the server sees a
/// client that left rather than a dropped connection. [`close`](Self::close) does the same and
/// waits for it to finish.
pub struct IonWsStream<T: IonFormat> {
    rx: mpsc::Receiver<Result<T, IonError>>,
    conn: StreamConnection,
}

impl<T: IonFormat + Send + 'static> IonWsStream<T> {
    /// Opens a server-streaming call. `payload` is the CBOR-encoded argument array.
    ///
    /// Returns once the server accepted the call (READY); a refusal is the `Err`.
    pub async fn open(
        ctx: &IonClientContext,
        interface_name: &str,
        method_name: &str,
        payload: &[u8],
    ) -> Result<Self, IonError> {
        let (rx, conn, _) = open_call::<T>(ctx, interface_name, method_name, payload, false).await?;
        Ok(Self { rx, conn })
    }
}

impl<T: IonFormat> IonWsStream<T> {
    /// The server's id for this connection, from READY.
    pub fn connection_id(&self) -> &str {
        &self.conn.connection_id
    }

    /// Leaves gracefully — CLOSE, then the WebSocket close handshake — and waits for it to
    /// finish, up to the close timeout.
    pub async fn close(self) {
        let IonWsStream { rx, conn } = self;
        drop(rx);
        conn.close().await;
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

/// A `stream` call that also takes an input stream: read the output as a `Stream`, send input
/// with [`send`](Self::send), and end the input with [`close_input`](Self::close_input).
///
/// Dropping it leaves gracefully, like [`IonWsStream`].
pub struct IonWsDuplexStream<TIn: IonFormat, TOut: IonFormat> {
    // Declared before `input`, so that it is dropped first: the call's end must not look like
    // the end of its input.
    output: IonWsStream<TOut>,
    input: mpsc::Sender<Vec<u8>>,
    _input: PhantomData<fn(TIn)>,
}

impl<TIn: IonFormat + Send + 'static, TOut: IonFormat + Send + 'static> IonWsDuplexStream<TIn, TOut> {
    /// Opens a bidirectional streaming call. `payload` is the CBOR-encoded argument array.
    ///
    /// Returns once the server accepted the call (READY); a refusal is the `Err`.
    pub async fn open(
        ctx: &IonClientContext,
        interface_name: &str,
        method_name: &str,
        payload: &[u8],
    ) -> Result<Self, IonError> {
        let (rx, conn, input) = open_call::<TOut>(ctx, interface_name, method_name, payload, true).await?;
        let input = input.expect("open_call creates an input channel when asked for one");
        Ok(Self {
            output: IonWsStream { rx, conn },
            input,
            _input: PhantomData,
        })
    }
}

impl<TIn: IonFormat, TOut: IonFormat> IonWsDuplexStream<TIn, TOut> {
    /// Sends an item to the server's input stream, waiting while the input queue is full.
    ///
    /// Fails once the call has ended; how it ended is on the output stream.
    pub async fn send(&self, item: TIn) -> Result<(), IonError> {
        let frame = encode_input(&item)?;
        self.input
            .send(frame)
            .await
            .map_err(|_| IonError::Encode("Input stream closed: the stream call has ended".into()))
    }

    /// Ends the input stream: END goes out after the items already sent. The output keeps
    /// streaming through the returned [`IonWsStream`].
    pub fn close_input(self) -> IonWsStream<TOut> {
        let IonWsDuplexStream { output, input, .. } = self;
        drop(input);
        output
    }

    /// The server's id for this connection, from READY.
    pub fn connection_id(&self) -> &str {
        self.output.connection_id()
    }

    /// Leaves gracefully — CLOSE, then the WebSocket close handshake — and waits for it to
    /// finish, up to the close timeout.
    pub async fn close(self) {
        let IonWsDuplexStream { output, input, .. } = self;
        let IonWsStream { rx, mut conn } = output;
        drop(rx);
        // The goodbye first: dropping `input` alone reads as the end of the input.
        drop(conn.life.take());
        drop(input);
        conn.close().await;
    }
}

impl<TIn: IonFormat, TOut: IonFormat> Stream for IonWsDuplexStream<TIn, TOut> {
    type Item = Result<TOut, IonError>;

    fn poll_next(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        Pin::new(&mut self.output).poll_next(cx)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sub_protocol_names_version_2_only() {
        assert_eq!(sub_protocol("abc"), "ion!ticket#abc!ver#2");
        assert_eq!(sub_protocol(""), "ion!ver#2");
    }

    #[test]
    fn client_close_frame_is_close_of_null() {
        let mut d = Decoder::new(&CLIENT_CLOSE_FRAME[1..]);
        assert_eq!(CLIENT_CLOSE_FRAME[0], OP_CLOSE);
        assert_eq!(d.array().unwrap(), Some(1));
        assert_eq!(d.datatype().unwrap(), minicbor::data::Type::Null);
    }

    #[test]
    fn ready_skips_trailing_elements() {
        let mut e = minicbor::Encoder::new(Vec::new());
        e.array(5).unwrap().str("c1").unwrap().u64(100).unwrap().u64(0).unwrap();
        e.array(2).unwrap().u8(1).unwrap().str("x").unwrap();
        e.bool(true).unwrap();
        let ready = read_ready(&e.into_writer()).unwrap();
        assert_eq!(ready.connection_id, "c1");
        assert_eq!(ready.keep_alive, Some(Duration::from_millis(100)));
        assert_eq!(ready.client_timeout, None);
    }

    #[test]
    fn ready_with_fewer_than_three_elements_is_rejected() {
        let mut e = minicbor::Encoder::new(Vec::new());
        e.array(2).unwrap().str("c1").unwrap().u64(100).unwrap();
        assert!(read_ready(&e.into_writer()).is_err());
    }

    #[test]
    fn close_payload_forms() {
        assert_eq!(read_close(&[]).unwrap(), (None, false));

        let mut e = minicbor::Encoder::new(Vec::new());
        e.array(2).unwrap().null().unwrap().bool(true).unwrap();
        assert_eq!(read_close(&e.into_writer()).unwrap(), (None, true));

        let mut e = minicbor::Encoder::new(Vec::new());
        e.array(3).unwrap().str("bye").unwrap().bool(false).unwrap().u8(7).unwrap();
        assert_eq!(read_close(&e.into_writer()).unwrap(), (Some("bye".into()), false));
    }

    #[test]
    fn timings_adopt_the_announced_values() {
        let options = IonStreamOptions::default();
        let ready = Ready {
            connection_id: String::new(),
            keep_alive: Some(Duration::from_secs(20)),
            client_timeout: Some(Duration::from_secs(10)),
        };
        let timings = Timings::adopt(&options, &ready);
        // min(15 s, 10 s / 2) and max(30 s, 2 × 20 s)
        assert_eq!(timings.keep_alive, Some(Duration::from_secs(5)));
        assert_eq!(timings.server_timeout, Some(Duration::from_secs(40)));

        let silent = Ready { connection_id: String::new(), keep_alive: None, client_timeout: None };
        let timings = Timings::adopt(&options, &silent);
        assert_eq!(timings.keep_alive, Some(Duration::from_secs(15)));
        assert_eq!(timings.server_timeout, Some(Duration::from_secs(30)));

        let disabled = IonStreamOptions {
            keep_alive_interval: Duration::ZERO,
            server_timeout: Duration::ZERO,
            ..IonStreamOptions::default()
        };
        let timings = Timings::adopt(&disabled, &ready);
        assert_eq!(timings.keep_alive, Some(Duration::from_secs(5)));
        assert_eq!(timings.server_timeout, None);
    }

    #[test]
    fn ticket_response_is_array_1_of_bytes() {
        let mut e = minicbor::Encoder::new(Vec::new());
        e.array(1).unwrap().bytes(&[1, 2, 3]).unwrap();
        assert_eq!(read_ticket(&e.into_writer()).unwrap(), vec![1, 2, 3]);
        assert_eq!(base56_encode(&[1, 2, 3]), "P5V");

        let mut e = minicbor::Encoder::new(Vec::new());
        e.array(0).unwrap();
        assert!(read_ticket(&e.into_writer()).is_err(), "an empty array carries no ticket");
    }

    #[test]
    fn input_frame_is_data_of_array_1() {
        let frame = encode_input(&7u32).unwrap();
        assert_eq!(frame, vec![OP_DATA, 0x81, 0x07]);
    }
}
