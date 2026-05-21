use std::fmt;

use thiserror::Error;

// ═══════════════════════════════════════════════════════════════════
// IonError
// ═══════════════════════════════════════════════════════════════════

#[derive(Debug, Error)]
pub enum IonError {
    #[error("CBOR decode error: {0}")]
    Decode(String),

    #[error("CBOR encode error: {0}")]
    Encode(String),

    #[error("HTTP error: {0}")]
    Http(#[from] reqwest::Error),

    #[error("Protocol error: {0}")]
    Protocol(IonProtocolError),

    #[error("Indefinite-length array not allowed")]
    IndefiniteArray,

    #[error("Invalid enum value: {0}")]
    InvalidEnum(i64),

    #[error("Invalid union index: {0}")]
    InvalidUnionIndex(u32),
}

impl From<minicbor::decode::Error> for IonError {
    fn from(e: minicbor::decode::Error) -> Self {
        IonError::Decode(e.to_string())
    }
}

impl<T: fmt::Debug> From<minicbor::encode::Error<T>> for IonError {
    fn from(e: minicbor::encode::Error<T>) -> Self {
        IonError::Encode(format!("{:?}", e))
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonProtocolError
// ═══════════════════════════════════════════════════════════════════

#[derive(Debug, Clone)]
pub struct IonProtocolError {
    pub code: String,
    pub msg: String,
}

impl IonProtocolError {
    pub fn upstream_error(msg: impl Into<String>) -> Self {
        Self { code: "UPSTREAM_ERROR".into(), msg: msg.into() }
    }

    pub fn internal_error(msg: impl Into<String>) -> Self {
        Self { code: "INTERNAL_ERROR".into(), msg: msg.into() }
    }

    pub fn deadline_exceeded() -> Self {
        Self { code: "DEADLINE_EXCEEDED".into(), msg: "Deadline exceeded".into() }
    }
}

impl fmt::Display for IonProtocolError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.code, self.msg)
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonMaybe<T>
// ═══════════════════════════════════════════════════════════════════

#[derive(Debug, Clone, PartialEq)]
pub struct IonMaybe<T> {
    value: Option<T>,
}

impl<T> IonMaybe<T> {
    pub fn some(value: T) -> Self {
        Self { value: Some(value) }
    }

    pub fn none() -> Self {
        Self { value: None }
    }

    pub fn has_value(&self) -> bool {
        self.value.is_some()
    }

    pub fn unwrap(self) -> T {
        self.value.unwrap()
    }

    pub fn unwrap_or(self, default: T) -> T {
        self.value.unwrap_or(default)
    }

    pub fn unwrap_or_default(self) -> T
    where
        T: Default,
    {
        self.value.unwrap_or_default()
    }

    pub fn as_ref(&self) -> Option<&T> {
        self.value.as_ref()
    }

    pub fn into_option(self) -> Option<T> {
        self.value
    }
}

impl<T> From<Option<T>> for IonMaybe<T> {
    fn from(opt: Option<T>) -> Self {
        Self { value: opt }
    }
}

impl<T> From<T> for IonMaybe<T> {
    fn from(value: T) -> Self {
        Self::some(value)
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonBytes
// ═══════════════════════════════════════════════════════════════════

#[derive(Debug, Clone, PartialEq)]
pub struct IonBytes {
    data: Vec<u8>,
}

impl IonBytes {
    pub fn new(data: Vec<u8>) -> Self {
        Self { data }
    }

    pub fn as_slice(&self) -> &[u8] {
        &self.data
    }

    pub fn len(&self) -> usize {
        self.data.len()
    }

    pub fn is_empty(&self) -> bool {
        self.data.is_empty()
    }

    pub fn into_vec(self) -> Vec<u8> {
        self.data
    }
}

impl From<Vec<u8>> for IonBytes {
    fn from(data: Vec<u8>) -> Self {
        Self { data }
    }
}

impl From<&[u8]> for IonBytes {
    fn from(data: &[u8]) -> Self {
        Self { data: data.to_vec() }
    }
}

impl AsRef<[u8]> for IonBytes {
    fn as_ref(&self) -> &[u8] {
        &self.data
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonDateOnly / IonTimeOnly / IonDuration
// ═══════════════════════════════════════════════════════════════════

#[derive(Debug, Clone, PartialEq)]
pub struct IonDateOnly {
    pub year: i32,
    pub month: u32,
    pub day: u32,
}

#[derive(Debug, Clone, PartialEq)]
pub struct IonTimeOnly {
    pub hour: u32,
    pub minute: u32,
    pub second: u32,
    pub millisecond: u32,
    pub microsecond: u32,
}

#[derive(Debug, Clone, PartialEq)]
pub struct IonDuration {
    pub ticks: i64,
}
