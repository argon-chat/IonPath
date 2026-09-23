//! Ion stream protocol v2 against an in-process WebSocket server.
//!
//! The server side is scripted per test with raw frames, so every assertion is about bytes on the
//! wire. The ticket exchange never leaves the process: an interceptor answers `ion.att` itself.
//! Timings come from READY or from small client options, so the whole suite runs in about a
//! second.

use std::future::Future;
use std::time::{Duration, Instant};

use futures_util::{SinkExt, StreamExt};
use ion_rustcore::{
    DisconnectReason, IonCallContext, IonClient, IonClientContext, IonError, IonInterceptor, IonNext,
    IonStreamOptions, IonWsDuplexStream, IonWsStream,
};
use minicbor::Encoder;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio_tungstenite::tungstenite::handshake::server::{Request, Response};
use tokio_tungstenite::tungstenite::http::HeaderValue;
use tokio_tungstenite::tungstenite::protocol::frame::coding::CloseCode;
use tokio_tungstenite::tungstenite::protocol::CloseFrame;
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::{accept_hdr_async, WebSocketStream};

// ═══════════════════════════════════════════════════════════════════
// Wire helpers
// ═══════════════════════════════════════════════════════════════════

const DATA: u8 = 0x00;
const END: u8 = 0x01;
const ERROR: u8 = 0x02;
const PING: u8 = 0x03;
const CLOSE: u8 = 0x04;
const READY: u8 = 0x05;

/// The argument array the client sends: `[5]`.
const ARGS: [u8; 2] = [0x81, 0x05];

/// CLOSE `[null]`, the client's goodbye.
const CLIENT_CLOSE: [u8; 3] = [CLOSE, 0x81, 0xf6];

/// base56 of the ticket bytes `01 02 03` the stub exchange hands out.
const EXPECTED_SUB_PROTOCOL: &str = "ion!ticket#P5V!ver#2";

fn frame(opcode: u8, build: impl FnOnce(&mut Encoder<Vec<u8>>)) -> Vec<u8> {
    let mut e = Encoder::new(vec![opcode]);
    build(&mut e);
    e.into_writer()
}

fn ready(id: &str, keep_alive_ms: u64, client_timeout_ms: u64) -> Vec<u8> {
    frame(READY, |e| {
        e.array(3).unwrap().str(id).unwrap().u64(keep_alive_ms).unwrap().u64(client_timeout_ms).unwrap();
    })
}

fn data(item: u32) -> Vec<u8> {
    frame(DATA, |e| {
        e.u32(item).unwrap();
    })
}

fn error(code: &str, msg: &str) -> Vec<u8> {
    frame(ERROR, |e| {
        e.array(2).unwrap().str(code).unwrap().str(msg).unwrap();
    })
}

fn server_close(reason: Option<&str>, allow_reconnect: bool) -> Vec<u8> {
    frame(CLOSE, |e| {
        e.array(2).unwrap();
        match reason {
            Some(reason) => e.str(reason).unwrap(),
            None => e.null().unwrap(),
        };
        e.bool(allow_reconnect).unwrap();
    })
}

fn end() -> Vec<u8> {
    vec![END]
}

// ═══════════════════════════════════════════════════════════════════
// Client side
// ═══════════════════════════════════════════════════════════════════

/// Answers the ticket exchange in-process: `array(1)[bytes 01 02 03]`.
struct TicketStub;

#[async_trait::async_trait]
impl IonInterceptor for TicketStub {
    async fn invoke(&self, ctx: &mut IonCallContext, next: &dyn IonNext) -> Result<(), IonError> {
        if ctx.interface_name != "ion.att" {
            return next.invoke(ctx).await;
        }

        let mut e = Encoder::new(Vec::new());
        e.array(1)?.bytes(&[1, 2, 3])?;
        ctx.response_status = Some(200);
        ctx.response_payload = Some(e.into_writer());
        Ok(())
    }
}

fn options() -> IonStreamOptions {
    IonStreamOptions {
        handshake_timeout: Duration::from_secs(5),
        close_timeout: Duration::from_secs(2),
        ..IonStreamOptions::default()
    }
}

