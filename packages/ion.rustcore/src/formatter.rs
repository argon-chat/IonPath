use crate::types::IonError;
use minicbor::{data::Type, Decoder, Encoder};
use std::cell::Cell;

// ═══════════════════════════════════════════════════════════════════
// Reader limits — the numbers a reader applies to a payload it did not produce
// ═══════════════════════════════════════════════════════════════════

/// The maximum number of simultaneously open CBOR containers a reader accepts.
///
/// The same number is `IonDecodeLimits.MaxDepth` in C# and `ION_MAX_DEPTH` in TypeScript. It is
/// part of the wire contract, not a local tuning knob: a payload one runtime accepts and the next
/// refuses stops being deliverable through a mixed-language path.
///
/// **Why a limit at all.** Nesting is the one dimension of a payload that costs *stack* rather
/// than heap, and a Rust stack overflow aborts the process — it is not a `Result`, not a panic,
/// and not catchable with `catch_unwind`. It is also the cheapest thing in the format for a peer
/// to produce: 100 KB of `0x81` bytes is 100 000 levels of array, and a field the reader only
/// *skips* still has to be walked, so no knowledge of the schema is required to reach it.
///
/// 128 is chosen the way `serde_json` (128) and protobuf (100) choose theirs: an order of
/// magnitude above anything a hand-written schema nests, two orders below what threatens a native
/// stack.
pub const MAX_DEPTH: usize = 128;

/// How many bytes of element storage a reader will reserve up front, before it has read anything.
///
/// A declared length is never trusted for allocation. [`LengthOverclaim`] already rejects a count
/// larger than the remaining input, which bounds the reservation by the *payload* size; this
/// bounds it by a constant as well, so that a legitimate 8 MB frame of a 64-byte element type
/// does not reserve half a gigabyte before the first element is validated. Past this point the
/// `Vec` simply grows as elements arrive.
///
/// [`LengthOverclaim`]: crate::types::IonError::LengthOverclaim
const PREALLOC_BUDGET_BYTES: usize = 64 * 1024;

thread_local! {
    /// Open containers on the current thread's decode stack.
    ///
    /// Ambient rather than threaded through `ion_read`, because [`IonFormat::ion_read`] takes only
    /// a `Decoder` — the signature generated code is written against — and `minicbor::Decoder` is
    /// a bare cursor with no container stack of its own to ask.
    static OPEN_CONTAINERS: Cell<usize> = const { Cell::new(0) };
}

/// Marks one open container for as long as it is alive, and refuses to open one past
/// [`MAX_DEPTH`].
///
/// RAII rather than an explicit release call so the count cannot leak: `Drop` runs on the `?`
/// early return of a failed element read and on an unwind, which is exactly when an explicit
/// release would be skipped.
///
/// **Bind it to a name.** `let _depth = …` holds the container open until the end of the scope;
/// `let _ = …` drops it immediately and the container is not counted at all. That is the one way
/// to get this wrong, and it is silent, so it is `#[must_use]`.
#[must_use = "the container is only counted for as long as the guard is alive; bind it to `_depth`"]
#[derive(Debug)]
pub struct DepthGuard(());

impl DepthGuard {
    /// Opens a container, or fails with [`IonError::DepthLimit`].
    pub fn enter() -> Result<Self, IonError> {
        OPEN_CONTAINERS.with(|open| {
            let depth = open.get() + 1;
            if depth > MAX_DEPTH {
                return Err(IonError::DepthLimit { limit: MAX_DEPTH, depth });
            }
            open.set(depth);
            Ok(DepthGuard(()))
        })
    }

    /// Containers currently open on this thread. Public so a hand-written formatter that opens a
    /// container without one of the helpers below can account for it.
    pub fn open_containers() -> usize {
        OPEN_CONTAINERS.with(|open| open.get())
    }
}

impl Drop for DepthGuard {
    fn drop(&mut self) {
        OPEN_CONTAINERS.with(|open| open.set(open.get().saturating_sub(1)));
    }
}

/// Bytes left in the decoder's input. Every CBOR data item occupies at least one byte, so this is
/// an upper bound on the number of items any container starting here can actually contain.
fn remaining(d: &Decoder<'_>) -> usize {
    d.input().len().saturating_sub(d.position())
}

/// Validates a declared element count against the bytes that are actually left, and returns how
/// many elements it is worth reserving space for.
///
/// The two halves are separate on purpose. The **check** is a correctness statement — a count
/// above the remaining byte count cannot be honoured by any payload, so it is a typed
/// [`IonError::LengthOverclaim`] rather than a read that fails later somewhere less informative.
/// The **clamp** is the allocation policy: even a believable count is only reserved up to
/// [`PREALLOC_BUDGET_BYTES`], and the rest is grown as elements arrive.
fn checked_capacity<T>(
    d: &Decoder<'_>,
    declared: u64,
    context: &'static str,
) -> Result<usize, IonError> {
    let available = remaining(d);
    if declared > available as u64 {
        return Err(IonError::LengthOverclaim { context, declared, available });
    }
    Ok(prealloc::<T>(declared as usize))
}

