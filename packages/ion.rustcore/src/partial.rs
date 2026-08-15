//! `Partial<T>` — written `T~` in Ion source.
//!
//! A sparse patch over a message: every field is either untouched, modified to
//! a value, or cleared.
//!
//! # Wire format
//!
//! Must stay byte-identical with `src/ion.runtime` (C#) and
//! `packages/ion.webcore.js` (TypeScript). Golden vectors live in
//! `/tests/golden/partial.golden.json`.
//!
//! ```text
//! partial := map(N)             definite length on write. Readers MUST also
//!                               accept an indefinite-length map (0xBF … 0xFF).
//!   key   := text string        the Ion field name
//!   value := null (0xF6)        the field is CLEARED
//!          | <field encoding>   the field is MODIFIED to that value
//! ```
//!
//! A field that does not appear in the map is UNTOUCHED. Unknown keys are
//! skipped on read. Fields are written in Ion declaration order — the order of
//! [`IonPartialFields::FIELD_NAMES`] — so the same patch is byte-identical in
//! every runtime.
//!
//! "Cleared" and "modified to null" are the same three bytes; `null` in the map
//! means CLEARED, so for an `Option<T>` field "cleared" and "set to `None`" are
//! the same patch.
//!
//! MIGRATION NOTE (roadmap 1.1 — explicit field indices + reserved): integer
//! keys would be smaller — `map(N) { 0: …, 3: null }` — but they need a stable
//! per-field number, which the language does not have yet. When 1.1 lands the
//! key type becomes an unsigned integer; that is a wire break and is
//! deliberately not implemented here.
//!
//! # Generated shape
//!
//! Codegen emits one patch struct per message and implements
//! [`IonPartialFields`] + [`IonPartialSchema`] for it — normally through the
//! [`ion_partial!`] macro:
//!
//! ```ignore
//! ion_rustcore::ion_partial! {
//!     pub struct VectorPatch for Vector {
//!         x: f32,
//!         y: f32,
//!         z: f32,
//!     }
//! }
//!
//! // `IonPartial<Vector>` now resolves to `VectorPatch`
//! let mut patch = ion_rustcore::IonPartial::<Vector>::default();
//! patch.x = IonPartialField::Modified(1.1);
//! patch.y = IonPartialField::Removed;
//! ```

use crate::types::IonError;
use minicbor::{Decoder, Encoder};

// ═══════════════════════════════════════════════════════════════════
// IonPartialState / IonPartialField
// ═══════════════════════════════════════════════════════════════════

/// Mirrors `ion.runtime.PartialState`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IonPartialState {
    None,
    Modified,
    Removed,
}

/// One field of a patch. Mirrors `ion.runtime.PartialField<T>`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum IonPartialField<T> {
    /// Untouched — the field is not written and will not appear in the map.
    None,
    /// Modified to a value.
    Modified(T),
    /// Cleared — written as CBOR null.
    Removed,
}

impl<T> Default for IonPartialField<T> {
    fn default() -> Self {
        IonPartialField::None
    }
}

impl<T> IonPartialField<T> {
    pub fn modified(value: T) -> Self {
        IonPartialField::Modified(value)
    }

    pub fn removed() -> Self {
        IonPartialField::Removed
    }

    pub fn untouched() -> Self {
        IonPartialField::None
    }

    pub fn state(&self) -> IonPartialState {
        match self {
            IonPartialField::None => IonPartialState::None,
            IonPartialField::Modified(_) => IonPartialState::Modified,
            IonPartialField::Removed => IonPartialState::Removed,
        }
    }

    pub fn is_untouched(&self) -> bool {
        matches!(self, IonPartialField::None)
    }

    pub fn is_modified(&self) -> bool {
        matches!(self, IonPartialField::Modified(_))
    }

    pub fn is_removed(&self) -> bool {
        matches!(self, IonPartialField::Removed)
    }

    /// The new value, if the field was modified.
    pub fn value(&self) -> Option<&T> {
        match self {
            IonPartialField::Modified(v) => Some(v),
            _ => None,
        }
    }

    pub fn into_value(self) -> Option<T> {
        match self {
            IonPartialField::Modified(v) => Some(v),
            _ => None,
        }
    }

    /// Applies the patch to a slot: modified overwrites, cleared resets to
    /// `Default`, untouched leaves it alone.
    pub fn apply(self, slot: &mut T)
    where
        T: Default,
    {
        match self {
            IonPartialField::None => {}
            IonPartialField::Modified(v) => *slot = v,
            IonPartialField::Removed => *slot = T::default(),
        }
    }
}