fn client(base_url: &str, options: IonStreamOptions) -> IonClientContext {
    IonClient::new(base_url).with_interceptor(TicketStub).with_stream_options(options).build()
}

async fn open(base_url: &str, options: IonStreamOptions) -> Result<IonWsStream<u32>, IonError> {
    IonWsStream::<u32>::open(&client(base_url, options), "IFoo", "bar", &ARGS).await
}

async fn open_duplex(base_url: &str) -> IonWsDuplexStream<u32, u32> {
    match IonWsDuplexStream::<u32, u32>::open(&client(base_url, options()), "IFoo", "chat", &ARGS).await {
        Ok(stream) => stream,
        Err(e) => panic!("open failed: {e}"),
    }
}

/// Fails the test instead of hanging it.
async fn bounded<F: Future>(future: F) -> F::Output {
    tokio::time::timeout(Duration::from_secs(10), future).await.expect("test timed out")
}

fn items(results: Vec<Result<u32, IonError>>) -> (Vec<u32>, Option<IonError>) {
    let mut items = Vec::new();
    let mut failure = None;
    for result in results {
        match result {
            Ok(item) => items.push(item),
            Err(e) => {
                assert!(failure.is_none(), "a second error after {failure:?}: {e}");
                failure = Some(e);
            }
        }
    }
    (items, failure)
}

// ═══════════════════════════════════════════════════════════════════
// Server side
// ═══════════════════════════════════════════════════════════════════

/// What the server saw from the client.
#[derive(Debug, PartialEq)]
enum Event {
    Frame(Vec<u8>),
    /// A WebSocket close frame, with its code.
    Close(Option<u16>),
    /// The connection ended after the close handshake.
    Ended,
    /// The connection ended without one — a reset or a bare EOF.
    Failed(String),
}

struct MockServer {
    listener: TcpListener,
}

async fn start() -> (MockServer, String) {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let base_url = format!("http://{}", listener.local_addr().unwrap());
    (MockServer { listener }, base_url)
}

impl MockServer {
    /// Accepts one WebSocket, echoing the first offered sub-protocol as a real server does.
    async fn accept(&self) -> Peer {
        let (tcp, _) = self.listener.accept().await.unwrap();
        let mut offered = Vec::new();
        let mut path = String::new();

        let ws = accept_hdr_async(tcp, |request: &Request, mut response: Response| {
            path = request.uri().path().to_owned();
            for value in request.headers().get_all("Sec-WebSocket-Protocol") {
                offered.extend(value.to_str().unwrap().split(',').map(|s| s.trim().to_owned()));
            }
            if let Some(first) = offered.first() {
                response.headers_mut().insert("Sec-WebSocket-Protocol", HeaderValue::from_str(first).unwrap());
            }
            Ok(response)
        })
        .await
        .unwrap();

        Peer { ws, offered, path }
    }

    /// Accepts one connection and refuses the upgrade the way the Ion endpoint does: an HTTP
    /// status with a CBOR `IonProtocolError` body.
    async fn refuse(&self, status: &str, code: &str, msg: &str) {
        let (mut tcp, _) = self.listener.accept().await.unwrap();
        let mut head = Vec::new();
        let mut buf = [0u8; 1024];
        while !head.windows(4).any(|w| w == b"\r\n\r\n") {
            let n = tcp.read(&mut buf).await.unwrap();
            assert!(n > 0, "the client hung up mid-request");
            head.extend_from_slice(&buf[..n]);
        }

        let body = error(code, msg)[1..].to_vec();
        let mut response = format!(
            "HTTP/1.1 {status}\r\nContent-Type: application/ion\r\nX-Ion-Status: {code}\r\nContent-Length: {}\r\n\r\n",
            body.len()
        )
        .into_bytes();
        response.extend_from_slice(&body);
        tcp.write_all(&response).await.unwrap();
        tcp.flush().await.unwrap();
        let _ = tcp.read(&mut buf).await; // let the client hang up first
    }

