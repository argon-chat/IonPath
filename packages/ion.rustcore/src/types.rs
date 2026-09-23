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

    // ── typed decode failures ───────────────────────────────────────────────
    // A malformed payload must never surface as an opaque `Decode(String)`. These mirror
    // `ion.runtime.IonDecodeException` and its subclasses in C#, and the `IonDecodeError`
    // hierarchy in TypeScript, so the three runtimes fail the same way on the same bytes.
    /// A `datetime` payload was not a parseable RFC 3339 date-time, or carried no offset.
    ///
    /// RFC 3339 requires an explicit offset; a local time without one is genuinely ambiguous, and
    /// guessing UTC would move the instant by up to 14 hours, so it is rejected rather than
    /// assumed.
    #[error("Malformed Ion datetime '{text}': {reason}")]
    DateTimeFormat { text: String, reason: String },

    /// A CBOR item carried a tag the formatter does not accept for the declared Ion type.
    #[error("Expected CBOR tag {expected} for Ion type '{ion_type}', got tag {actual}")]
    UnexpectedTag { expected: u64, actual: u64, ion_type: &'static str },

    /// A CBOR item was structurally not what the declared Ion type requires.
    #[error("Malformed Ion '{ion_type}': {reason}")]
    MalformedValue { ion_type: &'static str, reason: String },

    /// A `decimal` was a valid CBOR tag 4 fraction whose mantissa does not fit an `i128`.
    ///
    /// [`IonDecimal`] is dependency-free and stores the mantissa in an `i128`; tag 4 permits
    /// arbitrary precision. The gap is reachable from the TypeScript runtime, whose mantissa is a
    /// native `bigint`, so it must be a typed failure rather than a silent truncation.
    #[error("Ion decimal is outside the range of ion_rustcore::IonDecimal: {reason}")]
    DecimalRange { reason: String },

    /// A `Map<K,V>` payload contained the same key twice.
    ///
    /// Rejected rather than merged: last-wins and first-wins both make the decoded value depend
    /// on the order entries happen to appear in, which is the very non-determinism the canonical
    /// key ordering exists to remove. The key is reported as its canonical encoded bytes, in hex.
    #[error("Duplicate key (encoded {key_hex}) in an Ion Map payload; duplicate keys are rejected, not merged")]
    DuplicateMapKey { key_hex: String },

    /// A `Set<T>` payload contained the same element twice.
    ///
    /// Rejected rather than collapsed: collapsing would let a three-element wire array decode as
    /// a two-element set, a size change the caller can neither observe nor guard against.
    #[error("Duplicate element (encoded {element_hex}) in an Ion Set payload; duplicate elements are rejected, not collapsed")]
    DuplicateSetElement { element_hex: String },

    /// A fixed-size array `T[N]` was read from — or written with — a length other than `N`.
    ///
    /// Names **both** lengths: knowing only that the length was wrong does not tell a caller
    /// whether the peer is on an older schema revision or the payload was truncated.
    #[error("Ion fixed-size array declared length {expected}, got {actual}")]
    FixedArrayLength { expected: usize, actual: usize },

    /// The payload nests CBOR containers more deeply than [`crate::formatter::MAX_DEPTH`] allows.
    ///
    /// Nesting is the one dimension of a payload that costs **stack** rather than heap, and the
    /// stack is the one resource whose exhaustion is not recoverable: a Rust stack overflow
    /// aborts the process and cannot be caught, so a `catch_unwind` around the decode does not
    /// help. It is also reachable without knowing the schema — a field the reader only *skips*
    /// still has to be walked, so 100 KB of `0x81` bytes is 100 000 levels deep.
    ///
    /// The counterparts are `ion.runtime.IonDepthLimitException` (C#) and `IonDepthLimitError`
    /// (TypeScript), and all three share the limit of 128.
    #[error("Ion payload nests CBOR containers {depth} deep, past the limit of {limit} (see ion_rustcore::formatter::MAX_DEPTH)")]
    DepthLimit { limit: usize, depth: usize },

    /// A declared container length is larger than the remaining input could possibly contain.
    ///
    /// Every CBOR data item occupies at least one byte, so a declared item count above the number
    /// of bytes left is *provably* a lie. Checking it before reserving is what keeps a nine-byte
    /// payload (`9bffffffffffffffff` — an array header claiming 2^64-1 items) from reaching
    /// `Vec::with_capacity` and aborting the process on a capacity overflow.
    ///
    /// The counterpart is `IonLengthOverclaimError` in TypeScript.
    #[error("Ion '{context}' declares {declared} item(s) but only {available} byte(s) of input remain")]
    LengthOverclaim { context: &'static str, declared: u64, available: usize },

    /// A message array declared fewer items than this revision of the schema reads.
    ///
    /// `minicbor::Decoder` is a flat cursor with no container stack, so reading past a definite
    /// array is not refused by anything — the reader simply takes the *next* bytes in the stream,
    /// which belong to whatever follows. Left unchecked this is silent wrong data, not a failure:
    /// the peer is on an older revision and the field genuinely is not there.
    ///
    /// The counterparts are `IonFieldCountException` (C#) and `IonFieldCountError` (TypeScript).
    #[error("Ion message declares {declared} field(s) but this schema reads {expected}")]
    FieldCount { declared: u64, expected: u64 },

    /// A `union` envelope was not the two-item `[index, payload]` array the format defines.
    ///
    /// The envelope is fixed at two items in **every** revision of **every** union; a union grows
    /// by adding cases, and a case grows inside its payload, which is a message and skips its own
    /// trailing fields. A longer envelope is therefore never a forward-compatibility signal, and
    /// reading `[index, payload]` out of it and walking away leaves the stray items in the stream
    /// for the *next* field of the enclosing message to pick up as its own value.
    ///
    /// The counterpart is `IonUnionEnvelopeError` in TypeScript.
    #[error("Ion union '{union_type}' envelope must be exactly [index, payload] (2 items), got {actual_items}")]
    UnionEnvelope { union_type: &'static str, actual_items: u64 },

    // ── stream outcomes ─────────────────────────────────────────────────────
    // How a `stream` call ends is how its item stream ends: `None` after END, `Protocol` after
    // ERROR, and one of these two otherwise. They mirror `ion.runtime.IonStreamClosedException`
    // and `IonStreamDisconnectedException` in C#.
    /// The server closed the stream on purpose with a CLOSE frame — a kick, a revoked session, a
    /// restart. `allow_reconnect` says whether coming straight back is welcome.
    #[error("The server closed the stream{}", closed_detail(.reason, .allow_reconnect))]
    StreamClosed { reason: Option<String>, allow_reconnect: bool },

    /// The stream ended without the server saying so: the transport died, went silent past the
    /// timeout, or broke the protocol. Always safe to retry.
    #[error("Stream disconnected ({reason}): {message}")]
    StreamDisconnected { reason: DisconnectReason, message: String },
}

fn closed_detail(reason: &Option<String>, allow_reconnect: &bool) -> String {
    let reason = reason.as_deref().map(|r| format!(": {r}")).unwrap_or_default();
    let reconnect = if *allow_reconnect { " (reconnecting is allowed)" } else { "" };
    format!("{reason}{reconnect}")
}

/// Why a stream ended without an END, ERROR or CLOSE from the server — the members of C#'s
/// `ion.runtime.IonDisconnectReason` a client can observe.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum DisconnectReason {
    /// The connection died, or the server closed the WebSocket without ending the stream.
    TransportLost,
    /// The server sent nothing for longer than the timeout, or did not accept the stream in time.
    Timeout,
    /// The server sent something the stream protocol does not allow.
    ProtocolViolation,
}

impl DisconnectReason {
    /// The error code the other runtimes report for the same failure.
    pub fn code(&self) -> &'static str {
        match self {
            DisconnectReason::TransportLost => "STREAM_DISCONNECTED",
            DisconnectReason::Timeout => "STREAM_TIMEOUT",
            DisconnectReason::ProtocolViolation => "PROTOCOL_VIOLATION",
        }
    }
}

impl fmt::Display for DisconnectReason {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            DisconnectReason::TransportLost => "transport lost",
            DisconnectReason::Timeout => "timeout",
            DisconnectReason::ProtocolViolation => "protocol violation",
        })
    }
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

