import type { IonArray, IonPartial } from "../baseTypes";
import { CborReader, CborWriter } from "../cbor";
import { CborReaderState } from "../cbor/CborReader";
import {
  IonDuplicateMapKeyError,
  IonDuplicateSetElementError,
  IonFixedArrayLengthError,
  IonFieldCountError,
  IonIndefiniteLengthError,
  IonInvalidUnionIndexError,
  IonMalformedValueError,
  IonTruncatedPayloadError,
  IonUnexpectedCborTypeError,
  IonUnexpectedTagError,
  IonUnionEnvelopeError,
} from "../errors";
import type { IonClientContext } from "../unary/IonUnaryRequest";
import type { IIonService } from "./IIonService";
import { ServiceExecutor } from "./ServiceExecutor";

/** CBOR tag 258 — the IANA-registered "set" tag. */
export const ION_SET_TAG = 258;

/**
 * Canonical CBOR ordering, RFC 8949 §4.2.1: compare two encoded data items by their **byte length
 * first**, and only then lexicographically by their bytes.
 *
 * This is the total order that makes `Map<K,V>` and `Set<T>` byte-identical across runtimes. A
 * JavaScript `Map`, a C# `Dictionary` and a Rust `HashMap` have three different iteration orders;
 * without a total order on the wire the same logical map produces three different byte strings.
 *
 * **Length-first is not plain bytewise comparison.** Integer keys make the difference visible:
 * `-1` encodes as `20` (1 byte) and `1000` as `1903e8` (3 bytes), so length-first puts `-1` first
 * while a bytewise-only sort puts `1000` first, because `0x19 < 0x20`.
 */
export function ionCanonicalCborCompare(a: Uint8Array, b: Uint8Array): number {
  if (a.length !== b.length) return a.length - b.length;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return a[i] - b[i];
  return 0;
}

const toIdentity = (bytes: Uint8Array) =>
  Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");

export interface IonFormatter<T> {
  read(reader: CborReader): T;
  write(writer: CborWriter, value: T): void;
}

/**
 * How a `Partial<T>` field is encoded.
 *  - `value`    the formatter registered under {@link IonPartialField.type}
 *  - `array`    `T[]`, via readArray/writeArray
 *  - `maybe`    `T?` represented as {@link IonMaybe}
 *  - `nullable` `T?` represented as `T | null`
 */
export type IonPartialFieldKind = "value" | "array" | "maybe" | "nullable";

export interface IonPartialField {
  /** The Ion field name; used verbatim as the CBOR map key. */
  readonly name: string;
  /** Registered formatter name of the field (or of the element type for `array`). */
  readonly type: string;
  /** Defaults to `"value"`. */
  readonly kind?: IonPartialFieldKind;
}

/** Ordered field schema of a `Partial<T>` — Ion declaration order. */
export type IonPartialSchema = ReadonlyArray<IonPartialField>;

/** @deprecated legacy unordered form: `{ fieldName: formatterName }`. Prefer {@link IonPartialSchema}. */
export type FieldSchema = Record<string, string>;

function normalizePartialSchema(
  schema: IonPartialSchema | FieldSchema
): IonPartialField[] {
  const fields = Array.isArray(schema)
    ? (schema as IonPartialField[])
    : Object.entries(schema as FieldSchema).map(([name, type]) => ({
        name,
        type,
        kind: "value" as const,
      }));

  const seen = new Set<string>();
  for (const f of fields) {
    if (!f || typeof f.name !== "string" || typeof f.type !== "string")
      throw new Error(`Invalid Partial field descriptor: ${JSON.stringify(f)}`);
    if (seen.has(f.name))
      throw new Error(`Duplicate field '${f.name}' in Partial schema`);
    seen.add(f.name);
  }

  return fields;
}

export type ExecutorConstructor<T extends IIonService> = new (
  ctx: IonClientContext,
  signal: AbortSignal
) => ServiceExecutor<T> & T;

export class IonFormatterStorage {
  private static map = new Map<string, IonFormatter<any>>();
  private static mapExecutors = new Map<string, ExecutorConstructor<any>>();

  static register<T>(name: string, formatter: IonFormatter<T>) {
    this.map.set(name, formatter);
  }

