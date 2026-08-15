//! Wire encoding for Ion's three float widths.
//!
//! **Rule: every float is written at its declared width.** Ion is a schema-first format — the
//! field's width comes from the contract, so the wire honours it: `f2` → `0xF9` + 2 bytes,
//! `f4` → `0xFA` + 4 bytes, `f8` → `0xFB` + 8 bytes, always, regardless of value. minicbor's
//! encoder already behaves this way (`Encoder::f32` is a bare `0xFA` + `to_be_bytes`), so the
//! only writer change here is NaN canonicalisation.
//!
//! **NaN is canonicalised** to the positive quiet NaN with an empty payload
//! (`f2 7e00` / `f4 7fc00000` / `f8 7ff8000000000000`). Rust's `f32::NAN` already has those bits,
//! but a NaN produced by arithmetic may not — and .NET's `float.NaN` is *negative*
//! (`ffc00000`) while JavaScript cannot observe a NaN payload at all. Pinning the bits is what
//! makes NaN byte-identical across the three runtimes. Every other special value — `+0.0`,
//! `-0.0`, subnormals, `±Inf` — is written bit-for-bit, so `-0.0` stays `-0.0` on the wire.
//!
//! **Reading accepts every width** for every float type, in both directions (an `f4` field may
//! receive an `f8`-encoded value and vice versa). minicbor's own accessors do not: `Decoder::f32`
//! rejects `0xFB` and `Decoder::f16` rejects both `0xFA` and `0xFB`. [`read_any_width`] dispatches
//! on the actual wire type instead, then the caller narrows. Widening from a narrower wire width
//! is exact; narrowing from a wider one rounds, which is the intended behaviour for a field whose
//! declared width is smaller than what the peer sent. This is what keeps the encoding change
//! non-wire-breaking: payloads written by a peer that shrank floats still decode.

use crate::formatter::IonFormat;
use crate::types::IonError;
use minicbor::data::Type;
use minicbor::{Decoder, Encoder};

/// Positive quiet NaN, empty payload — the canonical `f2` NaN on the Ion wire.
pub const CANONICAL_NAN_BITS_F2: u16 = 0x7e00;
/// Positive quiet NaN, empty payload — the canonical `f4` NaN on the Ion wire.
pub const CANONICAL_NAN_BITS_F4: u32 = 0x7fc0_0000;
/// Positive quiet NaN, empty payload — the canonical `f8` NaN on the Ion wire.
pub const CANONICAL_NAN_BITS_F8: u64 = 0x7ff8_0000_0000_0000;

/// Replaces any NaN with the canonical positive quiet NaN; passes every other value through
/// untouched (`-0.0` included).
#[inline]
pub fn canonical_f32(x: f32) -> f32 {
    if x.is_nan() {
        f32::from_bits(CANONICAL_NAN_BITS_F4)
    } else {
        x
    }
}

/// Replaces any NaN with the canonical positive quiet NaN; passes every other value through
/// untouched (`-0.0` included).
#[inline]
pub fn canonical_f64(x: f64) -> f64 {
    if x.is_nan() {
        f64::from_bits(CANONICAL_NAN_BITS_F8)
    } else {
        x
    }
}

/// Reads a CBOR float of *any* wire width (`0xF9`, `0xFA` or `0xFB`) and widens it to `f64`.
///
/// Widening is exact, so the caller can narrow back to the field's declared width without
/// losing anything that was actually on the wire.
pub fn read_any_width(d: &mut Decoder<'_>) -> Result<f64, IonError> {
    match d.datatype()? {
        Type::F16 => Ok(d.f16()? as f64),
        Type::F32 => Ok(d.f32()? as f64),
        Type::F64 => Ok(d.f64()?),
        other => Err(IonError::Decode(format!(
            "expected a CBOR float (f16/f32/f64), found {other}"
        ))),
    }
}

// ═══════════════════════════════════════════════════════════════════
// f32 (f4) — single precision
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for f32 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(read_any_width(d)? as f32)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.f32(canonical_f32(*self))?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// f64 (f8) — double precision
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for f64 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        read_any_width(d)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.f64(canonical_f64(*self))?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// Half precision (f2)
// minicbor reads f16 as f32 value, so we use f32 as the Rust type
// and provide a separate newtype for f16 semantics.
// ═══════════════════════════════════════════════════════════════════

/// Half-precision float wrapper. Stored as f32 in memory, serialized as CBOR f16.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct IonF16(pub f32);

impl IonFormat for IonF16 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        // Accepts f16/f32/f64 on the wire; narrows to the declared f2 width.
        Ok(IonF16(read_any_width(d)? as f32))
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        // Write as half precision. `Encoder::f16` is `half::f16::from_f32(x).to_bits()`, which
        // preserves a NaN's sign bit — canonicalise first so every runtime agrees on `7e00`.
        e.f16(canonical_f32(self.0))?;
        Ok(())
    }
}

impl From<f32> for IonF16 {
    fn from(v: f32) -> Self {
        Self(v)
    }
}

impl From<IonF16> for f32 {
    fn from(v: IonF16) -> Self {
        v.0
    }
}