/// `Eq`/`Hash`/`Ord` are derived, not incidental: `duration` is one of the types Ion allows as a
/// `Map` key, so `HashMap<IonDuration, V>` has to compile. It is a single `i64` of ticks, so all
/// three are exact and agree with `PartialEq`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct IonDuration {
    pub ticks: i64,
}

// ═══════════════════════════════════════════════════════════════════
// IonDecimal
// ═══════════════════════════════════════════════════════════════════

/// An exact decimal: `mantissa × 10^exponent` — Ion's `decimal`.
///
/// **Dependency-free by design.** `rust_decimal` is deliberately not used: the wire form is CBOR
/// tag 4, which is already an exponent/mantissa pair, so a third-party decimal type would only
/// add a conversion layer and a dependency to a crate whose entire job is to speak a wire format.
/// The mantissa is a native `i128`, which spans every value C#'s `System.Decimal` can hold (its
/// unscaled magnitude tops out at 2^96 - 1) with three orders of magnitude to spare.
///
/// A mantissa outside `i128` — reachable only from the TypeScript runtime, whose mantissa is a
/// native `bigint` — is a typed decode error, [`IonError::DecimalRange`], never a truncation.
///
/// **Equality is structural, not numeric.** `1.50` (`exponent: -2, mantissa: 150`) and `1.5`
/// (`exponent: -1, mantissa: 15`) are different `IonDecimal`s and hash differently, because that
/// is what `#[derive(PartialEq, Hash)]` can honestly promise. Use [`IonDecimal::eq_numeric`] for
/// value equality, or compare [`IonDecimal::normalized`] forms — which is also exactly the
/// condition under which two values produce identical wire bytes.
///
/// Wire format and golden vectors: `/tests/golden/decimal.golden.json`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct IonDecimal {
    pub exponent: i32,
    pub mantissa: i128,
}

