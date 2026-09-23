//! The Rust Ion stream client against the real .NET server.
//!
//! The scenarios are in `tests/interop.rs`. The driver,
//! `src/tests/IonTestClientServer/Streaming/Interop/ForeignClientInteropTests.cs`, starts the
//! server, runs `cargo test` here with `ION_INTEROP_URL` and `ION_INTEROP_RUN` set, plays the
//! server's half of the scenarios while they run, and afterwards asserts what the server saw. Its
//! doc comment is the scenario contract.
//!
//! Without `ION_INTEROP_URL` every scenario passes without doing anything, so a plain
//! `cargo test` here is harmless.

use std::future::Future;
use std::time::Duration;

use ion_rustcore::{IonCallContext, IonClient, IonClientContext, IonError, IonInterceptor, IonNext};

/// The header the lab's ticket exchange reads the user name from.
pub const USER_HEADER: &str = "x-lab-user";

/// How long one scenario may take before it fails instead of stalling the run.
pub const SCENARIO_TIMEOUT: Duration = Duration::from_secs(20);

/// Where the server is, and the id that keeps this run's users apart from any other's.
pub struct Interop {
    pub url: String,
    pub run: String,
}

impl Interop {
    /// The driver's settings; `None`, with a note, when this is not running under the driver.
    pub fn from_env(scenario: &str) -> Option<Self> {
        let url = match std::env::var("ION_INTEROP_URL") {
            Ok(url) if !url.trim().is_empty() => url.trim().trim_end_matches('/').to_owned(),
            _ => {
                println!("{scenario}: skipped, ION_INTEROP_URL is not set");
                return None;
            }
        };

        let run = std::env::var("ION_INTEROP_RUN")
            .ok()
            .filter(|run| !run.trim().is_empty())
            .expect("ION_INTEROP_RUN must be set alongside ION_INTEROP_URL");

        Some(Self { url, run: run.trim().to_owned() })
    }

    /// `{run}-{suffix}`: the user of one scenario.
    pub fn user(&self, suffix: &str) -> String {
        format!("{}-{suffix}", self.run)
    }

    /// A client that is `user` to the server: the name travels as [`USER_HEADER`] on the ticket
    /// exchange, set by an interceptor.
    pub fn client(&self, user: impl Into<String>) -> IonClientContext {
        IonClient::new(self.url.clone()).with_interceptor(UserHeader(user.into())).build()
    }
}

/// Puts the user name on every call, the ticket exchange included.
pub struct UserHeader(pub String);

#[async_trait::async_trait]
impl IonInterceptor for UserHeader {
    async fn invoke(&self, ctx: &mut IonCallContext, next: &dyn IonNext) -> Result<(), IonError> {
        let value = self
            .0
            .parse()
            .map_err(|e| IonError::Encode(format!("'{}' is not a valid header value: {e}", self.0)))?;
        ctx.request_headers.insert(USER_HEADER, value);
        next.invoke(ctx).await
    }
}

/// Runs a scenario, failing it after [`SCENARIO_TIMEOUT`] rather than letting it hang.
pub async fn bounded<F: Future>(scenario: &str, future: F) -> F::Output {
    match tokio::time::timeout(SCENARIO_TIMEOUT, future).await {
        Ok(output) => output,
        Err(_) => panic!("{scenario}: did not finish within {SCENARIO_TIMEOUT:?}"),
    }
}

/// Dropping a stream says goodbye from the client's background tasks. A test's runtime ends with
/// the test and takes those tasks with it, so after a drop the test has to stay around long enough
/// for the goodbye to reach the server — on loopback a few milliseconds; this allows a second.
pub async fn let_the_goodbye_finish() {
    tokio::time::sleep(Duration::from_secs(1)).await;
}

/// Splits a finished stream into its items and how it ended: `None` for a normal end, otherwise
/// the one error, which must be the last result.
pub fn outcome<T>(results: Vec<Result<T, IonError>>) -> (Vec<T>, Option<IonError>) {
    let mut items = Vec::new();
    let mut failure = None;

    for result in results {
        if let Some(earlier) = &failure {
            panic!("the stream went on after it failed with {earlier}");
        }
        match result {
            Ok(item) => items.push(item),
            Err(e) => failure = Some(e),
        }
    }

    (items, failure)
}

/// Byte `i` of blob `n`: `(n + i) mod 251`, as `StreamLabImpl.Blob` makes it.
pub fn blob(size: usize, n: usize) -> Vec<u8> {
    (0..size).map(|i| ((n + i) % 251) as u8).collect()
}