  static registerClientExecutor<T extends IIonService>(
    name: string,
    executorCtor: ExecutorConstructor<T>
  ) {
    this.mapExecutors.set(name, executorCtor);
  }

  static createExecutor<T extends IIonService>(
    name: string,
    ctx: IonClientContext,
    signal: AbortSignal
  ): ServiceExecutor<T> & T {
    const ctor = this.mapExecutors.get(name);
    if (!ctor) throw new Error(`Executor not registered: ${name}`);
    return new ctor(ctx, signal);
  }

  static get<T>(name: string): IonFormatter<T> {
    const f = this.map.get(name);
    if (!f) throw new Error(`Formatter not found: ${name}`);
    return f;
  }

  static readMaybe<T>(reader: CborReader, typeName: string): IonMaybe<T> {
    const state = reader.peekState();
    if (state !== CborReaderState.Null) {
      const value = IonFormatterStorage.get<T>(typeName).read(reader);
      return IonMaybe.Some(value);
    }
    reader.readNull();
    return IonMaybe.None<T>();
  }

  static readNullable<T>(reader: CborReader, typeName: string): T | null {
    const state = reader.peekState();
    if (state !== CborReaderState.Null) {
      const value = IonFormatterStorage.get<T>(typeName).read(reader);
      return value;
    }
    reader.readNull();
    return null;
  }

  static writeMaybe<T>(
    writer: CborWriter,
    ionMaybe: IonMaybe<T>,
    typeName: string
  ): void {
    if (!ionMaybe.hasValue) {
      writer.writeNull();
      return;
    }
    IonFormatterStorage.get<T>(typeName).write(writer, ionMaybe.value as T);
  }

  static writeNullable<T>(
    writer: CborWriter,
    ionMaybe: T | null,
    typeName: string = ""
  ): void {
    if (ionMaybe === undefined || ionMaybe === null) {
      writer.writeNull();
      return;
    }
    IonFormatterStorage.get<T>(typeName).write(writer, ionMaybe as T);
  }

  static readNullableArray<T>(reader: CborReader, typeName: string): IonArray<T> | null {
    const state = reader.peekState();
    if (state !== CborReaderState.Null) {
      return IonFormatterStorage.readArray<T>(reader, typeName);
    }
    reader.readNull();
    return null;
  }

  static writeNullableArray<T>(
    writer: CborWriter,
    array: IonArray<T> | null,
    typeName: string
  ): void {
    if (array === undefined || array === null) {
      writer.writeNull();
      return;
    }
    IonFormatterStorage.writeArray<T>(writer, array, typeName);
  }

  static readArray<T>(reader: CborReader, typeName: string): IonArray<T> {
    const size = reader.readStartArray();
    if (size === null) throw new IonIndefiniteLengthError(`${typeName}[]`);

    const formatter = IonFormatterStorage.get<T>(typeName);
    const values: T[] = [];
    for (let i = 0; i < size; i++) {
      values.push(formatter.read(reader));
    }

    reader.readEndArray();
    return values;
  }

  static writeArray<T>(
    writer: CborWriter,
    array: IonArray<T>,
    typeName: string
  ): void {
    writer.writeStartArray(array.length);
    if (array.length === 0) {
      writer.writeEndArray();
      return;
    }

    const formatter = IonFormatterStorage.get<T>(typeName);
    for (let i = 0; i < array.length; i++) {
      formatter.write(writer, array[i]);
    }

    writer.writeEndArray();
  }

  // ═══════════════════════════════════════════════════════════════════════════
  //  Map<K,V> — definite-length CBOR map, keys in canonical order
  // ═══════════════════════════════════════════════════════════════════════════

  /** Encodes one value with its registered formatter into a standalone buffer, for sorting. */
  private static encodeItem<T>(value: T, typeName: string): Uint8Array {
    const scratch = new CborWriter();
    IonFormatterStorage.get<T>(typeName).write(scratch, value);
    // `data` is a subarray view of the scratch writer's buffer; copy so a later growth of that
    // buffer cannot alias it.
    return new Uint8Array(scratch.data);
  }

