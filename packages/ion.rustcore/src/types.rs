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
