//! One test per scenario of `ForeignClientInteropTests` — see its doc comment for the contract.
//! Each asserts what the client saw; the driver asserts what the server saw (the disconnect
//! reason, one connection per user, each hook called once).
//!
//! "Stops" means dropping the stream, which sends CLOSE from the background; "cancelled" means
//! `close().await`.

use std::time::Instant;

use futures_util::StreamExt;
use ion_rustcore::{IonError, IonWsStream};
use rust_interop::{blob, bounded, let_the_goodbye_finish, outcome, Interop};
use test_contracts::{Joined, LabEvent, LabSignal, Left, Said, StreamLabClient};

fn lab(env: &Interop, user: &str) -> StreamLabClient {
    env.client(user).service()
}

/// Opens a stream the server is expected to accept.
fn accepted<T>(scenario: &str, opened: Result<T, IonError>) -> T {
    opened.unwrap_or_else(|e| panic!("{scenario}: the server refused the stream: {e}"))
}

/// The next `n` items, each of which must be an item.
async fn take<T: ion_rustcore::IonFormat>(scenario: &str, stream: &mut IonWsStream<T>, n: usize) -> Vec<T> {
    let mut items = Vec::with_capacity(n);
    for i in 0..n {
        match stream.next().await {
            Some(Ok(item)) => items.push(item),
            Some(Err(e)) => panic!("{scenario}: item {i} failed: {e}"),
            None => panic!("{scenario}: the stream ended after {i} of {n} items"),
        }
    }
    items
}

fn assert_protocol_error(scenario: &str, error: Option<IonError>, code: &str) {
    match error {
        Some(IonError::Protocol(e)) => assert_eq!(e.code, code, "{scenario}: {e}"),
        Some(other) => panic!("{scenario}: expected a protocol error {code}, got {other}"),
        None => panic!("{scenario}: expected a protocol error {code}, but the stream ended normally"),
    }
}

/// Count(10, 50, 0): exactly 10..59, then a normal end.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn complete() {
    let Some(env) = Interop::from_env("complete") else { return };
    let lab = lab(&env, &env.user("complete"));

    bounded("complete", async {
        let stream = accepted("complete", lab.count(10, 50, 0).await);
        let (items, failure) = outcome(stream.collect().await);

        if let Some(e) = failure {
            panic!("complete: expected a normal end after {} items, got {e}", items.len());
        }
        assert_eq!(items, (10..60).collect::<Vec<i32>>(), "complete");
    })
    .await;
}

/// Count(0, 1000000, 5): three items, then the client stops early by dropping the stream.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn break_early() {
    let Some(env) = Interop::from_env("break") else { return };
    let lab = lab(&env, &env.user("break"));

    bounded("break", async {
        let mut stream = accepted("break", lab.count(0, 1_000_000, 5).await);
        assert_eq!(take("break", &mut stream, 3).await, vec![0, 1, 2], "break");
        drop(stream);
        let_the_goodbye_finish().await;
    })
    .await;
}

/// Count(0, 1000000, 5): three items, then cancelled with an explicit close.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn abort() {
    let Some(env) = Interop::from_env("abort") else { return };
    let lab = lab(&env, &env.user("abort"));

    bounded("abort", async {
        let mut stream = accepted("abort", lab.count(0, 1_000_000, 5).await);
        assert_eq!(take("abort", &mut stream, 3).await, vec![0, 1, 2], "abort");
        stream.close().await;
    })
    .await;
}

/// Explode(2): items 0 and 1, then an error with code INTERNAL_ERROR.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn error() {
    let Some(env) = Interop::from_env("error") else { return };
    let lab = lab(&env, &env.user("error"));

    bounded("error", async {
        let stream = accepted("error", lab.explode(2).await);
        let (items, failure) = outcome(stream.collect().await);

        assert_eq!(items, vec![0, 1], "error");
        assert_protocol_error("error", failure, "INTERNAL_ERROR");
    })
    .await;
}

/// Count(0, 1, 0) as banned-{run}: refused with code BANNED, and not retried.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn banned() {
    let Some(env) = Interop::from_env("banned") else { return };
    let lab = lab(&env, &format!("banned-{}", env.run));

    bounded("banned", async {
        match lab.count(0, 1, 0).await {
            Ok(_) => panic!("banned: the server accepted a banned user"),
            Err(e) => assert_protocol_error("banned", Some(e), "BANNED"),
        }
    })
    .await;
}