  /**
   * Writes a `Map<K,V>` as a definite-length CBOR map whose keys are sorted in canonical CBOR
   * order ({@link ionCanonicalCborCompare}).
   *
   * Sorting is what makes a JS `Map`, a C# `Dictionary` and a Rust `HashMap` — three different
   * iteration orders — produce the same bytes for the same logical map. Values are written by
   * their own formatter and take no part in the ordering.
   *
   * Golden vectors: `/tests/golden/collections.golden.json`, section `map`.
   */
  static writeMap<K, V>(
    writer: CborWriter,
    map: ReadonlyMap<K, V>,
    keyType: string,
    valueType: string
  ): void {
    // Only the KEYS need pre-encoding: once the order is decided the values can be written live.
    const ordered: Array<{ key: Uint8Array; value: V }> = [];
    for (const [key, value] of map)
      ordered.push({ key: IonFormatterStorage.encodeItem(key, keyType), value });

    ordered.sort((a, b) => ionCanonicalCborCompare(a.key, b.key));

    const valueFormatter = IonFormatterStorage.get<V>(valueType);
    writer.writeStartMap(ordered.length);
    for (const entry of ordered) {
      writer.writeEncodedValue(entry.key);
      valueFormatter.write(writer, entry.value);
    }
    writer.writeEndMap();
  }

  /**
   * Reads a `Map<K,V>`. Accepts a definite- or indefinite-length CBOR map and any wire order;
   * re-encoding canonicalises both.
   *
   * Duplicate keys are **rejected** with {@link IonDuplicateMapKeyError}. Identity is the
   * canonical encoded key bytes, which for every permitted key type (scalar / string / guid /
   * enum) coincides with value equality — and unlike a JS `Map`'s own `has()`, it does not fall
   * back to reference equality if a non-scalar key ever reaches it.
   */
  static readMap<K, V>(
    reader: CborReader,
    keyType: string,
    valueType: string
  ): Map<K, V> {
    if (reader.peekState() !== CborReaderState.StartMap)
      throw new IonMalformedValueError("Map", "expected a CBOR map");

    const keyFormatter = IonFormatterStorage.get<K>(keyType);
    const valueFormatter = IonFormatterStorage.get<V>(valueType);

    const result = new Map<K, V>();
    const seen = new Set<string>();

    const readEntry = () => {
      const key = keyFormatter.read(reader);
      const value = valueFormatter.read(reader);

      const identity = toIdentity(IonFormatterStorage.encodeItem(key, keyType));
      if (seen.has(identity)) throw new IonDuplicateMapKeyError(key);
      seen.add(identity);

      result.set(key, value);
    };

    // `readStartMap()` returns null for an indefinite-length map. Treating that as zero is the
    // exact bug the Partial<T> formatter had: it read no entries and then desynchronised the
    // reader on the closing break.
    const length = reader.readStartMap();
    if (length === null) {
      while (reader.peekState() !== CborReaderState.EndMap) {
        if (reader.peekState() === CborReaderState.Finished)
          throw new IonMalformedValueError("Map", "unexpected end of CBOR data inside a map");
        readEntry();
      }
    } else {
      for (let i = 0; i < length; i++) readEntry();
    }

    reader.readEndMap();
    return result;
  }

  /** Reads a nullable map: CBOR null becomes `null`. */
  static readNullableMap<K, V>(
    reader: CborReader,
    keyType: string,
    valueType: string
  ): Map<K, V> | null {
    if (reader.peekState() === CborReaderState.Null) {
      reader.readNull();
      return null;
    }
    return IonFormatterStorage.readMap<K, V>(reader, keyType, valueType);
  }

  /** Writes a nullable map: `null`/`undefined` becomes CBOR null. */
  static writeNullableMap<K, V>(
    writer: CborWriter,
    map: ReadonlyMap<K, V> | null | undefined,
    keyType: string,
    valueType: string
  ): void {
    if (map === null || map === undefined) {
      writer.writeNull();
      return;
    }
    IonFormatterStorage.writeMap(writer, map, keyType, valueType);
  }

  // ═══════════════════════════════════════════════════════════════════════════
  //  Set<T> — CBOR tag 258 + definite-length array, elements in canonical order
  // ═══════════════════════════════════════════════════════════════════════════

