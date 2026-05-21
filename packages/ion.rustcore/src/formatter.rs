use crate::types::IonError;
use minicbor::{Decoder, Encoder};

// ═══════════════════════════════════════════════════════════════════
// IonFormat trait — the core serialization interface
// ═══════════════════════════════════════════════════════════════════

/// Trait for types that can be serialized/deserialized in the Ion wire format.
/// Equivalent to `IonFormatter<T>` in C# and the formatter interface in TypeScript.
pub trait IonFormat: Sized {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError>;
    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError>;
}

// ═══════════════════════════════════════════════════════════════════
// Helper functions for reading/writing collections
// ═══════════════════════════════════════════════════════════════════

/// Read an `Option<T>` — reads null as None, value as Some.
pub fn read_maybe<T: IonFormat>(d: &mut Decoder<'_>) -> Result<Option<T>, IonError> {
    if matches!(d.datatype()?, minicbor::data::Type::Null | minicbor::data::Type::Undefined) {
        d.null()?;
        Ok(None)
    } else {
        let value = T::ion_read(d)?;
        Ok(Some(value))
    }
}

/// Write an `Option<T>` — writes null for None, value for Some.
pub fn write_maybe<T: IonFormat>(
    e: &mut Encoder<Vec<u8>>,
    value: &Option<T>,
) -> Result<(), IonError> {
    match value {
        Some(v) => v.ion_write(e)?,
        None => { e.null()?; }
    }
    Ok(())
}

/// Read a `Vec<T>` from a CBOR array.
pub fn read_array<T: IonFormat>(d: &mut Decoder<'_>) -> Result<Vec<T>, IonError> {
    let len = d.array()?.ok_or(IonError::IndefiniteArray)? as usize;
    let mut result = Vec::with_capacity(len);
    for _ in 0..len {
        result.push(T::ion_read(d)?);
    }
    Ok(result)
}

/// Write a `Vec<T>` as a CBOR array.
pub fn write_array<T: IonFormat>(
    e: &mut Encoder<Vec<u8>>,
    values: &[T],
) -> Result<(), IonError> {
    e.array(values.len() as u64)?;
    for v in values {
        v.ion_write(e)?;
    }
    Ok(())
}

/// Skip remaining fields in a CBOR array (for forward-compatibility).
/// If `total_len > expected_fields`, skips the extra items.
pub fn skip_remaining(d: &mut Decoder<'_>, total_len: u64, expected_fields: u64) -> Result<(), IonError> {
    let extra = total_len.saturating_sub(expected_fields);
    for _ in 0..extra {
        d.skip()?;
    }
    Ok(())
}

// ═══════════════════════════════════════════════════════════════════
// Blanket IonFormat impls for Vec<T>
// ═══════════════════════════════════════════════════════════════════

impl<T: IonFormat> IonFormat for Vec<T> {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        read_array::<T>(d)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        write_array(e, self)
    }
}

impl<T: IonFormat> IonFormat for Option<T> {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        read_maybe::<T>(d)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        write_maybe(e, self)
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonProtocolError formatter
// ═══════════════════════════════════════════════════════════════════

use crate::types::IonProtocolError;

impl IonFormat for IonProtocolError {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        d.array()?;
        let code = String::ion_read(d)?;
        let msg = String::ion_read(d)?;
        Ok(IonProtocolError { code, msg })
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.array(2)?;
        self.code.ion_write(e)?;
        self.msg.ion_write(e)?;
        Ok(())
    }
}