    /// Answers one bodiless HTTP request — the ticket exchange — and returns its head, lower-cased.
    async fn answer_http(&self, status: &str, content_type: &str, body: &[u8]) -> String {
        let (mut tcp, _) = self.listener.accept().await.unwrap();
        let mut head = Vec::new();
        let mut buf = [0u8; 1024];
        while !head.windows(4).any(|w| w == b"\r\n\r\n") {
            let n = tcp.read(&mut buf).await.unwrap();
            assert!(n > 0, "the client hung up mid-request");
            head.extend_from_slice(&buf[..n]);
        }

        let mut response = format!(
            "HTTP/1.1 {status}\r\nContent-Type: {content_type}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
            body.len()
        )
        .into_bytes();
        response.extend_from_slice(body);
        tcp.write_all(&response).await.unwrap();
        tcp.flush().await.unwrap();

        String::from_utf8_lossy(&head).to_lowercase()
    }
}

struct Peer {
    ws: WebSocketStream<TcpStream>,
    offered: Vec<String>,
    path: String,
}

impl Peer {
    async fn send(&mut self, frame: Vec<u8>) {
        self.ws.send(Message::Binary(frame.into())).await.unwrap();
    }

    async fn next_event(&mut self) -> Event {
        loop {
            return match self.ws.next().await {
                Some(Ok(Message::Binary(bytes))) => Event::Frame(bytes.to_vec()),
                Some(Ok(Message::Close(frame))) => Event::Close(frame.map(|f| u16::from(f.code))),
                Some(Ok(Message::Ping(_) | Message::Pong(_))) => continue,
                Some(Ok(other)) => panic!("unexpected message from the client: {other:?}"),
                Some(Err(e)) => Event::Failed(e.to_string()),
                None => Event::Ended,
            };
        }
    }

    async fn expect_frame(&mut self) -> Vec<u8> {
        match self.next_event().await {
            Event::Frame(frame) => frame,
            other => panic!("expected a frame, got {other:?}"),
        }
    }

    /// Everything the client sends from now until the connection ends.
    async fn rest(&mut self) -> Vec<Event> {
        let mut events = Vec::new();
        loop {
            let event = self.next_event().await;
            let done = matches!(event, Event::Ended | Event::Failed(_));
            events.push(event);
            if done {
                return events;
            }
        }
    }

    /// The real server's goodbye: a final frame, then the WebSocket close. Returns what the client
    /// sent afterwards.
    async fn goodbye(&mut self, final_frame: Vec<u8>) -> Vec<Event> {
        self.send(final_frame).await;
        self.ws
            .close(Some(CloseFrame { code: CloseCode::Normal, reason: "completed".into() }))
            .await
            .unwrap();
        self.rest().await
    }
}

/// After the server's goodbye the client owes a WebSocket close and nothing else: no CLOSE, no
/// PING, and certainly no reset.
fn assert_answered_goodbye(events: &[Event]) {
    assert!(
        events.iter().all(|e| !matches!(e, Event::Frame(_))),
        "the client sent frames after the server's goodbye: {events:?}"
    );
    assert!(events.contains(&Event::Close(Some(1000))), "no WebSocket close from the client: {events:?}");
    assert_eq!(events.last(), Some(&Event::Ended), "the close handshake did not complete: {events:?}");
}

/// The client's own goodbye: CLOSE `[null]`, then the WebSocket close, then a clean end.
fn assert_client_goodbye(events: &[Event]) {
    assert_eq!(
        events,
        &[Event::Frame(CLIENT_CLOSE.to_vec()), Event::Close(Some(1000)), Event::Ended],
        "expected CLOSE [null], then the WebSocket close handshake"
    );
}

fn assert_disconnected(error: &IonError, expected: DisconnectReason) {
    match error {
        IonError::StreamDisconnected { reason, .. } => assert_eq!(*reason, expected, "{error}"),
        other => panic!("expected StreamDisconnected({expected}), got {other}"),
    }
}