  /**
   * Writes a `Set<T>` as CBOR tag 258 — the IANA-registered set tag — wrapping a definite-length
   * array whose elements are sorted in canonical CBOR order.
   *
   * Sorting, not insertion order, is what makes two sets built by inserting the same elements in
   * different orders produce identical bytes. The tag is what distinguishes `Set<T>` from
   * `Array<T>` on the wire; they are distinct Ion types with distinct schema-lock entries.
   *
   * Golden vectors: `/tests/golden/collections.golden.json`, section `set`.
   */
  static writeSet<T>(
    writer: CborWriter,
    set: ReadonlySet<T>,
    elementType: string
  ): void {
    const ordered: Uint8Array[] = [];
    for (const element of set)
      ordered.push(IonFormatterStorage.encodeItem(element, elementType));

    ordered.sort(ionCanonicalCborCompare);

    writer.writeTag(ION_SET_TAG);
    writer.writeStartArray(ordered.length);
    for (const element of ordered) writer.writeEncodedValue(element);
    writer.writeEndArray();
  }

  /**
   * Reads a `Set<T>`. Accepts an indefinite-length inner array and any wire order.
   *
   * Tag 258 is **required**: a bare array is exactly the encoding of `Array<T>`, so accepting one
   * would erase — at the only point where it can still be checked — the type distinction the tag
   * exists to carry.
   *
   * Duplicate elements are **rejected** with {@link IonDuplicateSetElementError}; collapsing them
   * would let a three-element wire array decode as a two-element set.
   */
  static readSet<T>(reader: CborReader, elementType: string): Set<T> {
    if (reader.peekState() !== CborReaderState.Tag)
      throw new IonMalformedValueError(
        "Set",
        `expected CBOR tag ${ION_SET_TAG}; an untagged array is Array<T>, not Set<T>`
      );

    const tag = reader.readTag();
    if (Number(tag) !== ION_SET_TAG)
      throw new IonUnexpectedTagError(ION_SET_TAG, tag, "Set");

    if (reader.peekState() !== CborReaderState.StartArray)
      throw new IonMalformedValueError("Set", `tag ${ION_SET_TAG} must wrap an array`);

    const formatter = IonFormatterStorage.get<T>(elementType);
    const result = new Set<T>();
    const seen = new Set<string>();

    const readElement = () => {
      const element = formatter.read(reader);
      const identity = toIdentity(IonFormatterStorage.encodeItem(element, elementType));
      if (seen.has(identity)) throw new IonDuplicateSetElementError(element);
      seen.add(identity);
      result.add(element);
    };

    const length = reader.readStartArray();
    if (length === null) {
      while (reader.peekState() !== CborReaderState.EndArray) {
        if (reader.peekState() === CborReaderState.Finished)
          throw new IonMalformedValueError("Set", "unexpected end of CBOR data inside a set");
        readElement();
      }
    } else {
      for (let i = 0; i < length; i++) readElement();
    }

    reader.readEndArray();
    return result;
  }

  /** Reads a nullable set: CBOR null becomes `null`. */
  static readNullableSet<T>(reader: CborReader, elementType: string): Set<T> | null {
    if (reader.peekState() === CborReaderState.Null) {
      reader.readNull();
      return null;
    }
    return IonFormatterStorage.readSet<T>(reader, elementType);
  }

  /** Writes a nullable set: `null`/`undefined` becomes CBOR null. */
  static writeNullableSet<T>(
    writer: CborWriter,
    set: ReadonlySet<T> | null | undefined,
    elementType: string
  ): void {
    if (set === null || set === undefined) {
      writer.writeNull();
      return;
    }
    IonFormatterStorage.writeSet(writer, set, elementType);
  }

  // ═══════════════════════════════════════════════════════════════════════════
  //  T[N] — fixed-size arrays
  // ═══════════════════════════════════════════════════════════════════════════

