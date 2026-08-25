/**
 * Typed decode failures.
 *
 * A malformed payload must never surface as a bare `Error` with a prose message. Code bridging
 * Ion to HTTP, to a retry policy or to a log has to be able to tell "the peer sent something this
 * schema cannot represent" from "the peer sent garbage" — and it must do so with `instanceof`,
 * not by string-matching. Every class here carries the values that were rejected as fields.
 *
 * These mirror `ion.runtime.IonDecodeException` and its subclasses in C#, and the
 * `ion_rustcore::IonError` variants in Rust, so the three runtimes fail the same way on the same
 * bytes.
 */

/** Base class for every Ion decode failure. */
export class IonDecodeError extends Error {
  constructor(message: string, options?: { cause?: unknown }) {
    super(message, options);
    this.name = "IonDecodeError";
  }
}

/**
 * A `datetime` payload was not a parseable RFC 3339 date-time, or carried no offset.
 *
 * RFC 3339 requires an explicit offset; a local time without one is genuinely ambiguous, and
 * guessing UTC would move the instant by up to 14 hours, so it is rejected rather than assumed.
 */
export class IonDateTimeFormatError extends IonDecodeError {
  constructor(
    readonly text: string,
    readonly reason: string,
    options?: { cause?: unknown }
  ) {
    super(`Malformed Ion datetime '${text}': ${reason}`, options);
    this.name = "IonDateTimeFormatError";
  }
}

/** A CBOR item carried a tag the formatter does not accept for the declared Ion type. */
export class IonUnexpectedTagError extends IonDecodeError {
  constructor(
    readonly expectedTag: number | bigint,
    readonly actualTag: number | bigint,
    readonly ionType: string
  ) {
    super(`Expected CBOR tag ${expectedTag} for Ion type '${ionType}', got tag ${actualTag}`);
    this.name = "IonUnexpectedTagError";
  }
}

/** A CBOR item was structurally not what the declared Ion type requires. */
export class IonMalformedValueError extends IonDecodeError {
  constructor(readonly ionType: string, readonly reason: string) {
    super(`Malformed Ion '${ionType}': ${reason}`);
    this.name = "IonMalformedValueError";
  }
}

/**
 * A `Map<K,V>` payload contained the same key twice.
 *
 * Rejected rather than merged: last-wins and first-wins both make the decoded value depend on the
 * order entries happen to appear in, which is the very non-determinism the canonical key ordering
 * exists to remove.
 */
export class IonDuplicateMapKeyError extends IonDecodeError {
  constructor(readonly key: unknown) {
    super(`Duplicate key '${String(key)}' in an Ion Map payload; duplicate keys are rejected, not merged`);
    this.name = "IonDuplicateMapKeyError";
  }
}

/**
 * A `Set<T>` payload contained the same element twice.
 *
 * Rejected rather than collapsed: collapsing would let a three-element wire array decode as a
 * two-element set, a size change the caller can neither observe nor guard against.
 */
export class IonDuplicateSetElementError extends IonDecodeError {
  constructor(readonly element: unknown) {
    super(
      `Duplicate element '${String(element)}' in an Ion Set payload; duplicate elements are rejected, not collapsed`
    );
    this.name = "IonDuplicateSetElementError";
  }
}

/**
 * A fixed-size array `T[N]` was read from — or written with — a length other than `N`.
 *
 * Names **both** lengths: knowing only that the length was wrong does not tell a caller whether
 * the peer is on an older schema revision or the payload was truncated.
 */