/// Listen("kick-{run}"): the server closes it with reason "kicked", allowReconnect false.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn kicked() {
    let Some(env) = Interop::from_env("kicked") else { return };
    let lab = lab(&env, &env.user("kicked"));

    bounded("kicked", async {
        let stream = accepted("kicked", lab.listen(&format!("kick-{}", env.run)).await);
        let (items, failure) = outcome(stream.collect().await);

        assert!(items.is_empty(), "kicked: no items expected, got {items:?}");
        match failure {
            Some(IonError::StreamClosed { reason, allow_reconnect }) => {
                assert_eq!(reason.as_deref(), Some("kicked"), "kicked: close reason");
                assert!(!allow_reconnect, "kicked: the server said not to reconnect");
            }
            Some(other) => panic!("kicked: expected StreamClosed, got {other}"),
            None => panic!("kicked: expected StreamClosed, but the stream ended normally"),
        }
    })
    .await;
}

/// Listen("push-{run}"): the server pushes LabEvent(1..3, "push-{run}", "p1".."p3"); the client
/// stops after the third.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn push() {
    let Some(env) = Interop::from_env("push") else { return };
    let lab = lab(&env, &env.user("push"));
    let topic = format!("push-{}", env.run);

    bounded("push", async {
        let mut stream = accepted("push", lab.listen(&topic).await);
        let expected: Vec<LabEvent> = (1..=3)
            .map(|i| LabEvent { seq: i, topic: topic.clone(), body: format!("p{i}") })
            .collect();

        assert_eq!(take("push", &mut stream, 3).await, expected, "push");
        drop(stream);
        let_the_goodbye_finish().await;
    })
    .await;
}

/// Signals("{run}"): Joined("ann"), Said("ann", "hi"), Left("ann"); the client stops after the third.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn union() {
    let Some(env) = Interop::from_env("union") else { return };
    let lab = lab(&env, &env.user("union"));

    bounded("union", async {
        let mut stream = accepted("union", lab.signals(&env.run).await);
        let expected = vec![
            LabSignal::Joined(Joined { user: "ann".into() }),
            LabSignal::Said(Said { user: "ann".into(), text: "hi".into() }),
            LabSignal::Left(Left { user: "ann".into() }),
        ];

        assert_eq!(take("union", &mut stream, 3).await, expected, "union");
        drop(stream);
        let_the_goodbye_finish().await;
    })
    .await;
}

/// Echo("a".."j"): "A".."J" in order, then a normal end once the input ends.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn echo() {
    let Some(env) = Interop::from_env("echo") else { return };
    let lab = lab(&env, &env.user("echo"));

    bounded("echo", async {
        let call = accepted("echo", lab.echo().await);
        let letters: Vec<String> = ('a'..='j').map(String::from).collect();
        for letter in &letters {
            call.send(letter.clone())
                .await
                .unwrap_or_else(|e| panic!("echo: sending '{letter}' failed: {e}"));
        }

        let output = call.close_input();
        let (items, failure) = outcome(output.collect().await);

        if let Some(e) = failure {
            panic!("echo: expected a normal end after {items:?}, got {e}");
        }
        let expected: Vec<String> = letters.iter().map(|s| s.to_uppercase()).collect();
        assert_eq!(items, expected, "echo");
    })
    .await;
}

/// Blobs(1048579, 3): three payloads, byte i of payload n = (n + i) mod 251.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn blobs() {
    const SIZE: usize = 1_048_579;
    const COUNT: usize = 3;

    let Some(env) = Interop::from_env("blobs") else { return };
    let lab = lab(&env, &env.user("blobs"));

    bounded("blobs", async {
        let stream = accepted("blobs", lab.blobs(SIZE as i32, COUNT as i32).await);
        let (items, failure) = outcome(stream.collect().await);

        if let Some(e) = failure {
            panic!("blobs: expected a normal end after {} payloads, got {e}", items.len());
        }
        assert_eq!(items.len(), COUNT, "blobs: payload count");
        for (n, payload) in items.iter().enumerate() {
            assert_eq!(payload.len(), SIZE, "blobs: length of payload {n}");
            if let Some(i) = payload.as_slice().iter().zip(blob(SIZE, n)).position(|(a, b)| *a != b) {
                panic!("blobs: payload {n} differs first at byte {i}");
            }
        }
    })
    .await;
}

/// Listen("idle-{run}"): nothing for 4 s — past the server's 1.5 s client timeout, so only the
/// client's heartbeat keeps it up — then "still-here"; the client stops after it.
#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn idle() {
    let Some(env) = Interop::from_env("idle") else { return };
    let lab = lab(&env, &env.user("idle"));
    let topic = format!("idle-{}", env.run);

    bounded("idle", async {
        let mut stream = accepted("idle", lab.listen(&topic).await);
        let opened = Instant::now();

        let event = take("idle", &mut stream, 1).await.remove(0);
        let silence = opened.elapsed();

        assert_eq!(event, LabEvent { seq: 1, topic: topic.clone(), body: "still-here".into() }, "idle");
        assert!(
            silence.as_millis() >= 3_500,
            "idle: the event came after {silence:?}, before the 4 s silence was over"
        );
        drop(stream);
        let_the_goodbye_finish().await;
    })
    .await;
}
