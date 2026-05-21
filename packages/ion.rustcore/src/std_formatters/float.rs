use crate::formatter::IonFormat;
use crate::types::IonError;
use minicbor::{Decoder, Encoder};

// ═══════════════════════════════════════════════════════════════════
// f32 (f4) — single precision
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for f32 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.f32()?)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.f32(*self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// f64 (f8) — double precision
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for f64 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.f64()?)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.f64(*self)?;
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
        // minicbor's f16() returns the value as f32
        Ok(IonF16(d.f16()?))
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        // Write as half precision
        e.f16(self.0)?;
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