  /**
   * Writes exactly `length` items as a definite-length CBOR array.
   *
   * `length` is a **parameter**, never baked into a per-length formatter, so one call site serves
   * every declared N. A mismatched array is rejected with {@link IonFixedArrayLengthError},
   * because writers are exact.
   *
   * **No `u1[N]` special case:** a fixed array of `u1` is an array of N CBOR integers, not a byte
   * string. Collapsing it would make the wire type of a fixed array depend on its element type,
   * which no reader could predict from the array shape alone.
   *
   * Golden vectors: `/tests/golden/collections.golden.json`, section `fixedArray`.
   */
  static writeFixedArray<T>(
    writer: CborWriter,
    array: ArrayLike<T>,
    elementType: string,
    length: number
  ): void {
    if (array.length !== length)
      throw new IonFixedArrayLengthError(length, array.length);

    const formatter = IonFormatterStorage.get<T>(elementType);
    writer.writeStartArray(length);
    for (let i = 0; i < length; i++) formatter.write(writer, array[i]);
    writer.writeEndArray();
  }

  /**
   * Reads a CBOR array that must hold exactly `length` items, failing with
   * {@link IonFixedArrayLengthError} — which names **both** lengths — otherwise. That check is
   * the entire point of the feature.
   *
   * Extra items are not skipped for forward compatibility, unlike a message's trailing fields:
   * the declared length is the contract. An indefinite-length array is accepted as long as it
   * turns out to hold exactly `length` items.
   */
  static readFixedArray<T>(
    reader: CborReader,
    elementType: string,
    length: number
  ): IonArray<T> {
    if (reader.peekState() !== CborReaderState.StartArray)
      throw new IonMalformedValueError(`${elementType}[${length}]`, "expected a CBOR array");

    const declared = reader.readStartArray();
    if (declared !== null && declared !== length)
      throw new IonFixedArrayLengthError(length, declared);

    const formatter = IonFormatterStorage.get<T>(elementType);
    const values: T[] = [];

    if (declared === null) {
      while (reader.peekState() !== CborReaderState.EndArray) {
        if (reader.peekState() === CborReaderState.Finished)
          throw new IonMalformedValueError(
            `${elementType}[${length}]`,
            "unexpected end of CBOR data inside a fixed-size array"
          );
        // Stop before running past N so a hostile payload cannot make the reader allocate without
        // bound; the count is reported as at-least-N+1.
        if (values.length === length)
          throw new IonFixedArrayLengthError(length, length + 1);
        values.push(formatter.read(reader));
      }
    } else {
      for (let i = 0; i < declared; i++) values.push(formatter.read(reader));
    }

    reader.readEndArray();

    if (values.length !== length)
      throw new IonFixedArrayLengthError(length, values.length);

    return values;
  }

  /** Reads a nullable fixed-size array: CBOR null becomes `null`. */
  static readNullableFixedArray<T>(
    reader: CborReader,
    elementType: string,
    length: number
  ): IonArray<T> | null {
    if (reader.peekState() === CborReaderState.Null) {
      reader.readNull();
      return null;
    }
    return IonFormatterStorage.readFixedArray<T>(reader, elementType, length);
  }

  /** Writes a nullable fixed-size array: `null`/`undefined` becomes CBOR null. */
  static writeNullableFixedArray<T>(
    writer: CborWriter,
    array: ArrayLike<T> | null | undefined,
    elementType: string,
    length: number
  ): void {
    if (array === null || array === undefined) {
      writer.writeNull();
      return;
    }
    IonFormatterStorage.writeFixedArray(writer, array, elementType, length);
  }

  // ── named container formatters ──────────────────────────────────────────────
  // Formatter lookup in this runtime is by string name, and an `IonPartialField.type` is a name.
  // These factories mint a name for a parameterised container so it can be nested anywhere a
  // scalar formatter can — inside a Partial schema, an array, a Maybe.

  /** Registers `Map<K,V>` under `name`, e.g. `registerMap("Map<string,i4>", "string", "i4")`. */
  static registerMap<K, V>(
    name: string,
    keyType: string,
    valueType: string
  ): IonFormatter<Map<K, V>> {
    const formatter: IonFormatter<Map<K, V>> = {
      read: (reader) => IonFormatterStorage.readMap<K, V>(reader, keyType, valueType),
      write: (writer, value) =>
        IonFormatterStorage.writeMap<K, V>(writer, value, keyType, valueType),
    };
    IonFormatterStorage.register(name, formatter);
    return formatter;
  }