export class IonFixedArrayLengthError extends IonDecodeError {
  constructor(readonly expectedLength: number, readonly actualLength: number) {
    super(`Ion fixed-size array declared length ${expectedLength}, got ${actualLength}`);
    this.name = "IonFixedArrayLengthError";
  }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════
//  Structural decode failures
// ════════════════════════════════════════════════════════════════════════════════════════════════
// Before these existed, every structural failure in this runtime was `throw new Error("Not array")`
// — or, in the generated union reader, `throw new Error()` with no message at all, which reaches a
// log as an empty string. A caller could neither catch a decode failure with one `instanceof` nor
// tell "the peer speaks an older schema" from "the peer sent garbage". These mirror the
// `ion.runtime.IonDecodeException` subclasses in C# and the `IonError` variants in Rust.

/** The payload ended before the value being read was complete. */
export class IonTruncatedPayloadError extends IonDecodeError {
  constructor(readonly context: string, options?: { cause?: unknown }) {
    super(`Ion payload ended prematurely while reading ${context}`, options);
    this.name = "IonTruncatedPayloadError";
  }
}

/**
 * A message — or a method-argument envelope — declared fewer items than the reader's schema
 * requires.
 *
 * An Ion message is a positional CBOR array with no end marker of its own, so the declared length
 * is the only thing between a short payload and a reader walking into the bytes that follow it.
 * Both counts are reported: one of them alone does not say whether the peer is on an older
 * revision or the frame was damaged.
 */
export class IonFieldCountError extends IonDecodeError {
  constructor(
    readonly context: string,
    readonly expectedFields: number,
    readonly actualFields: number
  ) {
    super(
      `Ion message '${context}' expects ${expectedFields} field(s), but the payload declares ${actualFields}`
    );
    this.name = "IonFieldCountError";
  }
}

/**
 * A read was attempted while the enclosing definite-length CBOR container had no items left.
 *
 * The same wire fact as {@link IonFieldCountError}, caught by the item accounting in `CborReader`
 * rather than by a header check. Until that accounting existed this reader simply kept going: it
 * read the next field from *beyond* the array and then skipped a further item, consuming five
 * bytes for a three-byte message.
 */
export class IonContainerOverreadError extends IonDecodeError {
  constructor(readonly container: "array" | "map", readonly declared: number) {
    super(
      `Ion read past the end of a definite-length CBOR ${container} of ${declared} item(s); ` +
        "the payload declares fewer positional fields than this schema revision expects"
    );
    this.name = "IonContainerOverreadError";
  }
}

/**
 * A definite-length container was closed with items still outstanding.
 *
 * A message closes its array with `readEndArrayAndSkip(declared - fieldsRead)` and therefore never
 * hits this; a `union` envelope, which is fixed at `[index, payload]`, does. Leaving the stray item
 * in the stream hands it to whatever field comes next in the enclosing message.
 */
export class IonContainerNotConsumedError extends IonDecodeError {
  constructor(readonly container: "array" | "map", readonly outstanding: number) {
    super(
      `Ion closed a definite-length CBOR ${container} with ${outstanding} item(s) unread; ` +
        "a positional container must consume or skip every item it declares"
    );
    this.name = "IonContainerNotConsumedError";
  }
}

/** The CBOR item at the cursor is not of the major type the declared Ion type requires. */
export class IonUnexpectedCborTypeError extends IonDecodeError {
  constructor(
    readonly context: string,
    readonly expected: string,
    readonly actual: string,
    options?: { cause?: unknown }
  ) {
    super(`Ion '${context}': expected ${expected} on the wire, got ${actual}`, options);
    this.name = "IonUnexpectedCborTypeError";
  }
}

/**
 * An integer on the wire does not fit the declared fixed-width Ion type.
 *
 * CBOR integers carry no width, so 2^40 is a perfectly well-formed item that simply cannot be an
 * `i4`. This runtime used to range-check nothing at all: it handed the caller whatever `Number()`
 * produced, and `& 0xff` turned 256 into 0 inside a `u1`-based enum.
 */
export class IonIntegerRangeError extends IonDecodeError {
  constructor(
    readonly ionType: string,
    readonly value: bigint | number,
    readonly range: string
  ) {
    super(`Ion '${ionType}' cannot represent ${value}; the declared range is ${range}`);
    this.name = "IonIntegerRangeError";
  }
}

/**
 * A `union` envelope carried a case index the reader's schema does not declare.
 *
 * Unlike an enum, an unknown union case cannot be carried through: the payload behind an unknown
 * index has an unknown shape. The index is reported — the generated reader used to
 * `throw new Error()` with an empty message.
 */
export class IonInvalidUnionIndexError extends IonDecodeError {
  constructor(
    readonly unionType: string,
    readonly index: number,
    readonly declaredCases: number
  ) {
    super(
      `Ion union '${unionType}' has no case ${index}; this revision declares ${declaredCases} case(s)`
    );
    this.name = "IonInvalidUnionIndexError";
  }
}

/**
 * A `union` envelope was not the two-item `[index, payload]` array the format defines.
 *
 * The envelope is fixed at two items in every revision of every union; growth happens inside the
 * case payload, which is a message and skips its own tail.
 */
export class IonUnionEnvelopeError extends IonDecodeError {
  constructor(readonly unionType: string, readonly actualItems: number) {
    super(
      `Ion union '${unionType}' envelope must be exactly [index, payload] (2 items), got ${actualItems}`
    );
    this.name = "IonUnionEnvelopeError";
  }
}

/**
 * A container that Ion requires to carry a definite length arrived with an indefinite one.
 *
 * Maps, sets and fixed-size arrays *do* accept an indefinite length — only the positional forms
 * need the count, because the trailing-skip arithmetic is computed from it.
 */
export class IonIndefiniteLengthError extends IonDecodeError {
  constructor(readonly context: string) {
    super(
      `Ion '${context}' requires a definite-length CBOR container; the payload used an indefinite one`
    );
    this.name = "IonIndefiniteLengthError";
  }
}

/**
 * The payload nests CBOR containers more deeply than {@link ION_MAX_DEPTH} allows.
 *
 * Reading — and skipping — a nested container is recursive, so nesting depth is
 * attacker-controlled stack consumption reachable without any knowledge of the schema: 100 KB of
 * 0x81 bytes is 100 000 levels deep. This runtime used to die of a `RangeError` from its own
 * recursive `skipValue` at whatever depth the JavaScript engine happened to give up, while C# and
 * Rust walked all of it without complaint.
 */
export class IonDepthLimitError extends IonDecodeError {
  constructor(readonly limit: number, readonly depth: number) {
    super(
      `Ion payload nests CBOR containers ${depth} deep, past the limit of ${limit} (see ION_MAX_DEPTH)`
    );
    this.name = "IonDepthLimitError";
  }
}

/**
 * A declared container or string length is larger than the remaining input could possibly contain.
 *
 * Every CBOR data item occupies at least one byte, so a declared count above the bytes left is
 * provably a lie — and checking it before allocating is what keeps a nine-byte payload from asking
 * for a four-billion-element buffer.
 */
export class IonLengthOverclaimError extends IonDecodeError {
  constructor(
    readonly context: string,
    readonly declared: number | bigint,
    readonly available: number
  ) {
    super(
      `Ion '${context}' declares ${declared} item(s)/byte(s) but only ${available} byte(s) of input remain`
    );
    this.name = "IonLengthOverclaimError";
  }
}

/**
 * The maximum number of simultaneously open CBOR containers a reader accepts.
 *
 * The same number is `IonDecodeLimits.MaxDepth` in C# and `MAX_DEPTH` in Rust. It is part of the
 * wire contract, not a local tuning knob: a payload one runtime accepts and the next refuses stops
 * being deliverable through a mixed-language path. 128 is an order of magnitude above anything a
 * hand-written schema nests and two orders below what threatens a native stack.
 */
export const ION_MAX_DEPTH = 128;
