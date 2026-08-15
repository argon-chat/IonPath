export type Guid = string;

export type IonArray<T> = T[];

// ─────────────────────────────────────────────────────────────────────────────
//  Partial<T>  ("T~" in Ion source)
//
//  A sparse patch: every field is either untouched, modified to a value, or
//  cleared. Encodes as a CBOR map — see IonFormatterStorage.makePartialFormatter
//  and /tests/golden/partial.golden.json for the wire spec and golden vectors.
//
//  In-memory representation, matching C#'s PartialState:
//    absent key / undefined  => UNTOUCHED   (PartialState.None)
//    null                    => CLEARED     (PartialState.Removed)
//    any other value         => MODIFIED    (PartialState.Modified)
//
//  NOTE: this is deliberately NOT called `Partial`. TypeScript already has a
//  built-in `Partial<T>` utility type, and generated code that emitted the bare
//  name type-checked cleanly and then serialised as a plain positional struct.
// ─────────────────────────────────────────────────────────────────────────────

export type IonPartial<T> = { [K in keyof T]?: T[K] | null };

/** Alias of {@link IonPartial}; both names refer to the same type. */
export type IonPartialOf<T> = IonPartial<T>;

/** Mirrors `ion.runtime.PartialState`. */
export enum IonPartialState {
  None = 0,
  Modified = 1,
  Removed = 2,
}

export function ionPartialState<T>(
  patch: IonPartial<T>,
  key: keyof T & string
): IonPartialState {
  const v = (patch as any)[key];
  if (v === undefined) return IonPartialState.None;
  if (v === null) return IonPartialState.Removed;
  return IonPartialState.Modified;
}

/** Names of all touched fields. Encoders emit them in schema order, not this order. */
export function ionPartialPresentFields<T>(patch: IonPartial<T>): string[] {
  return Object.keys(patch).filter((k) => (patch as any)[k] !== undefined);
}

/** Mark `key` as modified to `value`. */
export function ionPartialModify<T, K extends keyof T>(
  patch: IonPartial<T>,
  key: K,
  value: T[K]
): IonPartial<T> {
  (patch as any)[key] = value;
  return patch;
}

/** Mark `key` as cleared. */
export function ionPartialRemove<T, K extends keyof T>(
  patch: IonPartial<T>,
  key: K
): IonPartial<T> {
  (patch as any)[key] = null;
  return patch;
}

/** Mark `key` as untouched. */
export function ionPartialUntouch<T, K extends keyof T>(
  patch: IonPartial<T>,
  key: K
): IonPartial<T> {
  delete (patch as any)[key];
  return patch;
}

export type IonBytes = Uint8Array;
export type bytes = Uint8Array;

export interface DateOnly {
  year: number;
  month: number;
  day: number;
}

export interface TimeOnly {
  hour: number;
  minute: number;
  second: number;
  millisecond: number;
  microsecond: number;
}

/**
 * @deprecated Superseded by {@link IonDateTime}, which Ion's `datetime` now maps to.
 *
 * `Date` has millisecond resolution and cannot hold the 100ns ticks that C#'s `DateTimeOffset`
 * — the most precise `datetime` representation in the protocol — routinely carries, so every
 * C# -> TypeScript round trip through this shape silently truncated the value. Retained only so
 * existing generated code keeps compiling; nothing in the runtime writes it any more.
 */
export interface DateTimeOffset {
  date: Date;
  offsetMinutes: number;
}

export interface Duration {
  ticks: bigint;
}

// ─────────────────────────────────────────────────────────────────────────────
//  IonDateTime  (Ion `datetime`)
// ─────────────────────────────────────────────────────────────────────────────

const TICKS_PER_SECOND = 10_000_000n;
const TICKS_PER_MINUTE = 600_000_000n;
const TICKS_PER_MILLISECOND = 10_000n;
const SECONDS_PER_DAY = 86_400n;

/** Floor division; `/` on BigInt truncates toward zero, which is wrong before the epoch. */
function floorDiv(a: bigint, b: bigint): bigint {
  const q = a / b;
  return a % b !== 0n && a < 0n !== b < 0n ? q - 1n : q;
}

const pad = (n: number | bigint, width: number) => String(n).padStart(width, "0");

/** Howard Hinnant's `days_from_civil`, proleptic Gregorian. No `Date` involved. */
function daysFromCivil(y: bigint, m: bigint, d: bigint): bigint {
  y -= m <= 2n ? 1n : 0n;
  const era = floorDiv(y, 400n);
  const yoe = y - era * 400n;
  const doy = (153n * (m + (m > 2n ? -3n : 9n)) + 2n) / 5n + d - 1n;
  const doe = yoe * 365n + yoe / 4n - yoe / 100n + doy;
  return era * 146097n + doe - 719468n;
}

