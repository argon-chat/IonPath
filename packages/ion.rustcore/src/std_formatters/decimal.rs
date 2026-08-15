//! Wire encoding for Ion's `decimal` primitive — exact-precision decimal arithmetic, as distinct
//! from the binary floating point of `f2`/`f4`/`f8`.
//!
//! **Rule: CBOR tag 4 (decimal fraction, RFC 8949 §3.4.4) wrapping a definite-length 2-element
//! array `[exponent, mantissa]`, whose value is `mantissa × 10^exponent`.** The exponent is always
//! a plain CBOR integer. The mantissa is a plain CBOR integer when it fits the i64/u64 range, and
//! a tag 2 / tag 3 bignum with a minimal-length big-endian magnitude when it does not.
//!
//! **Canonical form: the mantissa is normalised on write** — trailing decimal zeros stripped and
//! the exponent raised to compensate — and zero is always `[0, 0]`. This is required for byte
//! identity, not cosmetic. `1.50` and `1.5` are the same number, but C#'s `System.Decimal` is the
//! only one of the three runtime types that *remembers* a trailing-zero scale, so leaving the
//! mantissa "as authored" would mean C# alone could emit a form Rust and TypeScript are unable to
//! reproduce — the same class of defect as shortest-form floats.
//!
//! **The tag is required on read.** Unlike the leniencies below, accepting an untagged
//! `[exponent, mantissa]` array would not be free: that is exactly the encoding of an `i8[2]`
//! field. Tag 5 (bigfloat) is the same array shape with a base-2 exponent and is rejected outright
//! rather than misread as base 10.
//!
//! **Readers are lenient** about everything that cannot be confused with another type: an
//! indefinite-length inner array, a non-normalised mantissa, and a bignum holding a value that
//! would have fitted a plain integer are all accepted and re-encoded canonically.
//!
//! Golden vectors: `/tests/golden/decimal.golden.json`.

use crate::formatter::IonFormat;
use crate::types::{IonDecimal, IonError};
use minicbor::data::{Int, Tag, Type};
use minicbor::{Decoder, Encoder};

/// CBOR tag 4: decimal fraction.
pub const DECIMAL_TAG: u64 = 4;

/// CBOR tag 2: positive bignum.
pub const POS_BIGNUM_TAG: u64 = 2;

/// CBOR tag 3: negative bignum.
pub const NEG_BIGNUM_TAG: u64 = 3;

fn malformed(reason: impl Into<String>) -> IonError {
    IonError::MalformedValue { ion_type: "decimal", reason: reason.into() }
}

/// Writes a mantissa in Ion's canonical form.
///
/// A plain CBOR integer while the value fits **i64 or u64** — the union of the signed and unsigned
/// 64-bit ranges, i.e. what every runtime can express without arbitrary precision — and a tag 2/3
/// bignum beyond that. Note the deliberate asymmetry: `-2^63 - 1` becomes a bignum even though
/// CBOR could encode it as a plain negative integer, because C#'s `BigInteger` path draws the line
/// at `long.MinValue` and all three runtimes must draw it in the same place.
pub fn write_mantissa(e: &mut Encoder<Vec<u8>>, mantissa: i128) -> Result<(), IonError> {
    if mantissa >= i64::MIN as i128 && mantissa <= u64::MAX as i128 {
        // `Int` spans the whole CBOR integer range, so this cannot fail for the window above.
        let n = Int::try_from(mantissa)
            .map_err(|_| malformed("mantissa does not fit a CBOR integer"))?;
        e.int(n)?;
        return Ok(());
    }

    let (tag, magnitude) = if mantissa >= 0 {
        (POS_BIGNUM_TAG, mantissa as u128)
    } else {
        // magnitude = -1 - mantissa, computed as a bit complement so `i128::MIN` does not overflow.
        (NEG_BIGNUM_TAG, !(mantissa as u128))
    };

    let bytes = magnitude.to_be_bytes();
    let first = bytes.iter().position(|&b| b != 0).unwrap_or(bytes.len() - 1);

    e.tag(Tag::new(tag))?;
    e.bytes(&bytes[first..])?;
    Ok(())
}

/// Reads a mantissa: a plain CBOR integer, or a tag 2/3 bignum.
pub fn read_mantissa(d: &mut Decoder<'_>) -> Result<i128, IonError> {
    match d.datatype()? {
        Type::U8 | Type::U16 | Type::U32 | Type::U64 | Type::I8 | Type::I16 | Type::I32
        | Type::I64 | Type::Int => Ok(i128::from(d.int()?)),

        Type::Tag => {
            let tag = d.tag()?.as_u64();
            if tag != POS_BIGNUM_TAG && tag != NEG_BIGNUM_TAG {
                return Err(IonError::UnexpectedTag {
                    expected: POS_BIGNUM_TAG,
                    actual: tag,
                    ion_type: "decimal mantissa",
                });
            }

            let raw = d.bytes()?;
            let magnitude_bytes = {
                let first = raw.iter().position(|&b| b != 0).unwrap_or(raw.len());
                &raw[first..]
            };
            if magnitude_bytes.len() > 16 {
                return Err(IonError::DecimalRange {
                    reason: format!(
                        "bignum mantissa is {} bytes, beyond the 16 an i128 can hold",
                        magnitude_bytes.len()
                    ),
                });
            }

            let mut magnitude: u128 = 0;
            for byte in magnitude_bytes {
                magnitude = (magnitude << 8) | u128::from(*byte);
            }
            if magnitude > i128::MAX as u128 {
                return Err(IonError::DecimalRange {
                    reason: "bignum mantissa magnitude exceeds i128::MAX".into(),
                });
            }

            Ok(if tag == POS_BIGNUM_TAG { magnitude as i128 } else { -1 - (magnitude as i128) })
        }

        other => Err(malformed(format!(
            "mantissa must be an integer or a tag 2/3 bignum, got {other}"
        ))),
    }
}

impl IonFormat for IonDecimal {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        if d.datatype()? != Type::Tag {
            return Err(malformed(format!(
                "expected CBOR tag {DECIMAL_TAG}, got {}",
                d.datatype()?
            )));
        }

        let tag = d.tag()?.as_u64();
        if tag != DECIMAL_TAG {
            return Err(IonError::UnexpectedTag {
                expected: DECIMAL_TAG,
                actual: tag,
                ion_type: "decimal",
            });
        }

        if !matches!(d.datatype()?, Type::Array | Type::ArrayIndef) {
            return Err(malformed(format!(
                "tag {DECIMAL_TAG} must wrap an array, got {}",
                d.datatype()?
            )));
        }

        let length = d.array()?;
        if let Some(n) = length {
            if n != 2 {
                return Err(malformed(format!(
                    "tag {DECIMAL_TAG} requires exactly 2 elements, got {n}"
                )));
            }
        }

        let exponent = i32::try_from(i128::from(d.int()?))
            .map_err(|_| malformed("exponent does not fit an i32"))?;
        let mantissa = read_mantissa(d)?;

        // An indefinite-length inner array is accepted, but must still hold exactly two items.
        if length.is_none() {
            if d.datatype()? != Type::Break {
                return Err(malformed(format!(
                    "tag {DECIMAL_TAG} requires exactly 2 elements, got more"
                )));
            }
            d.skip()?;
        }

        Ok(IonDecimal { exponent, mantissa }.normalized())
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        let canonical = self.normalized();
        e.tag(Tag::new(DECIMAL_TAG))?;
        e.array(2)?;
        e.i32(canonical.exponent)?;
        write_mantissa(e, canonical.mantissa)?;
        Ok(())
    }
}
