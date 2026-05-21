use crate::formatter::IonFormat;
use crate::types::*;
use minicbor::{Decoder, Encoder};

// ═══════════════════════════════════════════════════════════════════
// bool
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for bool {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.bool()?)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.bool(*self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// String
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for String {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(d.str()?.to_owned())
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.str(self)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonBytes (bytes)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for IonBytes {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        let data = d.bytes()?.to_vec();
        Ok(IonBytes::new(data))
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.bytes(self.as_slice())?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// uuid::Uuid (guid) — stored as 16-byte CBOR binary
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for uuid::Uuid {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        let bytes = d.bytes()?;
        if bytes.len() != 16 {
            return Err(IonError::Decode(format!(
                "Expected 16 bytes for UUID, got {}",
                bytes.len()
            )));
        }
        Ok(uuid::Uuid::from_bytes(bytes.try_into().unwrap()))
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.bytes(self.as_bytes())?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonDateOnly — CBOR array [year, month, day]
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for IonDateOnly {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        d.array()?;
        let year = d.i32()?;
        let month = d.u32()?;
        let day = d.u32()?;
        Ok(IonDateOnly { year, month, day })
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.array(3)?;
        e.i32(self.year)?;
        e.u32(self.month)?;
        e.u32(self.day)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonTimeOnly — CBOR array [hour, minute, second, millisecond, microsecond]
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for IonTimeOnly {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        d.array()?;
        let hour = d.u32()?;
        let minute = d.u32()?;
        let second = d.u32()?;
        let millisecond = d.u32()?;
        let microsecond = d.u32()?;
        Ok(IonTimeOnly { hour, minute, second, millisecond, microsecond })
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.array(5)?;
        e.u32(self.hour)?;
        e.u32(self.minute)?;
        e.u32(self.second)?;
        e.u32(self.millisecond)?;
        e.u32(self.microsecond)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// IonDuration — CBOR int (ticks as i64)
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for IonDuration {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        let ticks = d.i64()?;
        Ok(IonDuration { ticks })
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        e.i64(self.ticks)?;
        Ok(())
    }
}

// ═══════════════════════════════════════════════════════════════════
// chrono::DateTime<FixedOffset> (datetime) — CBOR array [ticks, offset_minutes]
// ═══════════════════════════════════════════════════════════════════

impl IonFormat for chrono::DateTime<chrono::FixedOffset> {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        d.array()?;
        let ticks = d.i64()?;
        let offset_minutes = d.i32()?;

        let offset = chrono::FixedOffset::east_opt(offset_minutes as i32 * 60)
            .ok_or_else(|| IonError::Decode("Invalid timezone offset".into()))?;

        // .NET ticks are 100-nanosecond intervals since 0001-01-01
        // Convert to Unix timestamp
        const TICKS_PER_SECOND: i64 = 10_000_000;
        const UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;
        let unix_ticks = ticks - UNIX_EPOCH_TICKS;
        let secs = unix_ticks / TICKS_PER_SECOND;
        let nanos = ((unix_ticks % TICKS_PER_SECOND) * 100) as u32;

        let naive = chrono::DateTime::from_timestamp(secs, nanos)
            .ok_or_else(|| IonError::Decode("Invalid datetime timestamp".into()))?
            .naive_utc();

        Ok(chrono::DateTime::<chrono::FixedOffset>::from_naive_utc_and_offset(naive, offset))
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        const TICKS_PER_SECOND: i64 = 10_000_000;
        const UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;

        let unix_secs = self.timestamp();
        let nanos = self.timestamp_subsec_nanos() as i64;
        let ticks = UNIX_EPOCH_TICKS + unix_secs * TICKS_PER_SECOND + nanos / 100;
        let offset_minutes = self.offset().local_minus_utc() / 60;

        e.array(2)?;
        e.i64(ticks)?;
        e.i32(offset_minutes)?;
        Ok(())
    }
}
