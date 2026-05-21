use async_trait::async_trait;
use reqwest::header::{HeaderValue, CONTENT_TYPE};

use crate::client::IonClientContext;
use crate::formatter::IonFormat;
use crate::interceptor::{IonCallContext, IonNext, InterceptorChainLink};
use crate::types::{IonError, IonProtocolError};
use minicbor::Decoder;

// ═══════════════════════════════════════════════════════════════════
// IonRequest — handles a single unary RPC call
// ═══════════════════════════════════════════════════════════════════

pub struct IonRequest {
    ctx: IonClientContext,
    interface_name: String,
    method_name: String,
}

impl IonRequest {
    pub fn new(ctx: &IonClientContext, interface_name: impl Into<String>, method_name: impl Into<String>) -> Self {
        Self {
            ctx: ctx.clone(),
            interface_name: interface_name.into(),
            method_name: method_name.into(),
        }
    }

    /// Call with no return value (fire-and-forget).
    pub async fn call_void(&self, payload: &[u8]) -> Result<(), IonError> {
        let mut call_ctx = IonCallContext::new(
            &self.interface_name,
            &self.method_name,
            payload.to_vec(),
        );

        self.execute_chain(&mut call_ctx).await?;

        // Check for error response
        if let Some(status) = call_ctx.response_status {
            if status >= 400 {
                if let Some(ref body) = call_ctx.response_payload {
                    let mut d = Decoder::new(body);
                    let err = IonProtocolError::ion_read(&mut d)?;
                    return Err(IonError::Protocol(err));
                }
            }
        }

        Ok(())
    }

    /// Call and deserialize a single response value.
    pub async fn call<T: IonFormat>(&self, payload: &[u8]) -> Result<T, IonError> {
        let mut call_ctx = IonCallContext::new(
            &self.interface_name,
            &self.method_name,
            payload.to_vec(),
        );

        self.execute_chain(&mut call_ctx).await?;

        // Check for error response
        if let Some(status) = call_ctx.response_status {
            if status >= 400 {
                if let Some(ref body) = call_ctx.response_payload {
                    let mut d = Decoder::new(body);
                    let err = IonProtocolError::ion_read(&mut d)?;
                    return Err(IonError::Protocol(err));
                }
            }
        }

        let body = call_ctx.response_payload
            .ok_or_else(|| IonError::Decode("Empty response body".into()))?;
        let mut d = Decoder::new(&body);
        T::ion_read(&mut d)
    }

    /// Call and deserialize an optional response value.
    pub async fn call_nullable<T: IonFormat>(&self, payload: &[u8]) -> Result<Option<T>, IonError> {
        let mut call_ctx = IonCallContext::new(
            &self.interface_name,
            &self.method_name,
            payload.to_vec(),
        );

        self.execute_chain(&mut call_ctx).await?;

        if let Some(status) = call_ctx.response_status {
            if status >= 400 {
                if let Some(ref body) = call_ctx.response_payload {
                    let mut d = Decoder::new(body);
                    let err = IonProtocolError::ion_read(&mut d)?;
                    return Err(IonError::Protocol(err));
                }
            }
        }

        let body = call_ctx.response_payload
            .ok_or_else(|| IonError::Decode("Empty response body".into()))?;

        if body.is_empty() {
            return Ok(None);
        }

        let mut d = Decoder::new(&body);
        if matches!(d.datatype()?, minicbor::data::Type::Null | minicbor::data::Type::Undefined) {
            return Ok(None);
        }
        Ok(Some(T::ion_read(&mut d)?))
    }

    /// Execute the interceptor chain, ending with the terminal HTTP handler.
    async fn execute_chain(&self, call_ctx: &mut IonCallContext) -> Result<(), IonError> {
        let terminal = TerminalHandler {
            base_url: self.ctx.base_url.clone(),
            session_id: self.ctx.session_id.clone(),
            http_client: self.ctx.http_client.clone(),
        };

        // Build chain in reverse order (last interceptor is outermost)
        let mut current: Box<dyn IonNext> = Box::new(terminal);
        for interceptor in self.ctx.interceptors.iter().rev() {
            current = Box::new(InterceptorChainLink {
                interceptor: interceptor.clone(),
                next: current,
            });
        }

        current.invoke(call_ctx).await
    }
}

// ═══════════════════════════════════════════════════════════════════
// TerminalHandler — the final handler that makes the HTTP request
// ═══════════════════════════════════════════════════════════════════

struct TerminalHandler {
    base_url: String,
    session_id: String,
    http_client: reqwest::Client,
}

#[async_trait]
impl IonNext for TerminalHandler {
    async fn invoke(&self, ctx: &mut IonCallContext) -> Result<(), IonError> {
        let url = format!(
            "{}/ion/{}/{}.unary",
            self.base_url, ctx.interface_name, ctx.method_name
        );

        let mut request = self.http_client
            .post(&url)
            .header(CONTENT_TYPE, HeaderValue::from_static("application/ion"))
            .header("X-Ion-Session-Id", &self.session_id)
            .body(ctx.request_payload.clone());

        if let Some(ref correlation_id) = ctx.correlation_id {
            request = request.header("X-Ion-Correlation-Id", correlation_id.as_str());
        }

        // Apply custom headers from context
        for (name, value) in ctx.request_headers.iter() {
            request = request.header(name, value);
        }

        let response = request.send().await?;
        ctx.response_status = Some(response.status().as_u16());
        ctx.response_payload = Some(response.bytes().await?.to_vec());

        Ok(())
    }
}