// ═══════════════════════════════════════════════════════════════════
// Negotiation and handshake
// ═══════════════════════════════════════════════════════════════════

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn offers_only_ver_2_and_sends_raw_arguments_first() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            let args = peer.expect_frame().await;
            peer.send(ready("conn-1", 0, 0)).await;
            let after = peer.goodbye(end()).await;
            (peer.offered, peer.path, args, after)
        });

        let stream = open(&base_url, options()).await.unwrap();
        assert_eq!(stream.connection_id(), "conn-1");
        let results: Vec<_> = stream.collect().await;
        assert!(results.is_empty(), "expected no items");

        let (offered, path, args, after) = server.await.unwrap();
        assert_eq!(offered, vec![EXPECTED_SUB_PROTOCOL.to_owned()], "exactly one sub-protocol, v2");
        assert_eq!(path, "/ion/IFoo/bar.ws");
        assert_eq!(args, ARGS, "the first message is the argument array, without an opcode");
        assert_answered_goodbye(&after);
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn error_before_ready_fails_open() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.goodbye(error("TICKET_REFUSED", "no such ticket")).await
        });

        match open(&base_url, options()).await {
            Err(IonError::Protocol(e)) => {
                assert_eq!(e.code, "TICKET_REFUSED");
                assert_eq!(e.msg, "no such ticket");
            }
            Err(other) => panic!("expected IonError::Protocol, got {other}"),
            Ok(_) => panic!("open succeeded although the server refused the call"),
        }

        assert_answered_goodbye(&server.await.unwrap());
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn close_before_ready_fails_open_with_stream_closed() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.goodbye(server_close(Some("draining"), true)).await
        });

        match open(&base_url, options()).await {
            Err(IonError::StreamClosed { reason, allow_reconnect }) => {
                assert_eq!(reason.as_deref(), Some("draining"));
                assert!(allow_reconnect);
            }
            Err(other) => panic!("expected IonError::StreamClosed, got {other}"),
            Ok(_) => panic!("open succeeded although the server closed the call"),
        }

        assert_answered_goodbye(&server.await.unwrap());
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn no_ready_within_the_handshake_timeout_fails_open_and_leaves_gracefully() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.rest().await // never READY
        });

        let options = IonStreamOptions { handshake_timeout: Duration::from_millis(150), ..options() };
        let started = Instant::now();
        match open(&base_url, options).await {
            Err(e) => assert_disconnected(&e, DisconnectReason::Timeout),
            Ok(_) => panic!("open succeeded without READY"),
        }
        assert!(started.elapsed() >= Duration::from_millis(140));

        assert_client_goodbye(&server.await.unwrap());
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn refused_upgrade_surfaces_the_servers_error() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            server.refuse("426 Upgrade Required", "UNSUPPORTED_PROTOCOL_VERSION", "v2 only").await;
        });

        match open(&base_url, options()).await {
            Err(IonError::Protocol(e)) => assert_eq!(e.code, "UNSUPPORTED_PROTOCOL_VERSION"),
            Err(other) => panic!("expected IonError::Protocol, got {other}"),
            Ok(_) => panic!("open succeeded against a refused upgrade"),
        }

        server.await.unwrap();
    })
    .await;
}

// ═══════════════════════════════════════════════════════════════════
// The ticket exchange over HTTP
// ═══════════════════════════════════════════════════════════════════

/// Adds a request header, as an authentication interceptor would, and lets the call through.
struct AddHeader(&'static str, &'static str);

#[async_trait::async_trait]
impl IonInterceptor for AddHeader {
    async fn invoke(&self, ctx: &mut IonCallContext, next: &dyn IonNext) -> Result<(), IonError> {
        ctx.request_headers.insert(self.0, reqwest::header::HeaderValue::from_static(self.1));
        next.invoke(ctx).await
    }
}

/// A client whose ticket exchange really goes over HTTP, through `AddHeader` and an HTTP client
/// that marks its requests with a default header.
fn client_over_http(base_url: &str) -> IonClientContext {
    let mut defaults = reqwest::header::HeaderMap::new();
    defaults.insert("x-from-context-client", reqwest::header::HeaderValue::from_static("yes"));
    let http = reqwest::Client::builder().default_headers(defaults).build().unwrap();

    IonClient::new(base_url)
        .with_interceptor(AddHeader("x-lab-user", "alice"))
        .with_http_client(http)
        .with_stream_options(options())
        .build()
}

