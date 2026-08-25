namespace ion.runtime;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
//  Structural decode failures
// ═══════════════════════════════════════════════════════════════════════════════════════════════
// Everything in this file exists for one reason: before it, a malformed payload surfaced as
// System.InvalidOperationException, System.Formats.Cbor.CborContentException or
// System.OverflowException — three exception types from two libraries, neither of which is Ion. A
// caller could not catch "the peer sent something this schema cannot decode" with one handler, and
// could not tell it apart from a bug in its own code. Every reader path in this runtime now
// funnels through IonDecodeException; see IonDecodeGuard for the translation of anything that
// still escapes System.Formats.Cbor.

/// <summary>
/// The payload ended before the value being read was complete.
/// </summary>
/// <remarks>
/// This is the transport's failure, not the schema's: the bytes that arrived are a prefix of a
/// valid encoding. Distinguishing it from <see cref="IonFieldCountException"/> matters, because a
/// truncated frame is worth retrying and a schema mismatch is not.
/// </remarks>
public sealed class IonTruncatedPayloadException(string context, Exception? inner = null)
    : IonDecodeException($"Ion payload ended prematurely while reading {context}", inner)
{
    /// <summary>What was being read when the data ran out.</summary>
    public string Context { get; } = context;
}

/// <summary>
/// A message — or a method-argument envelope — declared fewer items than the reader's schema
/// requires.
/// </summary>
/// <remarks>
/// <para>
/// An Ion message is a positional CBOR array with no end marker of its own, so the <i>only</i>
/// thing standing between a short payload and a reader walking into the bytes that follow it is
/// the declared array length. A reader that finds the array exhausted with fields still to read
/// must stop and say so — never treat the shortfall as a skip distance, which is what
/// <c>Math.Abs(skipCount)</c> in <c>ReadEndArrayAndSkip</c> used to turn it into.
/// </para>
/// <para>
/// Both counts are reported: knowing only that the count was wrong does not tell a caller whether
/// the peer speaks an older revision of the schema or the frame was damaged.
/// </para>
/// </remarks>
public sealed class IonFieldCountException(string context, int expectedFields, int actualFields)
    : IonDecodeException(
        $"Ion message '{context}' expects {expectedFields} field(s), but the payload declares {actualFields}")
{
    /// <summary>The message or envelope whose field list was not satisfied.</summary>
    public string Context { get; } = context;

    /// <summary>How many items the reader's schema requires.</summary>
    public int ExpectedFields { get; } = expectedFields;

    /// <summary>How many items the payload actually declared.</summary>
    public int ActualFields { get; } = actualFields;
}

/// <summary>
/// A read was attempted while the enclosing definite-length CBOR container had no items left.
/// </summary>
/// <remarks>
/// The same wire fact as <see cref="IonFieldCountException"/>, reported from the read that hit it
/// rather than from the header check that could have predicted it: the payload is a positional
/// array declaring fewer items than this revision of the schema reads. Generated code emitted by a
/// current <c>ionc</c> checks the count up front and raises <see cref="IonFieldCountException"/>
/// with both numbers; code generated before that check reaches this instead.
/// </remarks>
public sealed class IonContainerExhaustedException(string context)
    : IonDecodeException(
        $"the enclosing definite-length CBOR array is exhausted; there is no item left to read '{context}' from " +
        "(the payload declares fewer positional fields than this schema revision expects)")
{
    /// <summary>The Ion type or field that had no bytes.</summary>
    public string Context { get; } = context;
}

/// <summary>
/// The CBOR item at the cursor is not of the major type the declared Ion type requires — a text
/// string where an integer was expected, a map where an array was expected, and so on.
/// </summary>
public sealed class IonUnexpectedCborTypeException(
    string context,
    string expected,
    string actual,
    Exception? inner = null)
    : IonDecodeException($"Ion '{context}': expected {expected} on the wire, got {actual}", inner)
{
    /// <summary>The Ion type or field whose formatter rejected the item.</summary>
    public string Context { get; } = context;

    /// <summary>What the formatter required.</summary>
    public string Expected { get; } = expected;

    /// <summary>What the payload actually carried.</summary>
    public string Actual { get; } = actual;
}

/// <summary>
/// An integer on the wire does not fit the declared fixed-width Ion type.
/// </summary>
/// <remarks>
/// <para>
/// CBOR integers carry no width, so <c>256</c> and <c>-1</c> are perfectly well-formed items that
/// simply cannot be a <c>u1</c>. Before this existed the readers just cast: <c>(byte)ReadInt32()</c>
/// turned 256 into 0 and -1 into 255, and an enum built on <c>u1</c> silently changed value with
/// nothing anywhere reporting a problem. A value outside the declared range is a decode failure.
/// </para>
/// <para>
/// The value is carried as text because it need not fit any CLR integer type — that is the whole
/// point of the failure.
/// </para>
/// </remarks>
public sealed class IonIntegerRangeException(string ionType, string value, string range, Exception? inner = null)
    : IonDecodeException($"Ion '{ionType}' cannot represent {value}; the declared range is {range}", inner)
{
    /// <summary>The declared Ion type, e.g. <c>u1</c>.</summary>
    public string IonType { get; } = ionType;

    /// <summary>The value that was rejected, rendered as text.</summary>
    public string Value { get; } = value;

    /// <summary>The inclusive range the declared type permits.</summary>
    public string Range { get; } = range;
}