impl<T> From<Option<T>> for IonPartialField<T> {
    /// `Some(v)` becomes `Modified(v)`, `None` becomes `None` (untouched).
    /// Use [`IonPartialField::Removed`] explicitly to clear a field.
    fn from(value: Option<T>) -> Self {
        match value {
            Some(v) => IonPartialField::Modified(v),
            Option::None => IonPartialField::None,
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
// Schema traits — implemented by generated code
// ═══════════════════════════════════════════════════════════════════

/// The per-message field schema of a patch struct. Generated by codegen (see
/// [`ion_partial!`]); the codec in [`read_partial`] / [`write_partial`] is
/// driven entirely by this.
///
/// The names in [`FIELD_NAMES`](IonPartialFields::FIELD_NAMES) are the CBOR map
/// keys *and* the field order used on write, so they must be the Ion field
/// names in Ion declaration order.
pub trait IonPartialFields: Default + Sized {
    /// Ion field names, in Ion declaration order.
    const FIELD_NAMES: &'static [&'static str];

    /// Patch state of a field. Unknown names report [`IonPartialState::None`].
    fn ion_partial_state(&self, name: &str) -> IonPartialState;

    /// Writes the map *value* of a modified field (the key is written by the codec).
    fn ion_partial_write_field(
        &self,
        name: &str,
        e: &mut Encoder<Vec<u8>>,
    ) -> Result<(), IonError>;

    /// Reads a non-null map value and records the field as modified.
    fn ion_partial_read_field(
        &mut self,
        name: &str,
        d: &mut Decoder<'_>,
    ) -> Result<(), IonError>;

    /// Records the field as cleared.
    fn ion_partial_clear_field(&mut self, name: &str);
}

/// Binds a message type to its patch struct, so `IonPartial<Vector>` names the
/// generated `VectorPatch`.
pub trait IonPartialSchema {
    type Patch: IonPartialFields;
}

/// `Partial<T>` — the patch type of the message `T`.
///
/// This is a projection, not a wrapper: `IonPartial<Vector>` *is* the generated
/// `VectorPatch`, with one [`IonPartialField`] per message field.
pub type IonPartial<T> = <T as IonPartialSchema>::Patch;

// ═══════════════════════════════════════════════════════════════════
// Codec
// ═══════════════════════════════════════════════════════════════════

/// Writes a patch as a definite-length CBOR map, in declaration order.
pub fn write_partial<P: IonPartialFields>(
    e: &mut Encoder<Vec<u8>>,
    patch: &P,
) -> Result<(), IonError> {
    let present = P::FIELD_NAMES
        .iter()
        .filter(|name| patch.ion_partial_state(name) != IonPartialState::None)
        .count();

    e.map(present as u64)?;

    for name in P::FIELD_NAMES {
        match patch.ion_partial_state(name) {
            IonPartialState::None => continue,
            IonPartialState::Removed => {
                e.str(name)?;
                // Removal is decided by the state, never by null-checking a value.
                e.null()?;
            }
            IonPartialState::Modified => {
                e.str(name)?;
                patch.ion_partial_write_field(name, e)?;
            }
        }
    }

    Ok(())
}

/// Reads a patch from a CBOR map. Accepts both definite- and
/// indefinite-length maps; unknown keys are skipped.
pub fn read_partial<P: IonPartialFields>(d: &mut Decoder<'_>) -> Result<P, IonError> {
    let mut patch = P::default();

    match d.map()? {
        Some(len) => {
            for _ in 0..len {
                read_partial_entry(d, &mut patch)?;
            }
        }
        None => loop {
            if matches!(d.datatype()?, minicbor::data::Type::Break) {
                // consume the break byte
                d.set_position(d.position() + 1);
                break;
            }
            read_partial_entry(d, &mut patch)?;
        },
    }

    Ok(patch)
}

fn read_partial_entry<P: IonPartialFields>(
    d: &mut Decoder<'_>,
    patch: &mut P,
) -> Result<(), IonError> {
    let key = d.str()?;

    if !P::FIELD_NAMES.contains(&key) {
        d.skip()?;
        return Ok(());
    }

    if matches!(d.datatype()?, minicbor::data::Type::Null) {
        d.null()?;
        patch.ion_partial_clear_field(key);
        return Ok(());
    }

    patch.ion_partial_read_field(key, d)
}

/// Encodes a patch to a fresh byte buffer. Convenience for tests and callers
/// that are not already inside an encoder.
pub fn encode_partial<P: IonPartialFields>(patch: &P) -> Result<Vec<u8>, IonError> {
    let mut e = Encoder::new(Vec::new());
    write_partial(&mut e, patch)?;
    Ok(e.into_writer())
}

/// Decodes a patch from a byte buffer.
pub fn decode_partial<P: IonPartialFields>(bytes: &[u8]) -> Result<P, IonError> {
    let mut d = Decoder::new(bytes);
    read_partial(&mut d)
}

// NOTE: `IonFormat` cannot be blanket-implemented for every `P: IonPartialFields`
// — it would collide with the concrete impls for `bool`, `String`, … — so the
// generated patch struct implements `IonFormat` itself by delegating to
// `read_partial` / `write_partial`. `ion_partial!` writes that impl for you.

// ═══════════════════════════════════════════════════════════════════
// ion_partial! — the codegen entry point
// ═══════════════════════════════════════════════════════════════════

/// Declares the patch struct for a message and wires up the whole `Partial<T>`
/// machinery: the struct itself, [`IonPartialFields`], [`IonPartialSchema`]
/// (so `IonPartial<Msg>` resolves to it) and [`IonFormat`].
///
/// Field idents are used verbatim as the CBOR map keys, so they must be the Ion
/// field names in Ion declaration order. If a field name is a Rust keyword,
/// implement [`IonPartialFields`] by hand instead — `stringify!` would keep the
/// `r#` prefix.
///
/// ```ignore
/// ion_rustcore::ion_partial! {
///     /// Sparse patch over `GoldenPatchTarget`.
///     pub struct GoldenPatchTargetPatch for GoldenPatchTarget {
///         n: i32,
///         f: f32,
///         s: String,
///         items: Vec<i32>,
///         note: Option<String>,
///     }
/// }
/// ```
#[macro_export]
macro_rules! ion_partial {
    (
        $(#[$meta:meta])*
        $vis:vis struct $patch:ident for $msg:ty {
            $( $(#[$fmeta:meta])* $field:ident : $ty:ty ),* $(,)?
        }
    ) => {
        $(#[$meta])*
        #[derive(Debug, Clone, Default, PartialEq)]
        $vis struct $patch {
            $( $(#[$fmeta])* pub $field: $crate::IonPartialField<$ty>, )*
        }

        impl $crate::IonPartialFields for $patch {
            const FIELD_NAMES: &'static [&'static str] = &[ $( stringify!($field) ),* ];

            fn ion_partial_state(&self, name: &str) -> $crate::IonPartialState {
                match name {
                    $( stringify!($field) => self.$field.state(), )*
                    _ => $crate::IonPartialState::None,
                }
            }

            fn ion_partial_write_field(
                &self,
                name: &str,
                e: &mut $crate::Encoder<Vec<u8>>,
            ) -> ::core::result::Result<(), $crate::IonError> {
                match name {
                    $(
                        stringify!($field) => match &self.$field {
                            $crate::IonPartialField::Modified(v) =>
                                <$ty as $crate::formatter::IonFormat>::ion_write(v, e),
                            _ => { e.null()?; Ok(()) }
                        },
                    )*
                    _ => Err($crate::IonError::Encode(format!(
                        "unknown Partial field '{}' on {}", name, stringify!($patch)))),
                }
            }

            fn ion_partial_read_field(
                &mut self,
                name: &str,
                d: &mut $crate::Decoder<'_>,
            ) -> ::core::result::Result<(), $crate::IonError> {
                match name {
                    $(
                        stringify!($field) => {
                            self.$field = $crate::IonPartialField::Modified(
                                <$ty as $crate::formatter::IonFormat>::ion_read(d)?);
                            Ok(())
                        }
                    )*
                    _ => { d.skip()?; Ok(()) }
                }
            }

            fn ion_partial_clear_field(&mut self, name: &str) {
                match name {
                    $( stringify!($field) => self.$field = $crate::IonPartialField::Removed, )*
                    _ => {}
                }
            }
        }

        impl $crate::IonPartialSchema for $msg {
            type Patch = $patch;
        }

        impl $crate::formatter::IonFormat for $patch {
            fn ion_read(d: &mut $crate::Decoder<'_>) -> ::core::result::Result<Self, $crate::IonError> {
                $crate::partial::read_partial::<Self>(d)
            }

            fn ion_write(&self, e: &mut $crate::Encoder<Vec<u8>>) -> ::core::result::Result<(), $crate::IonError> {
                $crate::partial::write_partial(e, self)
            }
        }
    };
}