  /** Registers `Set<T>` under `name`, e.g. `registerSet("Set<i4>", "i4")`. */
  static registerSet<T>(name: string, elementType: string): IonFormatter<Set<T>> {
    const formatter: IonFormatter<Set<T>> = {
      read: (reader) => IonFormatterStorage.readSet<T>(reader, elementType),
      write: (writer, value) => IonFormatterStorage.writeSet<T>(writer, value, elementType),
    };
    IonFormatterStorage.register(name, formatter);
    return formatter;
  }

  /** Registers `T[N]` under `name`, e.g. `registerFixedArray("i4[3]", "i4", 3)`. */
  static registerFixedArray<T>(
    name: string,
    elementType: string,
    length: number
  ): IonFormatter<IonArray<T>> {
    const formatter: IonFormatter<IonArray<T>> = {
      read: (reader) => IonFormatterStorage.readFixedArray<T>(reader, elementType, length),
      write: (writer, value) =>
        IonFormatterStorage.writeFixedArray<T>(writer, value, elementType, length),
    };
    IonFormatterStorage.register(name, formatter);
    return formatter;
  }

  /**
   * Opens a message's positional array and checks it against the declared field count.
   *
   * The front half of the trailing-skip mechanism, and the only point at which a payload that is
   * too short can still be reported honestly: a positional array with fewer items than the schema
   * declares is an {@link IonFieldCountError} naming both counts, never a read that walks past the
   * array into whatever follows it.
   *
   * Emitted by `ionc` in place of the older
   * `reader.readStartArray() ?? (() => { throw new Error("undefined len array not allowed") })()`.
   */
  static readStartMessage(
    reader: CborReader,
    expectedFields: number,
    context: string
  ): number {
    const state = reader.peekState();

    // Checked before the major type: an empty or exhausted buffer is a truncation, not "an array
    // was expected and something else arrived". C# reaches the same answer because `PeekState()`
    // throws there and `IonDecodeGuard` maps that to a truncation.
    if (state === CborReaderState.Finished) throw new IonTruncatedPayloadError(context);

    if (state !== CborReaderState.StartArray)
      throw new IonUnexpectedCborTypeError(context, "an array", CborReaderState[state]);

    const declared = reader.readStartArray();
    if (declared === null) throw new IonIndefiniteLengthError(context);
    if (declared < expectedFields)
      throw new IonFieldCountError(context, expectedFields, declared);

    return declared;
  }

  /**
   * Opens a `union` envelope and reads its case index.
   *
   * **A union envelope is exactly `[index, payload]` — two items, in every revision of every
   * union.** Growth happens inside the case payload, which is a message and skips its own tail;
   * the envelope itself never grows. A third item is therefore a malformed frame, not a newer
   * peer, and the generated reader used to walk straight past it: it read the index and the
   * payload and stopped, leaving the stray item for the *next field of the enclosing message* to
   * read as its own value. In the compat suite that turned `n: 5` into `n: 9` with no error
   * anywhere.
   *
   * An unknown index is {@link IonInvalidUnionIndexError} — the generated `else` arm used to be
   * `throw new Error()`, with no message at all.
   */
  static readStartUnion(
    reader: CborReader,
    unionType: string,
    declaredCases: number
  ): number {
    const state = reader.peekState();

    if (state === CborReaderState.Finished) throw new IonTruncatedPayloadError(unionType);

    if (state !== CborReaderState.StartArray)
      throw new IonUnexpectedCborTypeError(
        unionType,
        "a [index, payload] array",
        CborReaderState[state]
      );

    const declared = reader.readStartArray();
    if (declared === null) throw new IonIndefiniteLengthError(unionType);
    if (declared !== 2) throw new IonUnionEnvelopeError(unionType, declared);

    const index = reader.readUInt32();
    if (index >= declaredCases)
      throw new IonInvalidUnionIndexError(unionType, index, declaredCases);

    return index;
  }

  /** Closes a `union` envelope opened by {@link readStartUnion}. */
  static readEndUnion(reader: CborReader): void {
    reader.readEndArray();
  }

