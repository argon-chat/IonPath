use crate::formatter::IonFormat;
use crate::types::IonError;
use minicbor::{Decoder, Encoder};

// ═══════════════════════════════════════════════════════════════════
// u8 (u1)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for u8 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.u8()?)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.u8(*self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// u16 (u2)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for u16 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.u16()?)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.u16(*self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// u32 (u4)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for u32 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.u32()?)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.u32(*self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// u64 (u8)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for u64 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.u64()?)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.u64(*self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// u128 (u16)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for u128 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        // u128 is stored as a 16-byte big-endian CBOR byte string
        let bytes = d.bytes()?;
        if bytes.len() != 16 {
            return Err(IonError::Decode(format!(
                "Expected 16 bytes for u128, got {}",
                bytes.len()
            )));
        }
        let mut arr = [0u8; 16];
        arr.copy_from_slice(bytes);
        Ok(u128::from_be_bytes(arr))
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.bytes(&self.to_be_bytes())?;
        Ok(())
    }
}