/// <summary>
/// A <c>union</c> envelope carried a case index the reader's schema does not declare.
/// </summary>
/// <remarks>
/// Unlike an enum, an unknown union case cannot be carried through: the payload behind an unknown
/// index has an unknown shape, so there is nothing the reader can do but stop. The index is
/// reported so a caller can tell "the peer is newer than me" from "the peer sent garbage".
/// </remarks>
public sealed class IonInvalidUnionIndexException(string unionType, uint index, uint declaredCases)
    : IonDecodeException(
        $"Ion union '{unionType}' has no case {index}; this revision declares {declaredCases} case(s)")
{
    /// <summary>The union whose case list was not satisfied.</summary>
    public string UnionType { get; } = unionType;

    /// <summary>The index that was on the wire.</summary>
    public uint Index { get; } = index;

    /// <summary>How many cases this revision of the schema declares.</summary>
    public uint DeclaredCases { get; } = declaredCases;
}

/// <summary>
/// A <c>union</c> envelope was not the two-item <c>[index, payload]</c> array the format defines.
/// </summary>
/// <remarks>
/// The envelope is fixed at two items in every revision of every union — growth happens inside the
/// case payload, which is a message and skips its own tail. A third item is therefore not a newer
/// peer, it is a malformed frame, and reading past it would hand the stray item to whatever field
/// comes next in the enclosing message.
/// </remarks>
public sealed class IonUnionEnvelopeException(string unionType, int actualItems)
    : IonDecodeException(
        $"Ion union '{unionType}' envelope must be exactly [index, payload] (2 items), got {actualItems}")
{
    /// <summary>The union whose envelope was malformed.</summary>
    public string UnionType { get; } = unionType;

    /// <summary>How many items the envelope array declared.</summary>
    public int ActualItems { get; } = actualItems;
}

/// <summary>
/// A container that Ion requires to carry a definite length arrived with an indefinite one.
/// </summary>
/// <remarks>
/// A message is a positional array whose trailing-skip arithmetic is computed from the declared
/// length, so an indefinite-length message array has no defined meaning. Maps, sets and fixed-size
/// arrays <i>do</i> accept an indefinite length — only the positional forms need the count.
/// </remarks>
public sealed class IonIndefiniteLengthException(string context)
    : IonDecodeException(
        $"Ion '{context}' requires a definite-length CBOR container; the payload used an indefinite one")
{
    /// <summary>The Ion type or field that required a definite length.</summary>
    public string Context { get; } = context;
}

/// <summary>
/// The payload nests CBOR containers more deeply than <see cref="IonDecodeLimits.MaxDepth"/>
/// allows.
/// </summary>
/// <remarks>
/// <para>
/// Reading a nested message is recursive in all three Ion runtimes, so nesting depth is
/// attacker-controlled stack consumption — and it is reachable without knowing the schema, because
/// even a field the reader only <i>skips</i> has to be walked. In .NET a
/// <see cref="StackOverflowException"/> cannot be caught and takes the process with it; in Rust a
/// stack overflow aborts. The limit is checked before the recursion, so this is an ordinary
/// catchable failure instead.
/// </para>
/// </remarks>
public sealed class IonDepthLimitException(int limit, int depth)
    : IonDecodeException(
        $"Ion payload nests CBOR containers {depth} deep, past the limit of {limit} (see IonDecodeLimits.MaxDepth)")
{
    /// <summary>The configured limit.</summary>
    public int Limit { get; } = limit;

    /// <summary>The depth at which the read was stopped.</summary>
    public int Depth { get; } = depth;
}

/// <summary>
/// A declared container length is larger than the remaining input could possibly contain.
/// </summary>
/// <remarks>
/// Every CBOR data item occupies at least one byte, so a declared element count above the number
/// of bytes left is provably a lie. Checking it <i>before</i> allocating is what keeps a four-byte
/// payload from asking for a four-billion-element buffer.
/// </remarks>
public sealed class IonLengthOverclaimException(string context, long declared, long available)
    : IonDecodeException(
        $"Ion '{context}' declares {declared} item(s) but only {available} byte(s) of input remain")
{
    /// <summary>The container whose length was rejected.</summary>
    public string Context { get; } = context;

    /// <summary>The length the payload declared.</summary>
    public long Declared { get; } = declared;

    /// <summary>How many bytes of input were left.</summary>
    public long Available { get; } = available;
}