/** Inverse of {@link daysFromCivil}. */
function civilFromDays(z: bigint): [bigint, bigint, bigint] {
  z += 719468n;
  const era = floorDiv(z, 146097n);
  const doe = z - era * 146097n;
  const yoe = (doe - doe / 1460n + doe / 36524n - doe / 146096n) / 365n;
  const y = yoe + era * 400n;
  const doy = doe - (365n * yoe + yoe / 4n - yoe / 100n);
  const mp = (5n * doy + 2n) / 153n;
  const d = doy - (153n * mp + 2n) / 5n + 1n;
  const m = mp + (mp < 10n ? 3n : -9n);
  return [y + (m <= 2n ? 1n : 0n), m, d];
}

/**
 * RFC 3339 with the leniencies Ion readers allow: optional fraction of any length, `Z` or a
 * numeric offset, and a lower-case `t` or a space as the separator.
 */
const RFC3339 =
  /^(\d{4})-(\d{2})-(\d{2})[Tt ](\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(?:([Zz])|([+-])(\d{2}):(\d{2}))$/;

/**
 * An instant plus the UTC offset it was authored in — Ion's `datetime`.
 *
 * **This deliberately replaces `Date`.** A `Date` is a millisecond count, and Ion `datetime` is
 * specified at 100ns (the resolution of .NET's `DateTimeOffset`, the most precise representation
 * in the protocol). Storing one in a `Date` truncated four decimal digits off every value that
 * crossed from C#, silently. It also has no offset field, so `2023-12-31T19:00:00-05:00` and
 * `2024-01-01T00:00:00+00:00` — the same instant, two different authored values — were
 * indistinguishable.
 *
 * The value is held as a signed count of 100ns ticks since the Unix epoch plus the offset in
 * minutes, which is exact for every instant either C# or Rust can represent, and needs no
 * dependency. Use {@link toDate} when a `Date` is genuinely what an API wants, accepting the
 * truncation at that boundary rather than at the wire.
 *
 * Wire format and golden vectors: `/tests/golden/datetime.golden.json`.
 */
export class IonDateTime {
  /** Signed 100ns intervals since 1970-01-01T00:00:00Z. Negative before the epoch. */
  readonly unixTicks: bigint;

  /** Offset from UTC in minutes, -840..840 (±14:00). */
  readonly offsetMinutes: number;

  constructor(unixTicks: bigint, offsetMinutes: number = 0) {
    if (!Number.isInteger(offsetMinutes) || offsetMinutes < -840 || offsetMinutes > 840)
      throw new RangeError(
        `IonDateTime offset must be a whole number of minutes within ±14:00, got ${offsetMinutes}`
      );
    this.unixTicks = unixTicks;
    this.offsetMinutes = offsetMinutes;
  }

  /** The Unix epoch at UTC. */
  static get epoch(): IonDateTime {
    return new IonDateTime(0n, 0);
  }

  /** Now, at UTC. Millisecond resolution — that is all `Date.now()` can offer. */
  static now(offsetMinutes: number = 0): IonDateTime {
    return new IonDateTime(BigInt(Date.now()) * TICKS_PER_MILLISECOND, offsetMinutes);
  }

  /**
   * Adopts a `Date`. The result is exact but only as precise as its input: a `Date` carries
   * whole milliseconds, so the low four tick digits are zero.
   */
  static fromDate(date: Date, offsetMinutes: number = 0): IonDateTime {
    const ms = date.getTime();
    if (!Number.isFinite(ms)) throw new RangeError("IonDateTime.fromDate: invalid Date");
    return new IonDateTime(BigInt(ms) * TICKS_PER_MILLISECOND, offsetMinutes);
  }

  /** Builds from calendar fields plus a 100ns fraction (0..9_999_999). */
  static fromParts(
    year: number, month: number, day: number,
    hour: number, minute: number, second: number,
    fraction: number = 0, offsetMinutes: number = 0
  ): IonDateTime {
    const localSeconds =
      daysFromCivil(BigInt(year), BigInt(month), BigInt(day)) * SECONDS_PER_DAY +
      BigInt(hour) * 3600n + BigInt(minute) * 60n + BigInt(second);
    return new IonDateTime(
      (localSeconds - BigInt(offsetMinutes) * 60n) * TICKS_PER_SECOND + BigInt(fraction),
      offsetMinutes
    );
  }

  /**
   * Parses an RFC 3339 date-time. Fractional digits past the seventh are **truncated**, never
   * rounded — rounding could carry into the next second, and no two runtimes would agree on the
   * carry at every boundary.
   *
   * @throws {RangeError} if the text is not RFC 3339 or carries no offset. Wire decoding wraps
   *   this in `IonDateTimeFormatError`; this constructor is for application code.
   */
  static fromString(text: string): IonDateTime {
    const m = RFC3339.exec(text);
    if (!m)
      throw new RangeError(
        /^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}:\d{2}(\.\d+)?$/.test(text)
          ? `IonDateTime '${text}': RFC 3339 requires an explicit offset; a local time without one is ambiguous and is not assumed to be UTC`
          : `IonDateTime '${text}': not an RFC 3339 date-time`
      );

    const [, ys, mos, ds, hs, mis, ss, frac, z, sign, offH, offM] = m;
    const offsetMinutes = z
      ? 0
      : (sign === "-" ? -1 : 1) * (Number(offH) * 60 + Number(offM));

    const year = Number(ys), month = Number(mos), day = Number(ds);
    const hour = Number(hs), minute = Number(mis), second = Number(ss);

    if (month < 1 || month > 12) throw new RangeError(`IonDateTime '${text}': month out of range`);
    if (hour > 23 || minute > 59 || second > 59)
      throw new RangeError(`IonDateTime '${text}': time-of-day out of range`);

    const days = daysFromCivil(BigInt(year), BigInt(month), BigInt(day));
    const [cy, cm, cd] = civilFromDays(days);
    if (cy !== BigInt(year) || cm !== BigInt(month) || cd !== BigInt(day))
      throw new RangeError(`IonDateTime '${text}': day out of range for that month`);

    // Truncate, do not round.
    const fraction = frac ? Number((frac.length >= 7 ? frac.slice(0, 7) : frac.padEnd(7, "0"))) : 0;

    return new IonDateTime(
      (days * SECONDS_PER_DAY + BigInt(hour) * 3600n + BigInt(minute) * 60n + BigInt(second) -
        BigInt(offsetMinutes) * 60n) * TICKS_PER_SECOND + BigInt(fraction),
      offsetMinutes
    );
  }

  /** Alias of {@link fromString}. */
  static parse(text: string): IonDateTime {
    return IonDateTime.fromString(text);
  }

  /** Ticks since the epoch as seen on the local clock — i.e. including the offset. */
  private get localTicks(): bigint {
    return this.unixTicks + BigInt(this.offsetMinutes) * TICKS_PER_MINUTE;
  }

  /** The 100ns fraction of the second, 0..9_999_999. */
  get fraction(): number {
    const t = this.localTicks;
    return Number(t - floorDiv(t, TICKS_PER_SECOND) * TICKS_PER_SECOND);
  }

  /**
   * The same instant as a `Date`, **truncating** to whole milliseconds. This is the one lossy
   * operation on the type, and it is explicit so the loss happens where the caller asked for it
   * rather than on the wire.
   */
  toDate(): Date {
    return new Date(Number(floorDiv(this.unixTicks, TICKS_PER_MILLISECOND)));
  }

  /** The same instant expressed against a different offset. */
  toOffset(offsetMinutes: number): IonDateTime {
    return new IonDateTime(this.unixTicks, offsetMinutes);
  }

  /** The canonical Ion wire text: `YYYY-MM-DDTHH:MM:SS.fffffff±HH:MM`, always 33 characters. */
  toString(): string {
    const t = this.localTicks;
    const totalSeconds = floorDiv(t, TICKS_PER_SECOND);
    const fraction = t - totalSeconds * TICKS_PER_SECOND;
    const days = floorDiv(totalSeconds, SECONDS_PER_DAY);
    const sod = totalSeconds - days * SECONDS_PER_DAY;
    const [y, mo, d] = civilFromDays(days);

    const abs = Math.abs(this.offsetMinutes);
    const offset =
      `${this.offsetMinutes < 0 ? "-" : "+"}${pad(Math.floor(abs / 60), 2)}:${pad(abs % 60, 2)}`;

    return (
      `${pad(y, 4)}-${pad(mo, 2)}-${pad(d, 2)}` +
      `T${pad(sod / 3600n, 2)}:${pad((sod / 60n) % 60n, 2)}:${pad(sod % 60n, 2)}` +
      `.${pad(fraction, 7)}${offset}`
    );
  }

  /** Same instant **and** same offset. Use `a.unixTicks === b.unixTicks` for instant equality. */
  equals(other: IonDateTime): boolean {
    return this.unixTicks === other.unixTicks && this.offsetMinutes === other.offsetMinutes;
  }

  toJSON(): string {
    return this.toString();
  }
}