impl IonDecimal {
    /// The canonical zero: `(0, 0)`.
    pub const ZERO: IonDecimal = IonDecimal { exponent: 0, mantissa: 0 };

    pub const fn new(exponent: i32, mantissa: i128) -> Self {
        Self { exponent, mantissa }
    }

    /// Ion's canonical form: trailing zeros stripped from the mantissa, and zero as `(0, 0)`.
    /// This is what the formatter writes, and it is why `1.50` and `1.5` are byte-identical.
    pub fn normalized(&self) -> Self {
        if self.mantissa == 0 {
            return Self::ZERO;
        }

        let mut exponent = self.exponent;
        let mut mantissa = self.mantissa;
        while mantissa % 10 == 0 {
            mantissa /= 10;
            exponent += 1;
        }
        Self { exponent, mantissa }
    }

    /// Numeric equality: `1.50` equals `1.5`.
    pub fn eq_numeric(&self, other: &Self) -> bool {
        self.normalized() == other.normalized()
    }

    /// Lossy conversion to `f64`, for display and arithmetic that does not need exactness.
    /// Named so the loss cannot happen by accident.
    pub fn to_f64_lossy(&self) -> f64 {
        self.to_string().parse().unwrap_or(f64::NAN)
    }
}

/// Plain (never scientific) decimal text, preserving the authored scale.
impl fmt::Display for IonDecimal {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let negative = self.mantissa < 0;
        // `unsigned_abs` so `i128::MIN` does not overflow.
        let digits = self.mantissa.unsigned_abs().to_string();

        let body = if self.exponent >= 0 {
            format!("{digits}{}", "0".repeat(self.exponent as usize))
        } else {
            let scale = (-(self.exponent as i64)) as usize;
            if digits.len() > scale {
                let split = digits.len() - scale;
                format!("{}.{}", &digits[..split], &digits[split..])
            } else {
                format!("0.{}{digits}", "0".repeat(scale - digits.len()))
            }
        };

        if negative {
            write!(f, "-{body}")
        } else {
            f.write_str(&body)
        }
    }
}

/// Parses a plain or scientific decimal string: `-1.50`, `0`, `1e-28`, `+3.14E+2`.
/// The authored scale is preserved — `"1.50"` yields `(-2, 150)`, not `(-1, 15)`.
impl std::str::FromStr for IonDecimal {
    type Err = IonError;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        let text = s.trim();
        let bad = || IonError::DecimalRange { reason: format!("'{text}' is not a decimal number") };

        let (negative, rest) = match text.as_bytes().first() {
            Some(b'-') => (true, &text[1..]),
            Some(b'+') => (false, &text[1..]),
            _ => (false, text),
        };

        let (digits_part, exp_part) = match rest.find(['e', 'E']) {
            Some(i) => (&rest[..i], Some(&rest[i + 1..])),
            None => (rest, None),
        };

        let (int_part, frac_part) = match digits_part.find('.') {
            Some(i) => (&digits_part[..i], &digits_part[i + 1..]),
            None => (digits_part, ""),
        };

        if int_part.is_empty()
            || !int_part.bytes().all(|b| b.is_ascii_digit())
            || !frac_part.bytes().all(|b| b.is_ascii_digit())
        {
            return Err(bad());
        }

        let mantissa: i128 = format!("{int_part}{frac_part}").parse().map_err(|_| bad())?;
        let exponent: i32 = match exp_part {
            Some(e) => e.parse().map_err(|_| bad())?,
            None => 0,
        };

        let exponent = exponent
            .checked_sub(frac_part.len() as i32)
            .ok_or_else(bad)?;

        Ok(Self { exponent, mantissa: if negative { -mantissa } else { mantissa } })
    }
}
