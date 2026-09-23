use crate::interceptor::IonInterceptor;
use crate::streaming::IonStreamOptions;

// ═══════════════════════════════════════════════════════════════════
// IonClientContext — shared state for all requests from a client
// ═══════════════════════════════════════════════════════════════════

#[derive(Clone)]
pub struct IonClientContext {
    pub base_url: String,
    pub session_id: String,
    pub http_client: reqwest::Client,
    /// Settings for `stream` calls: heartbeat, timeouts, queue sizes.
    pub stream_options: IonStreamOptions,
    pub(crate) interceptors: Vec<std::sync::Arc<dyn IonInterceptor>>,
}

// ═══════════════════════════════════════════════════════════════════
// IonClient — builder for creating client contexts
// ═══════════════════════════════════════════════════════════════════

pub struct IonClient {
    base_url: String,
    interceptors: Vec<std::sync::Arc<dyn IonInterceptor>>,
    http_client: Option<reqwest::Client>,
    stream_options: IonStreamOptions,
}

impl IonClient {
    pub fn new(base_url: impl Into<String>) -> Self {
        Self {
            base_url: base_url.into(),
            interceptors: Vec::new(),
            http_client: None,
            stream_options: IonStreamOptions::default(),
        }
    }

    pub fn with_interceptor<T: IonInterceptor + 'static>(mut self, interceptor: T) -> Self {
        self.interceptors.push(std::sync::Arc::new(interceptor));
        self
    }

    pub fn with_http_client(mut self, client: reqwest::Client) -> Self {
        self.http_client = Some(client);
        self
    }

    /// Settings for `stream` calls; see [`IonStreamOptions`].
    pub fn with_stream_options(mut self, options: IonStreamOptions) -> Self {
        self.stream_options = options;
        self
    }

    pub fn build(self) -> IonClientContext {
        let http_client = self.http_client.unwrap_or_else(|| {
            reqwest::Client::new()
        });

        IonClientContext {
            base_url: self.base_url,
            session_id: uuid::Uuid::new_v4().to_string(),
            http_client,
            stream_options: self.stream_options,
            interceptors: self.interceptors,
        }
    }
}

impl IonClientContext {
    /// Create a typed service client. Used by generated code.
    pub fn service<T: FromContext>(&self) -> T {
        T::from_context(self.clone())
    }

    pub fn base_url(&self) -> &str {
        &self.base_url
    }

    pub fn interceptors(&self) -> &[std::sync::Arc<dyn IonInterceptor>] {
        &self.interceptors
    }

    pub fn stream_options(&self) -> &IonStreamOptions {
        &self.stream_options
    }
}

/// Trait for generated service client structs to construct themselves from context.
pub trait FromContext {
    fn from_context(ctx: IonClientContext) -> Self;
}
