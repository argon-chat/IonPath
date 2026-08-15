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