// ─────────────────────────────────────────────────────────────────────────────
//  IonDecimal  (Ion `decimal`)
// ─────────────────────────────────────────────────────────────────────────────

const DECIMAL_TEXT = /^([+-]?)(\d+)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$/;

/**
 * An exact decimal: `mantissa × 10^exponent`.
 *
 * **Deliberately not `number`.** A JS `number` is an IEEE-754 double, so `0.1 + 0.2 !== 0.3` and
 * a 28-digit C# `decimal` cannot survive a round trip through one. Money and any other
 * exact-precision quantity must not go near binary floating point, which is the whole reason Ion
 * has a `decimal` distinct from `f8`.
 *
 * Dependency-free by design — the mantissa is a native `bigint`, so there is no bignum library in
 * the bundle.
 *
 * The wire form (CBOR tag 4) is normalised: trailing zeros are stripped from the mantissa so that
 * `1.50` and `1.5` produce identical bytes. The in-memory value is *not* normalised, so
 * `toString()` still renders the trailing zeros you authored; use {@link normalized} to compare
 * or {@link equals} for numeric equality.
 *
 * Wire format and golden vectors: `/tests/golden/decimal.golden.json`.
 */
export class IonDecimal {
  readonly exponent: number;
  readonly mantissa: bigint;

  constructor(exponent: number, mantissa: bigint) {
    if (!Number.isInteger(exponent))
      throw new RangeError(`IonDecimal exponent must be an integer, got ${exponent}`);
    this.exponent = exponent;
    this.mantissa = mantissa;
  }