fn ticket_body(ticket: &[u8]) -> Vec<u8> {
    let mut e = Encoder::new(Vec::new());
    e.array(1).unwrap().bytes(ticket).unwrap();
    e.into_writer()
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn ticket_exchange_carries_the_interceptors_headers_on_the_contexts_client() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let exchange = server.answer_http("200 OK", "application/ion", &ticket_body(&[1, 2, 3])).await;
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.send(ready("c", 0, 0)).await;
            let after = peer.goodbye(end()).await;
            (exchange, peer.offered, after)
        });

        let stream = IonWsStream::<u32>::open(&client_over_http(&base_url), "IFoo", "bar", &ARGS).await.unwrap();
        let results: Vec<_> = stream.collect().await;
        assert!(results.is_empty(), "expected no items");

        let (exchange, offered, after) = server.await.unwrap();
        assert!(exchange.starts_with("post /ion.att http/1.1\r\n"), "{exchange}");
        assert!(exchange.contains("\r\ncontent-type: application/ion\r\n"), "{exchange}");
        assert!(exchange.contains("\r\nx-ion-session-id: "), "{exchange}");
        assert!(exchange.contains("\r\nx-lab-user: alice\r\n"), "the interceptor's header is missing: {exchange}");
        assert!(
            exchange.contains("\r\nx-from-context-client: yes\r\n"),
            "the exchange did not use the context's HTTP client: {exchange}"
        );
        assert_eq!(offered, vec![EXPECTED_SUB_PROTOCOL.to_owned()]);
        assert_answered_goodbye(&after);
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn refused_ticket_exchange_fails_open_with_the_servers_error() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let body = error("TICKET_REFUSED", "not you")[1..].to_vec();
            server.answer_http("401 Unauthorized", "application/ion", &body).await;
        });

        match IonWsStream::<u32>::open(&client_over_http(&base_url), "IFoo", "bar", &ARGS).await {
            Err(IonError::Protocol(e)) => {
                assert_eq!(e.code, "TICKET_REFUSED");
                assert_eq!(e.msg, "not you");
            }
            Err(other) => panic!("expected IonError::Protocol, got {other}"),
            Ok(_) => panic!("open succeeded after a refused ticket exchange"),
        }

        server.await.unwrap();
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn failed_ticket_exchange_without_an_ion_body_is_an_upstream_error() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            server.answer_http("502 Bad Gateway", "text/plain", b"bad gateway").await;
        });

        match IonWsStream::<u32>::open(&client_over_http(&base_url), "IFoo", "bar", &ARGS).await {
            Err(IonError::Protocol(e)) => {
                assert_eq!(e.code, "UPSTREAM_ERROR");
                assert!(e.msg.contains("502"), "{e}");
            }
            Err(other) => panic!("expected IonError::Protocol, got {other}"),
            Ok(_) => panic!("open succeeded after a failed ticket exchange"),
        }

        server.await.unwrap();
    })
    .await;
}

