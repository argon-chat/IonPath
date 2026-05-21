use crate::types::IonError;
use async_trait::async_trait;
use reqwest::header::HeaderMap;
use std::time::Instant;

// ═══════════════════════════════════════════════════════════════════
// IonCallContext — context available to interceptors
// ═══════════════════════════════════════════════════════════════════

pub struct IonCallContext {
    pub interface_name: String,
    pub method_name: String,
    pub request_payload: Vec<u8>,
    pub response_payload: Option<Vec<u8>>,
    pub response_status: Option<u16>,
    pub request_headers: HeaderMap,
    pub correlation_id: Option<String>,
    pub stopwatch: Instant,
}

impl IonCallContext {
    pub fn new(interface_name: impl Into<String>, method_name: impl Into<String>, payload: Vec<u8>) -> Self {
        Self {
            interface_name: interface_name.into(),
            method_name: method_name.into(),
            request_payload: payload,
            response_payload: None,
            response_status: None,
            request_headers: HeaderMap::new(),
            correlation_id: None,
            stopwatch: Instant::now(),
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonInterceptor — middleware trait
// ═══════════════════════════════════════════════════════════════════

#[async_trait]
pub trait IonInterceptor: Send + Sync {
    async fn invoke(
        &self,
        ctx: &mut IonCallContext,
        next: &dyn IonNext,
    ) -> Result<(), IonError>;
}

// ═══════════════════════════════════════════════════════════════════
// IonNext — represents the next handler in the chain
// ═══════════════════════════════════════════════════════════════════

#[async_trait]
pub trait IonNext: Send + Sync {
    async fn invoke(&self, ctx: &mut IonCallContext) -> Result<(), IonError>;
}

/// Internal: wraps the interceptor chain execution
pub struct InterceptorChainLink {
    pub interceptor: std::sync::Arc<dyn IonInterceptor>,
    pub next: Box<dyn IonNext>,
}

#[async_trait]
impl IonNext for InterceptorChainLink {
    async fn invoke(&self, ctx: &mut IonCallContext) -> Result<(), IonError> {
        self.interceptor.invoke(ctx, self.next.as_ref()).await
    }
}