  /**
   * Raises {@link IonInvalidUnionIndexError} — the generated union reader's final `else`.
   *
   * `readStartUnion` has already rejected an index this revision does not declare, so the arm is
   * unreachable in practice; it stays as the thing that fires if the emitted case list and the
   * emitted case *count* ever disagree, and it must not be the `throw new Error()` with an empty
   * message that used to stand there.
   *
   * **Why it is a method here rather than a `new` in the generated file.** The import preamble
   * `ionc` writes at the top of a generated module is a fixed list that does not include the error
   * classes, so generated code reaches the typed hierarchy through this class or not at all.
   */
  static invalidUnionIndex(unionType: string, index: number, declaredCases: number): never {
    throw new IonInvalidUnionIndexError(unionType, index, declaredCases);
  }

  /**
   * Reads an Ion `enum` as an **open** enum.
   *
   * Two different failures used to be conflated here. A value that does not fit the declared base
   * type — 256 or -1 into a `u1` — is a decode error, and the base-type formatter raises it. A
   * value that *fits* but names no declared member is not an error at all: it is a peer on a newer
   * revision of the schema, and the whole point of a positional wire format with a trailing skip
   * is that such a peer stays readable. The generated reader used to
   * `throw new Error('invalid enum type')` for the second case, so adding an enum member was a
   * breaking change in TypeScript while C# carried it through happily.
   *
   * The unknown value is returned as-is, so it re-encodes byte-identically. A `switch` over the
   * enum must therefore have a default arm — which it needed anyway, for the same reason a
   * protobuf `enum` does.
   *
   * A TypeScript numeric `enum` is a `number` at run time and its members are not a closed set at
   * run time either, so the undeclared value needs no new representation: it *is* a value of the
   * enum type. `Ion_TierV1_OpenEnum.isKnown(v)` — emitted beside every generated enum — is how a
   * caller asks whether the peer sent a member this build declares.
   *
   * `T` is the generated enum type, so the caller needs no cast; it also lets an `i8`/`u8`-based
   * enum, whose values are `bigint` in this runtime, come back without one.
   */
  static readOpenEnum<T = number>(reader: CborReader, baseType: string): T {
    return IonFormatterStorage.get<T>(baseType).read(reader);
  }

  private static readPartialFieldValue(
    reader: CborReader,
    field: IonPartialField
  ): unknown {
    switch (field.kind ?? "value") {
      case "array":
        return IonFormatterStorage.readArray(reader, field.type);
      case "maybe":
        return IonFormatterStorage.readMaybe(reader, field.type);
      case "nullable":
        return IonFormatterStorage.readNullable(reader, field.type);
      default:
        return IonFormatterStorage.get(field.type).read(reader);
    }
  }

  private static writePartialFieldValue(
    writer: CborWriter,
    field: IonPartialField,
    value: unknown
  ): void {
    switch (field.kind ?? "value") {
      case "array":
        IonFormatterStorage.writeArray(writer, value as IonArray<any>, field.type);
        return;
      case "maybe":
        IonFormatterStorage.writeMaybe(writer, value as IonMaybe<any>, field.type);
        return;
      case "nullable":
        IonFormatterStorage.writeNullable(writer, value, field.type);
        return;
      default:
        IonFormatterStorage.get(field.type).write(writer, value);
    }
  }