// ═══════════════════════════════════════════════════════════════════
// How a call ends
// ═══════════════════════════════════════════════════════════════════

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn ready_data_end_yields_every_item_then_ends() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.send(ready("conn-2", 0, 0)).await;
            for item in [1, 2, 3] {
                peer.send(data(item)).await;
            }
            peer.goodbye(end()).await
        });

        let stream = open(&base_url, options()).await.unwrap();
        let (items, failure) = items(stream.collect().await);
        assert_eq!(items, vec![1, 2, 3]);
        assert!(failure.is_none(), "END must end the stream cleanly, got {failure:?}");

        assert_answered_goodbye(&server.await.unwrap());
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn error_after_ready_ends_the_stream_with_the_protocol_error() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.send(ready("c", 0, 0)).await;
            peer.send(data(1)).await;
            peer.goodbye(error("BOOM", "the method threw")).await
        });

        let stream = open(&base_url, options()).await.unwrap();
        let (items, failure) = items(stream.collect().await);
        assert_eq!(items, vec![1]);
        match failure {
            Some(IonError::Protocol(e)) => assert_eq!(e.code, "BOOM"),
            other => panic!("expected IonError::Protocol, got {other:?}"),
        }

        assert_answered_goodbye(&server.await.unwrap());
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn close_after_ready_ends_the_stream_with_stream_closed() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.send(ready("c", 0, 0)).await;
            peer.send(data(1)).await;
            peer.goodbye(server_close(Some("maintenance"), true)).await
        });

        let stream = open(&base_url, options()).await.unwrap();
        let (items, failure) = items(stream.collect().await);
        assert_eq!(items, vec![1]);
        match failure {
            Some(IonError::StreamClosed { reason, allow_reconnect }) => {
                assert_eq!(reason.as_deref(), Some("maintenance"));
                assert!(allow_reconnect);
            }
            other => panic!("expected IonError::StreamClosed, got {other:?}"),
        }

        assert_answered_goodbye(&server.await.unwrap());
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn server_dropping_the_connection_is_transport_lost() {
    bounded(async {
        let (server, base_url) = start().await;
        let (dropped_tx, dropped_rx) = tokio::sync::oneshot::channel::<()>();
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.send(ready("c", 0, 0)).await;
            peer.send(data(1)).await;
            drop(peer); // no END, no CLOSE, no close handshake
            let _ = dropped_tx.send(());
        });

        let stream = open(&base_url, options()).await.unwrap();
        dropped_rx.await.unwrap();
        let (items, failure) = items(stream.collect().await);
        assert_eq!(items, vec![1]);
        assert_disconnected(&failure.expect("the stream must fail"), DisconnectReason::TransportLost);

        server.await.unwrap();
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn websocket_close_without_a_goodbye_is_transport_lost() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.send(ready("c", 0, 0)).await;
            peer.ws
                .close(Some(CloseFrame { code: CloseCode::Away, reason: "restarting".into() }))
                .await
                .unwrap();
            peer.rest().await
        });

        let stream = open(&base_url, options()).await.unwrap();
        let (items, failure) = items(stream.collect().await);
        assert!(items.is_empty());
        let failure = failure.expect("the stream must fail");
        assert_disconnected(&failure, DisconnectReason::TransportLost);
        assert!(failure.to_string().contains("1001"), "the close code belongs in the message: {failure}");

        let after = server.await.unwrap();
        assert_eq!(after.last(), Some(&Event::Ended), "the close handshake did not complete: {after:?}");
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn unknown_opcode_is_a_protocol_violation() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.send(ready("c", 0, 0)).await;
            peer.send(vec![0x7f]).await;
            peer.rest().await
        });

        let stream = open(&base_url, options()).await.unwrap();
        let (_, failure) = items(stream.collect().await);
        assert_disconnected(&failure.expect("the stream must fail"), DisconnectReason::ProtocolViolation);

        server.await.unwrap();
    })
    .await;
}

// ═══════════════════════════════════════════════════════════════════
// Heartbeat
// ═══════════════════════════════════════════════════════════════════

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn server_silence_after_ready_times_out() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.send(ready("c", 0, 0)).await;
            peer.rest().await // silent from here on
        });

        let options = IonStreamOptions { server_timeout: Duration::from_millis(150), ..options() };
        let mut stream = open(&base_url, options).await.unwrap();
        let started = Instant::now();
        let failure = stream.next().await.expect("an error, not the end").expect_err("an error, not an item");
        let elapsed = started.elapsed();
        assert_disconnected(&failure, DisconnectReason::Timeout);
        assert!(elapsed >= Duration::from_millis(140), "timed out early, after {elapsed:?}");
        assert!(elapsed < Duration::from_secs(2), "timed out late, after {elapsed:?}");
        assert!(stream.next().await.is_none());

        // A dead server gets no goodbye: the connection is dropped.
        let after = server.await.unwrap();
        assert!(
            matches!(after.as_slice(), [Event::Failed(_)]),
            "a timed-out connection is aborted, not closed: {after:?}"
        );
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn server_timeout_is_at_least_twice_the_announced_keep_alive() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.send(ready("c", 200, 0)).await; // the server pings every 200 ms (but doesn't)
            peer.rest().await
        });

        // 50 ms from the options, raised to 2 × 200 ms by READY.
        let options = IonStreamOptions { server_timeout: Duration::from_millis(50), ..options() };
        let mut stream = open(&base_url, options).await.unwrap();
        let started = Instant::now();
        let failure = stream.next().await.expect("an error").expect_err("an error");
        let elapsed = started.elapsed();
        assert_disconnected(&failure, DisconnectReason::Timeout);
        assert!(elapsed >= Duration::from_millis(350), "READY's keep-alive was ignored: {elapsed:?}");
        assert!(elapsed < Duration::from_secs(3), "timed out late, after {elapsed:?}");

        server.await.unwrap();
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn client_pings_while_idle_at_half_the_announced_client_timeout() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            // The server drops clients silent for 100 ms, so the client pings every 50 ms.
            peer.send(ready("c", 0, 100)).await;

            let until = tokio::time::Instant::now() + Duration::from_millis(400);
            let mut frames = Vec::new();
            while let Ok(event) = tokio::time::timeout_at(until, peer.next_event()).await {
                match event {
                    Event::Frame(frame) => frames.push(frame),
                    other => panic!("the client went away while idle: {other:?}"),
                }
            }
            peer.send(end()).await;
            (frames, peer.rest().await)
        });

        let stream = open(&base_url, options()).await.unwrap();
        let (items, failure) = items(stream.collect().await);
        assert!(items.is_empty());
        assert!(failure.is_none(), "{failure:?}");

        let (frames, _) = server.await.unwrap();
        assert!(frames.iter().all(|f| f == &[PING]), "only PINGs while idle: {frames:?}");
        assert!(frames.len() >= 3, "expected a PING about every 50 ms over 400 ms, got {}", frames.len());
        assert!(frames.len() <= 10, "PINGs faster than the keep-alive: {}", frames.len());
    })
    .await;
}

