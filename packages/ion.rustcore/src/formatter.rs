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
            let mut result = Vec::with_capacity(n);
            for _ in 0..n {
                result.push(T::ion_read(d)?);
            }
            Ok(result)
        }
        None => {
            let mut result = Vec::with_capacity(n);
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