  /**
   * Builds the codec for `Partial<T>` ("T~") from a code-generated field schema.
   *
   * Wire format — must stay byte-identical with `src/ion.runtime` and
   * `packages/ion.rustcore`; golden vectors live in
   * `/tests/golden/partial.golden.json`:
   *
   *   map(N)                    definite length on write; an indefinite-length
   *                             map (0xBF … 0xFF) is accepted on read
   *     key   := text string    the Ion field name
   *     value := null (0xF6)    the field is CLEARED
   *            | <encoding>     the field is MODIFIED to that value
   *
   * A field absent from the map is UNTOUCHED; unknown keys are skipped on read.
   * Entries are written in schema (Ion declaration) order, so the same patch
   * produces the same bytes in every runtime.
   *
   * `undefined`/absent ⇒ untouched, `null` ⇒ cleared, anything else ⇒ modified.
   * "Cleared" and "modified to null" are the same three bytes, so for a
   * `Maybe<T>` field "cleared" and "set to none" are the same patch.
   *
   * MIGRATION NOTE (roadmap 1.1 — explicit field indices): integer keys would be
   * smaller, but require stable per-field numbers the language does not have yet.
   */
  static makePartialFormatter<T>(
    schema: IonPartialSchema | FieldSchema
  ): IonFormatter<IonPartial<T>> {
    const fields = normalizePartialSchema(schema);
    const byName = new Map(fields.map((f) => [f.name, f]));

    return {
      read(reader: CborReader): IonPartial<T> {
        const result: IonPartial<T> = {};

        const readEntry = () => {
          const key = reader.readTextString();
          const field = byName.get(key);
          if (!field) {
            reader.skipValue();
            return;
          }

          if (reader.peekState() === CborReaderState.Null) {
            reader.readNull();
            (result as any)[key] = null;
            return;
          }

          (result as any)[key] = IonFormatterStorage.readPartialFieldValue(
            reader,
            field
          );
        };

        // `readStartMap()` returns null for an indefinite-length map. The old
        // `length ?? 0` read zero entries and then desynchronised the reader.
        const length = reader.readStartMap();
        if (length === null) {
          while (reader.peekState() !== CborReaderState.EndMap) {
            if (reader.peekState() === CborReaderState.Finished)
              throw new IonMalformedValueError(
                "Partial",
                "unexpected end of CBOR data inside a Partial map"
              );
            readEntry();
          }
        } else {
          for (let i = 0; i < length; i++) readEntry();
        }

        reader.readEndMap();
        return result;
      },

      write(writer: CborWriter, value: IonPartial<T>): void {
        // Schema order, so the bytes match the other runtimes.
        const present = fields.filter(
          (f) => (value as any)[f.name] !== undefined
        );

        const touched = Object.keys(value).filter(
          (k) => (value as any)[k] !== undefined
        );
        if (touched.length !== present.length) {
          const unknown = touched.filter((k) => !byName.has(k));
          throw new Error(
            `Partial has ${touched.length} touched field(s) but only ` +
              `${present.length} are covered by the schema. Unknown field(s): ` +
              `${unknown.length ? unknown.join(", ") : "<none>"}`
          );
        }

        writer.writeStartMap(present.length);

        for (const field of present) {
          writer.writeTextString(field.name);

          const v = (value as any)[field.name];
          if (v === null) {
            writer.writeNull();
            continue;
          }

          IonFormatterStorage.writePartialFieldValue(writer, field, v);
        }

        writer.writeEndMap();
      },
    };
  }

  /**
   * Registers the `Partial<T>` codec under `name` so generated code can resolve it
   * with `IonFormatterStorage.get<IonPartial<T>>(name)`.
   *
   * The conventional name is the lookup name of the partial type, e.g.
   * `"IonPartial<Vector>"`.
   *
   * @example
   * IonFormatterStorage.registerPartial("IonPartial<Vector>", [
   *   { name: "x", type: "f4" },
   *   { name: "y", type: "f4" },
   *   { name: "z", type: "f4" },
   * ]);
   */
  static registerPartial<T>(
    name: string,
    schema: IonPartialSchema | FieldSchema
  ): IonFormatter<IonPartial<T>> {
    const formatter = IonFormatterStorage.makePartialFormatter<T>(schema);
    IonFormatterStorage.register(name, formatter);
    return formatter;
  }
}

export class IonMaybe<T> {
  private constructor(
    public readonly value: T | null,
    public readonly hasValue: boolean
  ) {}

  static Some<T>(value: T): IonMaybe<T> {
    return new IonMaybe(value, true);
  }

  static None<T>(): IonMaybe<T> {
    return new IonMaybe<T>(null as any, false);
  }

  unwrap(): T {
    if (!this.hasValue) throw new Error("Cannot unwrap a None value.");
    return this.value as T;
  }

  static from<T>(value: T | null | undefined): IonMaybe<T> {
    if (value === null || value === undefined) return IonMaybe.None<T>();
    return IonMaybe.Some(value);
  }

  unwrapOrDefault(): T | null {
    return this.value as T | null;
  }
}