// ═══════════════════════════════════════════════════════════════════
// Leaving
// ═══════════════════════════════════════════════════════════════════

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn dropping_the_stream_sends_close_then_the_websocket_close() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.send(ready("c", 0, 0)).await;
            peer.send(data(1)).await;
            peer.rest().await
        });

        let mut stream = open(&base_url, options()).await.unwrap();
        assert_eq!(stream.next().await.unwrap().unwrap(), 1);
        drop(stream);

        assert_client_goodbye(&server.await.unwrap());
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn close_sends_close_and_waits_for_the_handshake() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.send(ready("c", 0, 0)).await;
            peer.rest().await
        });

        let stream = open(&base_url, options()).await.unwrap();
        let started = Instant::now();
        stream.close().await;
        assert!(started.elapsed() < Duration::from_secs(1), "close waited for its timeout");

        assert_client_goodbye(&server.await.unwrap());
    })
    .await;
}

// ═══════════════════════════════════════════════════════════════════
// Duplex
// ═══════════════════════════════════════════════════════════════════

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn duplex_sends_array_1_data_frames_then_end_on_close_input() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            let args = peer.expect_frame().await;
            peer.send(ready("duplex-1", 0, 0)).await;

            let mut input = Vec::new();
            loop {
                let frame = peer.expect_frame().await;
                let done = frame == [END];
                input.push(frame);
                if done {
                    break;
                }
            }

            peer.send(data(30)).await;
            let after = peer.goodbye(end()).await;
            (peer.path, args, input, after)
        });

        let duplex = open_duplex(&base_url).await;
        assert_eq!(duplex.connection_id(), "duplex-1");
        duplex.send(10).await.unwrap();
        duplex.send(20).await.unwrap();
        let output = duplex.close_input();
        let (items, failure) = items(output.collect().await);
        assert_eq!(items, vec![30]);
        assert!(failure.is_none(), "{failure:?}");

        let (path, args, input, after) = server.await.unwrap();
        assert_eq!(path, "/ion/IFoo/chat.ws");
        assert_eq!(args, ARGS);
        assert_eq!(
            input,
            vec![vec![DATA, 0x81, 10], vec![DATA, 0x81, 20], vec![END]],
            "each item as DATA array(1)[item], then END"
        );
        assert_answered_goodbye(&after);
    })
    .await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn dropping_a_duplex_stream_sends_close_not_end() {
    bounded(async {
        let (server, base_url) = start().await;
        let server = tokio::spawn(async move {
            let mut peer = server.accept().await;
            peer.expect_frame().await;
            peer.send(ready("c", 0, 0)).await;
            let first = peer.expect_frame().await;
            (first, peer.rest().await)
        });

        let duplex = open_duplex(&base_url).await;
        duplex.send(10).await.unwrap();
        drop(duplex);

        let (first, after) = server.await.unwrap();
        assert_eq!(first, vec![DATA, 0x81, 10]);
        assert_client_goodbye(&after);
    })
    .await;
}
