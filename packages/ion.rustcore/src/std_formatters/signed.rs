use crate::formatter::IonFormat;
use crate::types::IonError;
use minicbor::{Decoder, Encoder};

// ═══════════════════════════════════════════════════════════════════
// i8 (i1)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for i8 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.i8()?)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.i8(*self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// i16 (i2)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for i16 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.i16()?)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.i16(*self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// i32 (i4)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for i32 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.i32()?)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.i32(*self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// i64 (i8)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for i64 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.i64()?)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.i64(*self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// i128 (i16)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for i128 {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        // i128 is stored as a 16-byte big-endian CBOR byte string
        let bytes = d.bytes()?;
        if bytes.len() != 16 {
            return Err(IonError::Decode(format!(
                "Expected 16 bytes for i128, got {}",
                bytes.len()
            )));
        }
        let mut arr = [0u8; 16];
        arr.copy_from_slice(bytes);
        Ok(i128::from_be_bytes(arr))
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.bytes(&self.to_be_bytes())?;
        Ok(())
    }
}