  static get zero(): IonDecimal {
    return new IonDecimal(0, 0n);
  }

  /** Alias of the constructor, for symmetry with the other runtimes' `from_parts`. */
  static fromParts(exponent: number, mantissa: bigint): IonDecimal {
    return new IonDecimal(exponent, mantissa);
  }

  /**
   * Parses a plain or scientific decimal string: `-1.50`, `0`, `1e-28`, `+3.14E+2`.
   * The authored scale is preserved — `"1.50"` yields `(-2, 150n)`, not `(-1, 15n)`.
   */
  static fromString(text: string): IonDecimal {
    const m = DECIMAL_TEXT.exec(text.trim());
    if (!m) throw new RangeError(`IonDecimal: '${text}' is not a decimal number`);

    const [, sign, intPart, fracPart = "", expPart] = m;
    const mantissa = BigInt(intPart + fracPart) * (sign === "-" ? -1n : 1n);
    return new IonDecimal((expPart ? Number(expPart) : 0) - fracPart.length, mantissa);
  }

  /**
   * Ion's canonical form: trailing zeros stripped from the mantissa, and zero as exactly
   * `(0, 0n)`. This is what the formatter writes.
   */
  normalized(): IonDecimal {
    if (this.mantissa === 0n) return this.exponent === 0 ? this : IonDecimal.zero;

    let e = this.exponent;
    let m = this.mantissa;
    while (m % 10n === 0n) {
      m /= 10n;
      e += 1;
    }
    return e === this.exponent ? this : new IonDecimal(e, m);
  }

  /** Plain (never scientific) decimal text, preserving the authored scale. */
  toString(): string {
    const negative = this.mantissa < 0n;
    const digits = (negative ? -this.mantissa : this.mantissa).toString();

    let body: string;
    if (this.exponent >= 0) {
      body = digits + "0".repeat(this.exponent);
    } else {
      const scale = -this.exponent;
      body =
        digits.length > scale
          ? `${digits.slice(0, digits.length - scale)}.${digits.slice(digits.length - scale)}`
          : `0.${"0".repeat(scale - digits.length)}${digits}`;
    }
    return negative ? `-${body}` : body;
  }

  /**
   * Numeric equality: `1.50` equals `1.5`. Compares the normalised forms, which is also exactly
   * the condition under which the two values produce identical wire bytes.
   */
  equals(other: IonDecimal): boolean {
    const a = this.normalized();
    const b = other.normalized();
    return a.exponent === b.exponent && a.mantissa === b.mantissa;
  }

  /** Exact structural equality, trailing zeros included: `1.50` is NOT identical to `1.5`. */
  isIdenticalTo(other: IonDecimal): boolean {
    return this.exponent === other.exponent && this.mantissa === other.mantissa;
  }

  /**
   * Lossy conversion to a `number`, for display and arithmetic that does not need exactness.
   * Named to make the loss impossible to perform by accident.
   */
  toNumberLossy(): number {
    return Number(this.toString());
  }

  toJSON(): string {
    return this.toString();
  }
}