/// [`PREALLOC_BUDGET_BYTES`] worth of `T`, or `count`, whichever is smaller.
fn prealloc<T>(count: usize) -> usize {
    let element = std::mem::size_of::<T>().max(1);
    count.min(PREALLOC_BUDGET_BYTES / element)
}

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
///
/// The declared length is **never** trusted for allocation. `9bffffffffffffffff` is a nine-byte
/// array header claiming 2^64-1 items; handing that to `Vec::with_capacity` aborts the process on
/// a capacity overflow, which crosses an async boundary as a task abort rather than as a decode
/// failure and takes the connection down with it. The count is checked against the bytes that
/// remain — every item costs at least one byte — and the reservation is clamped on top of that;
/// see [`checked_capacity`].
pub fn read_array<T: IonFormat>(d: &mut Decoder<'_>) -> Result<Vec<T>, IonError> {
    let len = d.array()?.ok_or(IonError::IndefiniteArray)?;
    let _depth = DepthGuard::enter()?;
    let mut result = Vec::with_capacity(checked_capacity::<T>(d, len, "Array<T>")?);
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

/// Opens a message's positional field array: its declared item count, and the guard that counts
/// it towards [`MAX_DEPTH`].
///
/// A message is a CBOR array with no end marker of its own, so `total_len` is the only thing
/// standing between a short payload and a reader walking into the bytes behind it — which is why
/// it is returned rather than discarded, and why [`skip_remaining`] needs it at the end.
///
/// **The guard is the reason this exists.** The collection helpers count the containers they open,
/// but a message opens its own field array directly, so without this the depth Rust counts is
/// *collections only* while C#'s `CborReader.CurrentDepth` and TypeScript's frame stack count
/// every container. A recursive message nests two containers per level — its field array plus the
/// collection carrying the recursion — so the two counts differ by a factor of two, and the shared
/// limit of 128 stops meaning the same thing in the three runtimes.
///
/// Bind the guard for the whole body of `ion_read`; it is released when the message is done:
///
/// ```ignore
/// fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
///     let (len, _depth) = ion_rustcore::formatter::read_message_header(d, "AppendedV1")?;
///     let a = <i32 as IonFormat>::ion_read(d)?;
///     let b = <String as IonFormat>::ion_read(d)?;
///     ion_rustcore::formatter::skip_remaining(d, len, 2)?;
///     Ok(Self { a, b })
/// }
/// ```
///
/// An indefinite-length message array is [`IonError::IndefiniteArray`]: the trailing-skip
/// arithmetic is computed from the declared length, so there is nothing to compute it from.
pub fn read_message_header(
    d: &mut Decoder<'_>,
    _message_type: &'static str,
) -> Result<(u64, DepthGuard), IonError> {
    let len = d.array()?.ok_or(IonError::IndefiniteArray)?;
    Ok((len, DepthGuard::enter()?))
}

/// Skip remaining fields in a CBOR array (for forward-compatibility).
/// If `total_len > expected_fields`, skips the extra items.
///
/// The skip goes through [`skip_value`], not `minicbor::Decoder::skip`, so that a field this
/// revision of the schema does not even look at is still bounded by [`MAX_DEPTH`]. A skipped
/// field is the cheapest path an unknown peer has to the reader's stack, and it is the one that
/// is easy to miss precisely because nothing about it is typed.
pub fn skip_remaining(d: &mut Decoder<'_>, total_len: u64, expected_fields: u64) -> Result<(), IonError> {
    // Saturating here was the last silent-corruption path in this runtime. A message declaring
    // fewer items than the schema reads meant the fields past the end came from outside the array
    // — minicbor has no container stack to refuse them — and the saturating subtraction then made
    // the shortfall invisible. C# and TypeScript both report it; this is the same failure.
    if total_len < expected_fields {
        return Err(IonError::FieldCount { declared: total_len, expected: expected_fields });
    }

    let extra = total_len - expected_fields;
    for _ in 0..extra {
        skip_value(d)?;
    }
    Ok(())
}

/// Skips exactly one CBOR data item — however deeply nested — enforcing [`MAX_DEPTH`].
///
/// **Why not `minicbor::Decoder::skip`.** That one is iterative, which is why 100 000 levels of
/// nesting never overflowed the stack here the way TypeScript's recursive `skipValue` did; but it
/// is iterative by *flattening* the counts, so it has no notion of depth to bound and walks any
/// nesting it is handed. Depth is the limit that has to be enforced on the skip path, because a
/// trailing field a reader skips is reachable from any peer without knowing the schema at all.
///
/// This is a walk over an explicit stack of "items still outstanding at this level", so the
/// recursion lives in the heap where it can be counted, and `stack.len()` *is* the current depth.
/// The ambient [`DepthGuard::open_containers`] count is added to it, so a skip that starts three
/// containers deep is bounded at 128 total rather than at 128 more.
pub fn skip_value(d: &mut Decoder<'_>) -> Result<(), IonError> {
    // `Some(n)`: n items still outstanding in a definite-length container.
    // `None`:    an indefinite-length container, which ends at its break byte.
    // The bottom frame is the value itself rather than a container, so it does not count as depth.
    let mut stack: Vec<Option<u64>> = vec![Some(1)];

    while let Some(frame) = stack.last().copied() {
        match frame {
            Some(0) => {
                stack.pop();
                continue;
            }
            Some(n) => *stack.last_mut().expect("frame checked above") = Some(n - 1),
            None => {
                if d.datatype()? == Type::Break {
                    d.skip()?; // the break
                    stack.pop();
                    continue;
                }
            }
        }

        // A tag prefixes exactly one data item, so it is not an item of the enclosing container
        // in its own right. Peel any run of them and skip whatever they decorate.
        while d.datatype()? == Type::Tag {
            d.tag()?;
        }

        let ty = d.datatype()?;
        let opening = match ty {
            Type::Array | Type::ArrayIndef => d.array()?,
            // A map's items are its keys and values both.
            Type::Map | Type::MapIndef => d.map()?.map(|n| n.saturating_mul(2)),
            Type::Break => {
                // Only reachable with a definite-length frame outstanding: the container promised
                // more items and delivered a break instead.
                return Err(IonError::MalformedValue {
                    ion_type: "skipped value",
                    reason: "a definite-length CBOR container ended at a break byte with items \
                             still outstanding"
                        .to_owned(),
                });
            }
            // Scalars, byte strings and text strings — including the chunked forms, whose chunks
            // `minicbor`'s own skip walks iteratively and which add no nesting.
            _ => {
                d.skip()?;
                continue;
            }
        };

        let depth = DepthGuard::open_containers() + stack.len();
        if depth > MAX_DEPTH {
            return Err(IonError::DepthLimit { limit: MAX_DEPTH, depth });
        }
        // An empty definite-length container needs no frame at all; pushing one would only be
        // popped on the next iteration.
        if opening != Some(0) {
            stack.push(opening);
        }
    }

    Ok(())
}

// ═══════════════════════════════════════════════════════════════════
// Union<U> — the [index, payload] envelope
// ═══════════════════════════════════════════════════════════════════

/// The number of items a union envelope carries, in every revision of every union.
pub const UNION_ENVELOPE_ITEMS: u64 = 2;

/// Opens a union envelope and returns its case index.
///
/// **This is the whole of the envelope's structural contract, and it belongs here rather than in
/// generated code.** A union is written as `[index, payload]` — exactly two items, in every
/// revision of every union, because a union grows by *adding cases* and a case grows inside its
/// payload, which is a message and skips its own trailing fields. There is therefore no
/// forward-compatibility reading of a longer envelope; a third item is either a bug in the writer
/// or a probe.
///
/// The reader that skipped this check read the index and the payload and walked away, leaving the
/// stray item in the stream. A CBOR decoder is a flat cursor with no container stack, so nothing
/// downstream notices: the **next field of the enclosing message** reads the union's leftover as
/// its own value. `[[0, [1,"b"], 9], 5]` decodes with `n = 9`. That is silent corruption of a
/// value the schema says is well-typed, which is worse than any failure.
///
/// A length other than two is [`IonError::UnionEnvelope`] and an indefinite length is
/// [`IonError::IndefiniteArray`] — both raised **before** the index is read, so the cursor is
/// never left half-way into an envelope whose shape is already known to be wrong.
///
/// The returned [`DepthGuard`] counts the envelope towards [`MAX_DEPTH`] for as long as it is
/// alive; bind it for the whole body of `ion_read`, as [`read_message_header`] describes.
///
/// Generated code replaces
/// ```ignore
/// d.array()?;
/// let union_index = d.u32()?;
/// ```
/// with
/// ```ignore
/// let (union_index, _depth) =
///     ion_rustcore::formatter::read_union_envelope(d, "ShapeV1")?;
/// ```
pub fn read_union_envelope(
    d: &mut Decoder<'_>,
    union_type: &'static str,
) -> Result<(u32, DepthGuard), IonError> {
    let declared = d.array()?.ok_or(IonError::IndefiniteArray)?;
    if declared != UNION_ENVELOPE_ITEMS {
        return Err(IonError::UnionEnvelope { union_type, actual_items: declared });
    }
    let depth = DepthGuard::enter()?;
    Ok((d.u32()?, depth))
}

/// Writes the union envelope header and its case index. The payload follows.
pub fn write_union_envelope(
    e: &mut Encoder<Vec<u8>>,
    union_index: u32,
) -> Result<(), IonError> {
    e.array(UNION_ENVELOPE_ITEMS)?;
    e.u32(union_index)?;
    Ok(())
}

// ═══════════════════════════════════════════════════════════════════
// Open enums — an unknown member is carried, not rejected
// ═══════════════════════════════════════════════════════════════════

/// An Ion `enum` that tolerates a value this revision of the schema does not declare.
///
/// **The forward-compatibility contract.** Adding a member to an enum is documented as a safe
/// schema change, and it can only be one if an older reader survives the new value. There are
/// exactly two failure modes a runtime can have here and Ion has had both: reject the payload
/// (an old client hard-fails the moment the server deploys a new member — the rolling-deploy
/// blocker), or cast the raw number into the enum type anyway (an out-of-range value that falls
/// through every `match` arm and flows on silently). Carrying it in an explicit variant is the
/// only answer that is neither.
///
/// Two cases, and they are not the same:
///
/// * A value that **does not fit the declared base type** — `256` or `-1` into a `u1`-based enum
///   — is a typed decode error. It is not a newer schema revision, because no revision of this
///   enum could ever have written it: the base type is part of the enum's identity and is pinned
///   in the schema lock. That check is [`Self::Repr`]'s own formatter and needs nothing here.
/// * A value that **fits the base type but names no declared member** is accepted, preserved
///   verbatim, and re-encoded byte-identically. `Trial = 2` written by a v2 peer and read by a v1
///   reader comes back as `Unknown(2)` and writes back as `02`.
///
/// # Required shape of a generated enum
///
/// ```ignore
/// #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
/// #[non_exhaustive]
/// pub enum Tier {
///     Free,
///     Paid,
///     Trial,
///     /// A value of the base type this revision of the schema does not declare.
///     Unknown(u8),
/// }
///
/// impl ion_rustcore::IonOpenEnum for Tier {
///     type Repr = u8;
///     const ION_ENUM_NAME: &'static str = "Tier";
///
///     fn from_ion_repr(value: u8) -> Self {
///         match value {
///             0 => Self::Free,
///             1 => Self::Paid,
///             2 => Self::Trial,
///             other => Self::Unknown(other),
///         }
///     }
///
///     fn to_ion_repr(&self) -> u8 {
///         match self {
///             Self::Free => 0,
///             Self::Paid => 1,
///             Self::Trial => 2,
///             Self::Unknown(raw) => *raw,
///         }
///     }
///
///     fn ion_unknown_value(&self) -> Option<u8> {
///         match self { Self::Unknown(raw) => Some(*raw), _ => None }
///     }
/// }
///
/// impl ion_rustcore::formatter::IonFormat for Tier {
///     fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
///         ion_rustcore::formatter::read_open_enum::<Self>(d)
///     }
///     fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
///         ion_rustcore::formatter::write_open_enum(e, self)
///     }
/// }
/// ```
///
/// [`ion_open_enum!`](crate::ion_open_enum) writes all of that from the member list.
///
/// **`#[repr(u8)]` and the discriminant cast are gone, and so is the `unsafe`.** A data-carrying
/// variant cannot be `as`-cast to its base type, so the mapping is spelled out in
/// [`Self::to_ion_repr`] instead — which also retires the
/// `Ok(unsafe { std::mem::transmute(x) })` the old `TryFrom` used to reach a discriminant it had
/// just proved valid. The base type survives as [`Self::Repr`], which is what the wire cares
/// about.
pub trait IonOpenEnum: Sized {
    /// The enum's declared base type: `u8`/`u16`/`u32`/`u64` or `i8`/`i16`/`i32`/`i64` for Ion's
    /// `u1`…`u8` and `i1`…`i8`. Its [`IonFormat`] is what range-checks the wire value.
    type Repr: IonFormat + Copy + PartialEq;

    /// The enum's Ion name, for error messages and diagnostics.
    const ION_ENUM_NAME: &'static str;

    /// Maps a base-type value onto a member, or onto the catch-all variant. Total by
    /// construction: every value of `Repr` maps to something.
    fn from_ion_repr(value: Self::Repr) -> Self;

    /// Recovers the base-type value to write — the declared member's number, or, for the
    /// catch-all variant, the exact number that was read. This is what makes an unknown value
    /// round-trip byte-identically.
    fn to_ion_repr(&self) -> Self::Repr;

    /// `Some(raw)` if this is the catch-all variant, `None` for a declared member.
    ///
    /// This is how a caller asks "did the peer send something this build does not understand?"
    /// without matching on a variant it may not know about.
    fn ion_unknown_value(&self) -> Option<Self::Repr>;

    /// Whether this value is a member this revision of the schema declares.
    fn is_known(&self) -> bool {
        self.ion_unknown_value().is_none()
    }
}

/// Reads an open enum: the base type's own formatter, then [`IonOpenEnum::from_ion_repr`].
///
/// A value outside the base type is that formatter's error and is left exactly as it was — an
/// out-of-range number is a malformed payload, not a newer schema.
pub fn read_open_enum<T: IonOpenEnum>(d: &mut Decoder<'_>) -> Result<T, IonError> {
    Ok(T::from_ion_repr(<T::Repr as IonFormat>::ion_read(d)?))
}

/// Writes an open enum as its base-type value — the declared member's number, or the preserved
/// unknown one.
pub fn write_open_enum<T: IonOpenEnum>(
    e: &mut Encoder<Vec<u8>>,
    value: &T,
) -> Result<(), IonError> {
    value.to_ion_repr().ion_write(e)
}

/// Declares an open Ion enum with its [`IonOpenEnum`] and [`IonFormat`] impls.
///
/// One macro call per generated enum, so the shape cannot drift between the generator and this
/// runtime. Doc comments and attributes — `#[deprecated]` on the enum or on any member — pass
/// through to the generated item.
///
/// ```
/// ion_rustcore::ion_open_enum! {
///     /// Where a cached entry lives.
///     pub enum Tier: u8 {
///         Free = 0,
///         Paid = 1,
///         Trial = 2,
///     }
/// }
///
/// use ion_rustcore::IonOpenEnum;
/// assert_eq!(Tier::from_ion_repr(1), Tier::Paid);
/// assert_eq!(Tier::from_ion_repr(7), Tier::Unknown(7));
/// assert_eq!(Tier::Unknown(7).to_ion_repr(), 7);
/// assert!(!Tier::Unknown(7).is_known());
/// ```
#[macro_export]
macro_rules! ion_open_enum {
    (
        $(#[$enum_meta:meta])*
        $vis:vis enum $name:ident : $repr:ty {
            $( $(#[$member_meta:meta])* $member:ident = $value:expr ),* $(,)?
        }
    ) => {
        $(#[$enum_meta])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
        #[non_exhaustive]
        $vis enum $name {
            $( $(#[$member_meta])* $member, )*
            /// A value of the base type this revision of the schema does not declare.
            ///
            /// Preserved verbatim so that it re-encodes byte-identically: adding a member is a
            /// safe schema change only if an older reader can carry the new value through.
            Unknown($repr),
        }

        #[allow(deprecated)]
        impl $crate::IonOpenEnum for $name {
            type Repr = $repr;
            const ION_ENUM_NAME: &'static str = stringify!($name);

            fn from_ion_repr(value: $repr) -> Self {
                match value {
                    $( v if v == $value => Self::$member, )*
                    other => Self::Unknown(other),
                }
            }

            fn to_ion_repr(&self) -> $repr {
                match self {
                    $( Self::$member => $value, )*
                    Self::Unknown(raw) => *raw,
                }
            }

            fn ion_unknown_value(&self) -> ::core::option::Option<$repr> {
                match self {
                    Self::Unknown(raw) => ::core::option::Option::Some(*raw),
                    _ => ::core::option::Option::None,
                }
            }
        }

        #[allow(deprecated)]
        impl $crate::formatter::IonFormat for $name {
            fn ion_read(
                d: &mut $crate::Decoder<'_>,
            ) -> ::core::result::Result<Self, $crate::IonError> {
                $crate::formatter::read_open_enum::<Self>(d)
            }

            fn ion_write(
                &self,
                e: &mut $crate::Encoder<::std::vec::Vec<u8>>,
            ) -> ::core::result::Result<(), $crate::IonError> {
                $crate::formatter::write_open_enum(e, self)
            }
        }
    };
}

// ═══════════════════════════════════════════════════════════════════
// T[N] — fixed-size arrays
// ═══════════════════════════════════════════════════════════════════

/// Reads a CBOR array that must hold exactly `n` items.
///
/// **Rule: a definite-length CBOR array of exactly `N` items.** Any other length is
/// [`IonError::FixedArrayLength`], which names **both** the declared `N` and the length received
/// — that check is the entire point of the feature, and knowing only that the length was wrong
/// does not tell a caller whether the peer is on an older schema revision or the payload was
/// truncated.
///
/// `n` is a **parameter**, never baked into a per-length type, so one function serves every
/// declared `N`. An indefinite-length array is accepted as long as it turns out to hold exactly
/// `n` items. Extra items are *not* skipped for forward compatibility, unlike a message's trailing
/// fields: the declared length is the contract.
///
/// **No `u1[N]` special case:** a fixed array of `u1` is an array of `N` CBOR integers, not a byte
/// string. Collapsing it would make the wire type of a fixed array depend on its element type,
/// which no reader could predict from the array shape alone.
///
/// Golden vectors: `/tests/golden/collections.golden.json`, section `fixedArray`.
pub fn read_fixed_array<T: IonFormat>(d: &mut Decoder<'_>, n: usize) -> Result<Vec<T>, IonError> {
    match d.array()? {
        Some(declared) => {
            let declared = declared as usize;
            if declared != n {
                return Err(IonError::FixedArrayLength { expected: n, actual: declared });
            }
            let _depth = DepthGuard::enter()?;
            // `n` comes from the schema, not from the payload, so it cannot be overclaimed — but
            // it is still reserved lazily, so that a declared `T[1_000_000]` does not allocate
            // before the first element is known to be readable.
            let mut result = Vec::with_capacity(prealloc::<T>(n));
            for _ in 0..n {
                result.push(T::ion_read(d)?);
            }
            Ok(result)
        }
        None => {
            let _depth = DepthGuard::enter()?;
            let mut result = Vec::with_capacity(prealloc::<T>(n));
            while d.datatype()? != minicbor::data::Type::Break {
                // Stop before running past N so a hostile payload cannot make the reader allocate
                // without bound; the count is reported as at-least-N+1.
                if result.len() == n {
                    return Err(IonError::FixedArrayLength { expected: n, actual: n + 1 });
                }
                result.push(T::ion_read(d)?);
            }
            d.skip()?; // the break
            if result.len() != n {
                return Err(IonError::FixedArrayLength { expected: n, actual: result.len() });
            }
            Ok(result)
        }
    }
}

/// Writes exactly `n` items as a definite-length CBOR array.
///
/// A mismatched slice is [`IonError::FixedArrayLength`] too, because writers are exact.
pub fn write_fixed_array<T: IonFormat>(
    e: &mut Encoder<Vec<u8>>,
    values: &[T],
    n: usize,
) -> Result<(), IonError> {
    if values.len() != n {
        return Err(IonError::FixedArrayLength { expected: n, actual: values.len() });
    }
    e.array(n as u64)?;
    for v in values {
        v.ion_write(e)?;
    }
    Ok(())
}

// ═══════════════════════════════════════════════════════════════════
// Canonical CBOR ordering — RFC 8949 §4.2.1
// ═══════════════════════════════════════════════════════════════════

/// Compares two encoded CBOR data items by their **byte length first**, and only then
/// lexicographically by their bytes.
///
/// This is the total order that makes `Map<K,V>` and `Set<T>` byte-identical across runtimes. A
/// Rust `HashMap`, a C# `Dictionary` and a JavaScript `Map` have three different iteration orders;
/// without a total order on the wire the same logical map produces three different byte strings,
/// which defeats byte identity and any future signing, hashing or content-addressing of payloads.
///
/// **Length-first is not plain bytewise comparison.** Integer keys make the difference visible:
/// `-1` encodes as `20` (1 byte) and `1000` as `1903e8` (3 bytes), so length-first puts `-1`
/// first while a bytewise-only sort puts `1000` first, because `0x19 < 0x20`.
pub fn canonical_cbor_cmp(a: &[u8], b: &[u8]) -> std::cmp::Ordering {
    a.len().cmp(&b.len()).then_with(|| a.cmp(b))
}

/// Encodes one value with its own formatter into a standalone buffer.
///
/// Canonical ordering is defined over encoded bytes, and the bytes only exist once the value has
/// been written, so each key/element is encoded here first and the sorted results are spliced in
/// afterwards.
pub fn encode_item<T: IonFormat>(value: &T) -> Result<Vec<u8>, IonError> {
    let mut e = Encoder::new(Vec::new());
    value.ion_write(&mut e)?;
    Ok(e.into_writer())
}

fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

// ═══════════════════════════════════════════════════════════════════
// Map<K,V>
// ═══════════════════════════════════════════════════════════════════

/// Writes a `Map<K,V>` as a definite-length CBOR map whose keys are sorted in canonical CBOR
/// order ([`canonical_cbor_cmp`]).
///
/// Sorting is what makes a Rust `HashMap`, a C# `Dictionary` and a JavaScript `Map` — three
/// different iteration orders — produce the same bytes for the same logical map. Values are
/// written by their own formatter and take no part in the ordering.
///
/// **Key types.** The compiler restricts keys to scalar / `string` / `guid` / enum types. This
/// function does not re-validate that: it encodes whatever `K`'s formatter produces and sorts by
/// the resulting bytes, so a composite key still yields a deterministic map. Duplicate detection
/// on the read side then follows `K`'s own `Eq`/`Hash`, so the compiler restriction is the real
/// guarantee.
///
/// Golden vectors: `/tests/golden/collections.golden.json`, section `map`.
pub fn write_map<'a, K, V, I>(e: &mut Encoder<Vec<u8>>, entries: I) -> Result<(), IonError>
where
    K: IonFormat + 'a,
    V: IonFormat + 'a,
    I: IntoIterator<Item = (&'a K, &'a V)>,
{
    // Only the KEYS need pre-encoding: once the order is decided the values can be written live.
    let mut ordered: Vec<(Vec<u8>, &V)> = Vec::new();
    for (key, value) in entries {
        ordered.push((encode_item(key)?, value));
    }
    ordered.sort_by(|a, b| canonical_cbor_cmp(&a.0, &b.0));

    e.map(ordered.len() as u64)?;
    for (key, value) in ordered {
        e.writer_mut().extend_from_slice(&key);
        value.ion_write(e)?;
    }
    Ok(())
}

/// Reads a `Map<K,V>` into any map-like collection.
///
/// Accepts a definite- **or** indefinite-length CBOR map and any wire order; re-encoding
/// canonicalises both. The indefinite case is called out explicitly because the `Partial<T>`
/// formatter had exactly that bug — a null length treated as zero read no entries and then
/// desynchronised the reader on the closing break.
///
/// Duplicate keys are **rejected** with [`IonError::DuplicateMapKey`]. Last-wins and first-wins
/// both make the decoded value depend on the order entries happen to appear in, which is the very
/// non-determinism the canonical ordering exists to remove.
pub fn read_map<K, V, M>(d: &mut Decoder<'_>) -> Result<M, IonError>
where
    K: IonFormat,
    V: IonFormat,
    M: Default + MapInsert<K, V>,
{
    let mut result = M::default();
    let _depth = DepthGuard::enter()?;

    let read_entry = |d: &mut Decoder<'_>, result: &mut M| -> Result<(), IonError> {
        let key = K::ion_read(d)?;
        let value = V::ion_read(d)?;
        // Report the key by its canonical encoded bytes: that is the documented identity rule,
        // and it avoids forcing a `Debug` bound onto every generated key type.
        let hex = to_hex(&encode_item(&key)?);
        if result.insert_unique(key, value).is_err() {
            return Err(IonError::DuplicateMapKey { key_hex: hex });
        }
        Ok(())
    };

    match d.map()? {
        Some(n) => {
            for _ in 0..n {
                read_entry(d, &mut result)?;
            }
        }
        None => {
            while d.datatype()? != minicbor::data::Type::Break {
                read_entry(d, &mut result)?;
            }
            d.skip()?; // the break
        }
    }

    Ok(result)
}

/// Insertion with duplicate detection, so [`read_map`] can fill a `HashMap` or a `BTreeMap`
/// without either of them silently overwriting a repeated key.
pub trait MapInsert<K, V> {
    /// Inserts, or returns `Err(())` if the key was already present.
    fn insert_unique(&mut self, key: K, value: V) -> Result<(), ()>;
}

impl<K: Eq + std::hash::Hash, V, S: std::hash::BuildHasher> MapInsert<K, V>
    for std::collections::HashMap<K, V, S>
{
    fn insert_unique(&mut self, key: K, value: V) -> Result<(), ()> {
        match self.insert(key, value) {
            Some(_) => Err(()),
            None => Ok(()),
        }
    }
}

impl<K: Ord, V> MapInsert<K, V> for std::collections::BTreeMap<K, V> {
    fn insert_unique(&mut self, key: K, value: V) -> Result<(), ()> {
        match self.insert(key, value) {
            Some(_) => Err(()),
            None => Ok(()),
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
// Set<T> — CBOR tag 258
// ═══════════════════════════════════════════════════════════════════

/// CBOR tag 258 — the IANA-registered "set" tag.
pub const SET_TAG: u64 = 258;

/// Writes a `Set<T>` as CBOR tag 258 wrapping a definite-length array whose elements are sorted in
/// canonical CBOR order.
///
/// Sorting, not insertion order, is what makes two sets built by inserting the same elements in
/// different orders produce identical bytes.
///
/// **The tag is mandatory in both directions.** It is what distinguishes `Set<T>` from
/// `Array<T>` on the wire, and they are distinct Ion types with distinct schema-lock entries.
///
/// Golden vectors: `/tests/golden/collections.golden.json`, section `set`.
pub fn write_set<'a, T, I>(e: &mut Encoder<Vec<u8>>, elements: I) -> Result<(), IonError>
where
    T: IonFormat + 'a,
    I: IntoIterator<Item = &'a T>,
{
    let mut ordered: Vec<Vec<u8>> = Vec::new();
    for element in elements {
        ordered.push(encode_item(element)?);
    }
    ordered.sort_by(|a, b| canonical_cbor_cmp(a, b));

    e.tag(minicbor::data::Tag::new(SET_TAG))?;
    e.array(ordered.len() as u64)?;
    for element in ordered {
        e.writer_mut().extend_from_slice(&element);
    }
    Ok(())
}

/// Reads a `Set<T>`. Accepts an indefinite-length inner array and any wire order.
///
/// Tag 258 is **required**: a bare array is exactly the encoding of `Array<T>`, so accepting one
/// would erase — at the only point where it can still be checked — the type distinction the tag
/// exists to carry.
///
/// Duplicate elements are **rejected** with [`IonError::DuplicateSetElement`]; collapsing them
/// would let a three-element wire array decode as a two-element set, a size change the caller can
/// neither observe nor guard against.
pub fn read_set<T, S>(d: &mut Decoder<'_>) -> Result<S, IonError>
where
    T: IonFormat,
    S: Default + SetInsert<T>,
{
    if d.datatype()? != minicbor::data::Type::Tag {
        return Err(IonError::MalformedValue {
            ion_type: "Set",
            reason: format!(
                "expected CBOR tag {SET_TAG}, got {}; an untagged array is Array<T>, not Set<T>",
                d.datatype()?
            ),
        });
    }

    let tag = d.tag()?.as_u64();
    if tag != SET_TAG {
        return Err(IonError::UnexpectedTag { expected: SET_TAG, actual: tag, ion_type: "Set" });
    }

    if !matches!(d.datatype()?, minicbor::data::Type::Array | minicbor::data::Type::ArrayIndef) {
        return Err(IonError::MalformedValue {
            ion_type: "Set",
            reason: format!("tag {SET_TAG} must wrap an array, got {}", d.datatype()?),
        });
    }

    let mut result = S::default();
    let _depth = DepthGuard::enter()?;

    let read_element = |d: &mut Decoder<'_>, result: &mut S| -> Result<(), IonError> {
        let element = T::ion_read(d)?;
        let hex = to_hex(&encode_item(&element)?);
        if result.insert_unique(element).is_err() {
            return Err(IonError::DuplicateSetElement { element_hex: hex });
        }
        Ok(())
    };

    match d.array()? {
        Some(n) => {
            for _ in 0..n {
                read_element(d, &mut result)?;
            }
        }
        None => {
            while d.datatype()? != minicbor::data::Type::Break {
                read_element(d, &mut result)?;
            }
            d.skip()?; // the break
        }
    }

    Ok(result)
}

/// Insertion with duplicate detection, so [`read_set`] can fill a `HashSet` or a `BTreeSet`
/// without either of them silently collapsing a repeated element.
pub trait SetInsert<T> {
    /// Inserts, or returns `Err(())` if the element was already present.
    fn insert_unique(&mut self, value: T) -> Result<(), ()>;
}

impl<T: Eq + std::hash::Hash, S: std::hash::BuildHasher> SetInsert<T>
    for std::collections::HashSet<T, S>
{
    fn insert_unique(&mut self, value: T) -> Result<(), ()> {
        if self.insert(value) { Ok(()) } else { Err(()) }
    }
}

impl<T: Ord> SetInsert<T> for std::collections::BTreeSet<T> {
    fn insert_unique(&mut self, value: T) -> Result<(), ()> {
        if self.insert(value) { Ok(()) } else { Err(()) }
    }
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

// ── containers ──────────────────────────────────────────────────────────────
// So a Map/Set/T[N] can be nested inside a message, an array, a Maybe or a Partial with no
// special-casing in the generator. `[T; N]` gets the const-generic impl for ergonomics; the
// generator can equally call `read_fixed_array::<T>(d, n)` with a runtime `n`.

impl<K, V, S> IonFormat for std::collections::HashMap<K, V, S>
where
    K: IonFormat + Eq + std::hash::Hash,
    V: IonFormat,
    S: std::hash::BuildHasher + Default,
{
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        read_map::<K, V, Self>(d)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        write_map(e, self.iter())
    }
}

impl<K: IonFormat + Ord, V: IonFormat> IonFormat for std::collections::BTreeMap<K, V> {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        read_map::<K, V, Self>(d)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        write_map(e, self.iter())
    }
}

impl<T, S> IonFormat for std::collections::HashSet<T, S>
where
    T: IonFormat + Eq + std::hash::Hash,
    S: std::hash::BuildHasher + Default,
{
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        read_set::<T, Self>(d)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        write_set(e, self.iter())
    }
}

impl<T: IonFormat + Ord> IonFormat for std::collections::BTreeSet<T> {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        read_set::<T, Self>(d)
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        write_set(e, self.iter())
    }
}

impl<T: IonFormat, const N: usize> IonFormat for [T; N] {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        let values = read_fixed_array::<T>(d, N)?;
        // read_fixed_array already guarantees the length, so this cannot fail.
        <[T; N]>::try_from(values)
            .map_err(|v| IonError::FixedArrayLength { expected: N, actual: v.len() })
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        write_fixed_array(e, self.as_slice(), N)
    }
}

impl<T: IonFormat> IonFormat for crate::types::IonMaybe<T> {
    fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
        Ok(crate::types::IonMaybe::from(read_maybe::<T>(d)?))
    }

    fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
        match self.as_ref() {
            Some(v) => v.ion_write(e)?,
            None => {
                e.null()?;
            }
        }
        Ok(())
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

// ═══════════════════════════════════════════════════════════════════
// Unit tests for the reader guards
// ═══════════════════════════════════════════════════════════════════
//
// The cross-runtime vectors in `/tests/golden/compat.golden.json` cover the guards a *generated*
// reader reaches on its own. Two of the fixes below — the union envelope and the open enum —
// change the shape of generated code as well as the runtime, so until `ionc` re-emits the
// vendored schemas under `tests/compat_schemas/` those two paths have no vector that exercises
// them. These are that coverage.

#[cfg(test)]
mod tests {
    use super::*;

    fn bytes(hex: &str) -> Vec<u8> {
        (0..hex.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap())
            .collect()
    }

    fn hex_of(bytes: &[u8]) -> String {
        bytes.iter().map(|b| format!("{b:02x}")).collect()
    }

    // ── the union envelope ──────────────────────────────────────────────────

    #[test]
    fn union_envelope_reads_the_index_and_leaves_the_payload() {
        let input = bytes("82008107");
        let mut d = Decoder::new(&input);
        let (index, _depth) = read_union_envelope(&mut d, "ShapeV1").unwrap();
        assert_eq!(index, 0);
        // Positioned on the payload, not past it.
        assert_eq!(d.position(), 2);
    }

    #[test]
    fn union_envelope_of_three_items_is_a_typed_error() {
        let input = bytes("83008107f5");
        let mut d = Decoder::new(&input);
        match read_union_envelope(&mut d, "ShapeV1") {
            Err(IonError::UnionEnvelope { union_type, actual_items }) => {
                assert_eq!(union_type, "ShapeV1");
                assert_eq!(actual_items, 3);
            }
            other => panic!("expected UnionEnvelope, got {other:?}"),
        }
    }

    /// The corruption this exists to stop: with the stray item left in the stream, the field
    /// *behind* the union reads the union's leftover as its own value.
    #[test]
    fn union_envelope_fails_before_the_next_field_can_read_the_stray_item() {
        // [ [0, [1, "b"], 9], 5 ] — a union declaring three items, then `n: i4 = 5`.
        let input = bytes("828300820161620905");
        let mut d = Decoder::new(&input);
        assert_eq!(d.array().unwrap(), Some(2));
        let err = read_union_envelope(&mut d, "GrowV1").unwrap_err();
        assert!(matches!(err, IonError::UnionEnvelope { .. }), "got {err:?}");
    }

    #[test]
    fn union_envelope_rejects_an_indefinite_length() {
        let input = bytes("9f008107ff");
        let mut d = Decoder::new(&input);
        assert!(matches!(
            read_union_envelope(&mut d, "ShapeV1"),
            Err(IonError::IndefiniteArray)
        ));
    }

    #[test]
    fn union_envelope_round_trips() {
        let mut e = Encoder::new(Vec::new());
        write_union_envelope(&mut e, 1).unwrap();
        assert_eq!(hex_of(&e.into_writer()), "8201");
    }

    // ── open enums ──────────────────────────────────────────────────────────

    crate::ion_open_enum! {
        /// A `u1`-based enum at revision 1, which never heard of `Trial = 2`.
        pub enum TierV1: u8 {
            Free = 0,
            Paid = 1,
        }
    }

    crate::ion_open_enum! {
        /// A signed base type, to prove `Repr` is not hard-wired to `u8`.
        pub enum RegionV1: i32 {
            Eu = 0,
            Us = 1,
        }
    }

    #[test]
    fn a_declared_member_reads_as_itself() {
        let input = bytes("01");
        let mut d = Decoder::new(&input);
        let tier: TierV1 = read_open_enum(&mut d).unwrap();
        assert_eq!(tier, TierV1::Paid);
        assert!(tier.is_known());
        assert_eq!(tier.ion_unknown_value(), None);
        assert_eq!(TierV1::ION_ENUM_NAME, "TierV1");
    }

    #[test]
    fn an_undeclared_member_is_carried_and_re_encodes_byte_identically() {
        for (hex, raw) in [("02", 2u8), ("07", 7), ("18ff", 255)] {
            let input = bytes(hex);
            let mut d = Decoder::new(&input);
            let tier: TierV1 = read_open_enum(&mut d).unwrap();

            assert!(!tier.is_known(), "{hex} should not be a declared member");
            assert_eq!(tier, TierV1::Unknown(raw));
            assert_eq!(tier.ion_unknown_value(), Some(raw));

            let mut e = Encoder::new(Vec::new());
            write_open_enum(&mut e, &tier).unwrap();
            assert_eq!(hex_of(&e.into_writer()), hex, "{hex} must round-trip verbatim");
        }
    }

    #[test]
    fn a_value_outside_the_base_type_is_still_an_error() {
        // 256 into a u1-based enum, and -1. Neither is a newer schema revision: no revision of a
        // `u1`-based enum could have written them.
        for hex in ["190100", "20"] {
            let input = bytes(hex);
            let mut d = Decoder::new(&input);
            assert!(
                read_open_enum::<TierV1>(&mut d).is_err(),
                "{hex} must not decode into a u1-based enum"
            );
        }
    }

    #[test]
    fn a_signed_base_type_carries_an_unknown_value_too() {
        let input = bytes("2a"); // -11
        let mut d = Decoder::new(&input);
        let region: RegionV1 = read_open_enum(&mut d).unwrap();
        assert_eq!(region, RegionV1::Unknown(-11));

        let mut e = Encoder::new(Vec::new());
        write_open_enum(&mut e, &region).unwrap();
        assert_eq!(hex_of(&e.into_writer()), "2a");
    }

    #[test]
    fn an_open_enum_survives_every_container_position() {
        use std::collections::{HashMap, HashSet};

        // `Map` key and `Set` element are the two positions that need `Eq + Hash`.
        let mut m: HashMap<TierV1, i32> = HashMap::new();
        m.insert(TierV1::Unknown(9), 1);
        assert_eq!(m.get(&TierV1::Unknown(9)), Some(&1));

        let s: HashSet<TierV1> = [TierV1::Free, TierV1::Unknown(9)].into_iter().collect();
        assert_eq!(s.len(), 2);

        // `T[]` of an unknown member, read and written back unchanged.
        let input = bytes("8102");
        let mut d = Decoder::new(&input);
        let v = read_array::<TierV1>(&mut d).unwrap();
        assert_eq!(v, vec![TierV1::Unknown(2)]);
        let mut e = Encoder::new(Vec::new());
        write_array(&mut e, &v).unwrap();
        assert_eq!(hex_of(&e.into_writer()), "8102");

        // Set<T> of {Free, Paid, unknown 2}, which is the `enum-added.forward.unknown.set` vector.
        let input = bytes("d9010283000102");
        let mut d = Decoder::new(&input);
        let set: HashSet<TierV1> = read_set(&mut d).unwrap();
        assert_eq!(set.len(), 3);
        let mut e = Encoder::new(Vec::new());
        write_set(&mut e, set.iter()).unwrap();
        assert_eq!(hex_of(&e.into_writer()), "d9010283000102");
    }

    // ── the allocation guard ────────────────────────────────────────────────

    #[test]
    fn an_array_length_larger_than_the_input_is_a_typed_error_not_a_panic() {
        // An array header claiming 2^64-1 items, and nothing behind it.
        let input = bytes("9bffffffffffffffff");
        let mut d = Decoder::new(&input);
        match read_array::<i32>(&mut d) {
            Err(IonError::LengthOverclaim { declared, available, .. }) => {
                assert_eq!(declared, u64::MAX);
                assert_eq!(available, 0);
            }
            other => panic!("expected LengthOverclaim, got {other:?}"),
        }
    }

    #[test]
    fn a_declared_length_is_never_the_reservation() {
        // 2^31-1 items declared with no input behind them: provably a lie, so it never reaches an
        // allocation at all.
        let input = bytes("9a7fffffff");
        let mut d = Decoder::new(&input);
        assert!(matches!(read_array::<i32>(&mut d), Err(IonError::LengthOverclaim { .. })));

        // And even a believable count is only reserved up to the budget.
        assert!(prealloc::<i32>(usize::MAX) * std::mem::size_of::<i32>() <= PREALLOC_BUDGET_BYTES);
        assert_eq!(prealloc::<i32>(3), 3);
    }

    #[test]
    fn an_honest_array_still_decodes() {
        let input = bytes("83010203");
        let mut d = Decoder::new(&input);
        assert_eq!(read_array::<i32>(&mut d).unwrap(), vec![1, 2, 3]);
    }

    // ── the depth limit ─────────────────────────────────────────────────────

    /// `levels` open arrays, the innermost holding a single integer.
    fn nested_arrays(levels: usize) -> Vec<u8> {
        let mut v = vec![0x81u8; levels];
        v.push(0x00);
        v
    }

    /// `levels` open arrays, the innermost empty — so every level is a `Vec` the reader builds.
    fn nested_empty(levels: usize) -> Vec<u8> {
        let mut v = vec![0x81u8; levels - 1];
        v.push(0x80);
        v
    }

    #[test]
    fn the_skip_path_accepts_exactly_max_depth() {
        let input = nested_arrays(MAX_DEPTH);
        let mut d = Decoder::new(&input);
        skip_value(&mut d).unwrap();
        assert_eq!(d.position(), input.len());
    }

    #[test]
    fn the_skip_path_refuses_one_level_past_max_depth() {
        let input = nested_arrays(MAX_DEPTH + 1);
        let mut d = Decoder::new(&input);
        match skip_value(&mut d) {
            Err(IonError::DepthLimit { limit, depth }) => {
                assert_eq!(limit, MAX_DEPTH);
                assert_eq!(depth, MAX_DEPTH + 1);
            }
            other => panic!("expected DepthLimit, got {other:?}"),
        }
    }

    /// The vector this comes from is 100 000 levels of `0x81` in a field the reader only skips.
    /// It has to be a `Result`, and it has to stop at the limit rather than walk the payload.
    #[test]
    fn a_hundred_thousand_skipped_levels_are_a_typed_error() {
        let input = nested_arrays(100_000);
        let mut d = Decoder::new(&input);
        assert!(matches!(skip_value(&mut d), Err(IonError::DepthLimit { .. })));
        assert!(d.position() <= MAX_DEPTH + 1, "stopped after {} bytes", d.position());
    }

    #[test]
    fn skip_remaining_is_bounded_the_same_way() {
        // A message array of two items whose second is 100 000 arrays deep and only skipped.
        let mut input = bytes("8200");
        input.extend(nested_arrays(100_000));
        let mut d = Decoder::new(&input);
        let len = d.array().unwrap().unwrap();
        assert_eq!(i32::ion_read(&mut d).unwrap(), 0);
        assert!(matches!(skip_remaining(&mut d, len, 1), Err(IonError::DepthLimit { .. })));
    }

    /// A recursive message decodes through a collection helper at every level, which is what
    /// carries the ambient depth on the *read* path.
    #[derive(Debug, PartialEq)]
    struct Deep(Vec<Deep>);

    impl IonFormat for Deep {
        fn ion_read(d: &mut Decoder<'_>) -> Result<Self, IonError> {
            Ok(Deep(read_array::<Deep>(d)?))
        }
        fn ion_write(&self, e: &mut Encoder<Vec<u8>>) -> Result<(), IonError> {
            write_array(e, &self.0)
        }
    }

    #[test]
    fn the_decode_path_accepts_exactly_max_depth() {
        let input = nested_empty(MAX_DEPTH);
        let mut d = Decoder::new(&input);
        Deep::ion_read(&mut d).unwrap();
        assert_eq!(d.position(), input.len());
        assert_eq!(DepthGuard::open_containers(), 0);
    }

    #[test]
    fn the_decode_path_refuses_one_level_past_max_depth() {
        let input = nested_empty(MAX_DEPTH + 1);
        let mut d = Decoder::new(&input);
        match Deep::ion_read(&mut d) {
            Err(IonError::DepthLimit { limit, depth }) => {
                assert_eq!(limit, MAX_DEPTH);
                assert_eq!(depth, MAX_DEPTH + 1);
            }
            other => panic!("expected DepthLimit, got {other:?}"),
        }
        assert_eq!(DepthGuard::open_containers(), 0, "the depth count must not leak");
    }

    #[test]
    fn the_depth_count_does_not_leak_on_a_failed_read() {
        assert_eq!(DepthGuard::open_containers(), 0);
        let input = bytes("83010203"); // three integers, read as strings
        let mut d = Decoder::new(&input);
        assert!(read_array::<String>(&mut d).is_err());
        assert_eq!(DepthGuard::open_containers(), 0);
    }

    #[test]
    fn a_skip_inherits_the_ambient_depth() {
        // Holding MAX_DEPTH containers open by hand leaves the skip path no room at all.
        let guards: Vec<DepthGuard> =
            (0..MAX_DEPTH).map(|_| DepthGuard::enter().unwrap()).collect();
        let input = nested_arrays(1);
        let mut d = Decoder::new(&input);
        assert!(matches!(skip_value(&mut d), Err(IonError::DepthLimit { .. })));
        drop(guards);
        assert_eq!(DepthGuard::open_containers(), 0);
    }

    // ── the skip walk itself ────────────────────────────────────────────────

    #[test]
    fn skip_value_consumes_exactly_one_item_of_every_shape() {
        for hex in [
            "01",                   // integer
            "20",                   // negative integer
            "6162",                 // text
            "7f6162ff",             // chunked text
            "4101",                 // bytes
            "5f4101ff",             // chunked bytes
            "80",                   // empty array
            "83010203",             // array
            "9f010203ff",           // indefinite array
            "a0",                   // empty map
            "a1616b01",             // map
            "bf616b01ff",           // indefinite map
            "d9010283000102",       // tag 258 over an array
            "c074323032342d30312d30315430303a30303a30305a", // tag 0 over text
            "826161a2616101616202", // array holding a map
            "9f9f9fffffff",         // indefinite arrays nested in each other
            "a1616b9f0102ff",       // an indefinite array as a map value
            "f6",                   // null
            "f7",                   // undefined
            "f5",                   // bool
            "fb3ff0000000000000",   // double
        ] {
            // 0xf5 is a sentinel behind the item, and must survive the skip untouched.
            let input = bytes(&format!("{hex}f5"));
            let mut d = Decoder::new(&input);
            skip_value(&mut d).unwrap_or_else(|e| panic!("skipping {hex}: {e}"));
            assert_eq!(d.position(), input.len() - 1, "skipping {hex} consumed the wrong length");
            assert!(d.bool().unwrap(), "the sentinel behind {hex} was consumed");
        }
    }

    #[test]
    fn skip_value_rejects_a_break_inside_a_definite_container() {
        // The array declares three items and delivers one and a break.
        let input = bytes("8301ff");
        let mut d = Decoder::new(&input);
        assert!(matches!(skip_value(&mut d), Err(IonError::MalformedValue { .. })));
    }

    #[test]
    fn skip_value_reports_a_truncated_item_rather_than_looping() {
        let input = bytes("830102");
        let mut d = Decoder::new(&input);
        assert!(skip_value(&mut d).is_err());
    }

    // ── chunked strings ─────────────────────────────────────────────────────

    #[test]
    fn a_chunked_text_string_is_accepted_and_re_encodes_definite() {
        let input = bytes("7f6162ff");
        let mut d = Decoder::new(&input);
        let s = String::ion_read(&mut d).unwrap();
        assert_eq!(s, "b");
        assert_eq!(d.position(), input.len());

        let mut e = Encoder::new(Vec::new());
        s.ion_write(&mut e).unwrap();
        assert_eq!(hex_of(&e.into_writer()), "6162", "writers stay definite-length");
    }

    #[test]
    fn a_chunked_byte_string_is_accepted() {
        let input = bytes("5f41014102ff");
        let mut d = Decoder::new(&input);
        let b = crate::types::IonBytes::ion_read(&mut d).unwrap();
        assert_eq!(b.as_slice(), &[1, 2]);
        assert_eq!(d.position(), input.len());
    }

    // ── the message header ──────────────────────────────────────────────────

    #[test]
    fn read_message_header_returns_the_declared_length_and_counts_the_array() {
        let input = bytes("83016162f5");
        let mut d = Decoder::new(&input);
        let (len, depth) = read_message_header(&mut d, "AppendedV1").unwrap();
        assert_eq!(len, 3);
        assert_eq!(DepthGuard::open_containers(), 1);
        assert_eq!(i32::ion_read(&mut d).unwrap(), 1);
        assert_eq!(String::ion_read(&mut d).unwrap(), "b");
        skip_remaining(&mut d, len, 2).unwrap();
        assert_eq!(d.position(), input.len());
        drop(depth);
        assert_eq!(DepthGuard::open_containers(), 0);
    }

    #[test]
    fn read_message_header_rejects_an_indefinite_array() {
        let input = bytes("9f016162ff");
        let mut d = Decoder::new(&input);
        assert!(matches!(
            read_message_header(&mut d, "AppendedV1"),
            Err(IonError::IndefiniteArray)
        ));
        assert_eq!(DepthGuard::open_containers(), 0);
    }

    /// With the message array counted, a recursive message costs two levels per recursion — the
    /// same arithmetic `CborReader.CurrentDepth` does in C#.
    #[test]
    fn a_message_array_and_its_collection_both_count() {
        let guards: Vec<DepthGuard> =
            (0..MAX_DEPTH - 1).map(|_| DepthGuard::enter().unwrap()).collect();
        // A one-field message whose field is a `T[]`: two containers, not one.
        let input = bytes("818100");
        let mut d = Decoder::new(&input);
        let (len, depth) = read_message_header(&mut d, "Msg").unwrap();
        assert_eq!(len, 1);
        assert_eq!(DepthGuard::open_containers(), MAX_DEPTH);
        assert!(matches!(read_array::<i32>(&mut d), Err(IonError::DepthLimit { .. })));
        drop((depth, guards));
        assert_eq!(DepthGuard::open_containers(), 0);
    }

    #[test]
    fn a_definite_text_string_is_unchanged() {
        let input = bytes("6162");
        let mut d = Decoder::new(&input);
        assert_eq!(String::ion_read(&mut d).unwrap(), "b");
        assert_eq!(d.position(), input.len());
    }
}
