namespace ion.runtime;

using System.Numerics;

/// <summary>
/// Base class for every decode failure Ion raises on a malformed or unrepresentable payload.
/// <para>
/// <b>Why this hierarchy exists.</b> A malformed payload must never surface as an opaque
/// <see cref="Exception"/>, an <see cref="OverflowException"/> from an arithmetic conversion, or a
/// bare <see cref="System.Formats.Cbor.CborContentException"/> with a prose message. A caller
/// bridging Ion to HTTP, to a retry policy, or to a log has to be able to tell "the peer sent
/// something this schema cannot represent" from "the peer sent garbage" from "the transport
/// truncated the frame" — and it has to do so without string-matching a message. Every derived
/// type here carries the specific values that were rejected as typed properties.
/// </para>
/// <para>
/// These are thrown by the readers only. Writers are exact and do not need to report; the one
/// exception is <see cref="IonFixedArrayLengthException"/>, which a writer also raises when the
/// caller hands it an array whose length disagrees with the declared <c>N</c>.
/// </para>
/// </summary>
public class IonDecodeException : Exception
{
    public IonDecodeException(string message) : base(message)
    {
    }

    public IonDecodeException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// A <c>datetime</c> payload was not a parseable RFC 3339 date-time, or carried no offset.
/// </summary>
/// <remarks>
/// RFC 3339 requires an explicit offset. A local time with no offset is genuinely ambiguous, and
/// guessing UTC would silently move the instant by up to 14 hours, so it is rejected here rather
/// than assumed.
/// </remarks>
public sealed class IonDateTimeFormatException(string text, string reason, Exception? inner = null)
    : IonDecodeException($"Malformed Ion datetime '{text}': {reason}", inner)
{
    /// <summary>The offending text, exactly as it appeared inside the tag-0 string.</summary>
    public string Text { get; } = text;

    /// <summary>Why it was rejected.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// A <c>decimal</c> payload was a valid CBOR tag 4 decimal fraction, but its value cannot be
/// represented by <see cref="decimal"/>.
/// </summary>
/// <remarks>
/// <para>
/// CBOR tag 4 permits an arbitrary-precision mantissa and an arbitrary exponent;
/// <see cref="decimal"/> permits a 96-bit unscaled magnitude and a scale of 0..28. The overlap is
/// large but not total, and the gap is reachable from both the TypeScript and Rust runtimes, which
/// have no such limit. Decoding such a value must be a typed, inspectable failure rather than an
/// <see cref="OverflowException"/> escaping from a conversion, and must never be a silently
/// rounded result.
/// </para>
/// <para>
/// The mantissa/exponent are reported <i>after</i> canonicalisation (trailing zeros stripped), so
/// a value that only looked out of range because of trailing zeros is not reported here at all.
/// </para>
/// </remarks>
public sealed class IonDecimalRangeException(int exponent, BigInteger mantissa, string reason)
    : IonDecodeException(
        $"Ion decimal {mantissa}E{exponent:+0;-0;+0} is outside the range of System.Decimal: {reason}")
{
    /// <summary>The canonical exponent that was rejected.</summary>
    public int Exponent { get; } = exponent;

    /// <summary>The canonical mantissa that was rejected.</summary>
    public BigInteger Mantissa { get; } = mantissa;

    /// <summary>Which of the two <see cref="decimal"/> limits was exceeded.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// A CBOR item carried a tag the formatter does not accept for the declared Ion type.
/// </summary>
public sealed class IonUnexpectedTagException(ulong expectedTag, ulong actualTag, string ionType)
    : IonDecodeException($"Expected CBOR tag {expectedTag} for Ion type '{ionType}', got tag {actualTag}")
{
    public ulong ExpectedTag { get; } = expectedTag;

    public ulong ActualTag { get; } = actualTag;

    /// <summary>The Ion type whose formatter rejected the tag, e.g. <c>datetime</c> or <c>decimal</c>.</summary>
    public string IonType { get; } = ionType;
}

/// <summary>
/// A CBOR item was structurally not what the declared Ion type requires — for instance a tag 4
/// wrapping something other than a two-element array.
/// </summary>
public sealed class IonMalformedValueException(string ionType, string reason)
    : IonDecodeException($"Malformed Ion '{ionType}': {reason}")
{
    public string IonType { get; } = ionType;

    public string Reason { get; } = reason;
}

/// <summary>
/// A <c>Map&lt;K,V&gt;</c> payload contained the same key twice.
/// </summary>
/// <remarks>
/// Rejecting is the only defensible answer. Last-wins and first-wins both make the decoded value
/// depend on the order the entries happen to appear in — which is precisely the non-determinism
/// the canonical key ordering exists to remove — and silently collapsing them hides a size change
/// from the caller. A duplicate key is a malformed payload, not a merge instruction.
/// </remarks>
public sealed class IonDuplicateMapKeyException(object? key)
    : IonDecodeException($"Duplicate key '{key}' in an Ion Map payload; duplicate keys are rejected, not merged")
{
    /// <summary>The decoded key that appeared more than once.</summary>
    public object? Key { get; } = key;
}

/// <summary>
/// A <c>Set&lt;T&gt;</c> payload contained the same element twice.
/// </summary>
/// <remarks>
/// Rejecting, for the same reason as <see cref="IonDuplicateMapKeyException"/>: silently
/// collapsing would let a three-element wire array decode as a two-element set, a length change
/// the caller can neither observe nor guard against.
/// </remarks>
public sealed class IonDuplicateSetElementException(object? element)
    : IonDecodeException($"Duplicate element '{element}' in an Ion Set payload; duplicate elements are rejected, not collapsed")
{
    /// <summary>The decoded element that appeared more than once.</summary>
    public object? Element { get; } = element;
}

/// <summary>
/// A fixed-size array <c>T[N]</c> was read from — or written with — a length other than
/// <c>N</c>.
/// </summary>
/// <remarks>
/// This check is the entire point of the fixed-array feature, so the error names <b>both</b>
/// lengths: knowing only that the length was wrong does not tell a caller whether the peer is on
/// an older schema revision or the payload was truncated.
/// </remarks>
public sealed class IonFixedArrayLengthException(int expectedLength, int actualLength)
    : IonDecodeException($"Ion fixed-size array declared length {expectedLength}, got {actualLength}")
{
    /// <summary>The <c>N</c> declared in the Ion contract.</summary>
    public int ExpectedLength { get; } = expectedLength;

    /// <summary>The length actually present.</summary>
    public int ActualLength { get; } = actualLength;
}
