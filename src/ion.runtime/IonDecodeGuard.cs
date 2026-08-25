namespace ion.runtime;

using System.Numerics;
using System.Runtime.CompilerServices;

/// <summary>
/// Limits every Ion reader enforces on a payload it did not produce.
/// </summary>
/// <remarks>
/// <para>
/// The same numbers are hard-coded in <c>packages/ion.webcore.js/src/cbor/CborReader.ts</c> and
/// <c>packages/ion.rustcore/src/formatter.rs</c>. They are part of the wire contract, not a local
/// tuning knob: if one runtime accepts a payload the next one refuses, a message that round-trips
/// through a TypeScript gateway stops being deliverable to a .NET peer.
/// </para>
/// </remarks>
public static class IonDecodeLimits
{
    /// <summary>
    /// The default maximum number of simultaneously open CBOR containers, <c>128</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nesting is the one dimension of a payload that costs <i>stack</i> rather than heap, and the
    /// stack is the one resource whose exhaustion is not recoverable — a .NET
    /// <see cref="StackOverflowException"/> cannot be caught, and a Rust stack overflow aborts the
    /// process. It is also reachable without knowing the schema: a field the reader only skips
    /// still has to be walked, so 100 KB of <c>0x81</c> bytes is 100 000 levels deep.
    /// </para>
    /// <para>
    /// 128 is chosen the same way <c>serde_json</c> (128) and protobuf (100) choose theirs: an
    /// order of magnitude above anything a hand-written schema nests, and two orders below what
    /// threatens a 1 MB stack. A message nests two containers per level (its own field array plus
    /// whatever collection carries the recursion), so 128 is roughly 60 levels of a recursive
    /// message.
    /// </para>
    /// </remarks>
    public const int DefaultMaxDepth = 128;

    private static int _maxDepth = DefaultMaxDepth;

    /// <summary>
    /// The maximum number of simultaneously open CBOR containers a reader will accept.
    /// </summary>
    /// <remarks>
    /// Raise it only in a process whose peers all agree; the three runtimes ship the same default,
    /// and a payload accepted by one and refused by another is worse than either answer.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than one.</exception>
    public static int MaxDepth
    {
        get => _maxDepth;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxDepth = value;
        }
    }
}

/// <summary>
/// Turns whatever <see cref="CborReader"/> throws into an <see cref="IonDecodeException"/>, and
/// enforces <see cref="IonDecodeLimits.MaxDepth"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a translation layer rather than per-call-site checks.</b> The reads that fail live in
/// generated code and in <see cref="System.Formats.Cbor"/>, neither of which can be taught about
/// Ion's error hierarchy. Every Ion read enters through <see cref="IonFormatterStorage{T}"/>, so
/// that is where the translation goes: one <c>try</c>/<c>catch</c> per value read, free until it
/// throws, and after it there is no path by which a decode failure reaches a caller as anything
/// other than an <see cref="IonDecodeException"/>.
/// </para>
/// <para>
/// <b>The diagnosis is not guesswork.</b> <see cref="CborReader.PeekState"/> after a failed read
/// says which of the three structural failures happened:
/// <see cref="CborReaderState.EndArray"/> means the enclosing definite-length array ran out of
/// items — a field count mismatch, the case that used to become a negative skip;
/// <see cref="CborReaderState.Finished"/> means the buffer ran out — a truncation; anything else
/// is the wrong major type, and the state names it.
/// </para>
/// </remarks>
public static class IonDecodeGuard
{
    /// <summary>
    /// Throws <see cref="IonDepthLimitException"/> if the reader is already at
    /// <see cref="IonDecodeLimits.MaxDepth"/> open containers.
    /// </summary>
    /// <remarks>
    /// Called before a formatter that may open another container recurses.
    /// <see cref="CborReader.CurrentDepth"/> is the reader's own count of open containers, so no
    /// separate bookkeeping — and therefore no way for the two counts to drift apart — is needed.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnsureDepth(CborReader reader)
    {
        var depth = reader.CurrentDepth;
        if (depth >= IonDecodeLimits.MaxDepth)
            throw new IonDepthLimitException(IonDecodeLimits.MaxDepth, depth);
    }

    /// <summary>
    /// <see cref="CborReader.PeekState"/>, with its truncation failure reported as an Ion error.
    /// </summary>
    /// <remarks>
    /// Several formatters branch on the peeked state before reading anything — a <c>Maybe</c> on
    /// <see cref="CborReaderState.Null"/>, a <c>Set</c> on <see cref="CborReaderState.Tag"/>. On a
    /// truncated payload the peek itself throws, so it needs the same translation as a read.
    /// </remarks>
    public static CborReaderState PeekState(CborReader reader, string context)
    {
        try
        {
            return reader.PeekState();
        }
        catch (IonDecodeException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new IonTruncatedPayloadException(context, e);
        }
    }

    /// <summary>
    /// Maps an exception thrown out of a reader onto the Ion decode hierarchy.
    /// </summary>
    /// <param name="reader">The reader as it stands after the failed read.</param>
    /// <param name="e">What was thrown.</param>
    /// <param name="context">The Ion type or field being read, for the message.</param>
    public static IonDecodeException Translate(CborReader reader, Exception e, string context)
    {
        if (e is IonDecodeException already)
            return already;

        // An arithmetic conversion inside System.Formats.Cbor: the item was a well-formed integer
        // that does not fit the CLR type the formatter asked for.
        if (e is OverflowException)
            return new IonIntegerRangeException(context, "the value on the wire", "the declared width", e);

        var state = PeekQuietly(reader);

        // The enclosing definite-length array is exhausted: the payload declares fewer positional
        // fields than this schema revision reads. This is the case that used to become
        // `Math.Abs(-1)` — a skip forward, out of the array and into the next frame.
        if (state is CborReaderState.EndArray or CborReaderState.EndMap)
            return new IonContainerExhaustedException(context);

        if (state is CborReaderState.Finished)
            return new IonTruncatedPayloadException(context, e);

        // CborContentException is System.Formats.Cbor's single failure type for "these bytes are
        // not valid CBOR", which covers both a frame that stops mid-item and one that is simply
        // wrong. Nothing left to read means the former.
        if (e is CborContentException)
            return reader.BytesRemaining == 0
                ? new IonTruncatedPayloadException(context, e)
                : new IonMalformedValueException(context, e.Message);

        // Anything else: the cursor is on a well-formed item of the wrong shape.
        if (e is InvalidOperationException)
            return new IonUnexpectedCborTypeException(context, "a value of this Ion type", Describe(state), e);

        return new IonDecodeException($"Ion '{context}' could not be decoded: {e.Message}", e);
    }

    /// <summary>
    /// <see cref="CborReader.PeekState"/> without letting its own failure replace the failure
    /// being diagnosed.
    /// </summary>
    private static CborReaderState PeekQuietly(CborReader reader)
    {
        try
        {
            return reader.PeekState();
        }
        catch (CborContentException)
        {
            // PeekState itself only throws when the buffer ends mid-item, which is a truncation.
            return CborReaderState.Finished;
        }
        catch (Exception)
        {
            return CborReaderState.Undefined;
        }
    }

    private static string Describe(CborReaderState state) => state switch
    {
        CborReaderState.UnsignedInteger => "an unsigned integer",
        CborReaderState.NegativeInteger => "a negative integer",
        CborReaderState.ByteString or CborReaderState.StartIndefiniteLengthByteString => "a byte string",
        CborReaderState.TextString or CborReaderState.StartIndefiniteLengthTextString => "a text string",
        CborReaderState.StartArray => "an array",
        CborReaderState.StartMap => "a map",
        CborReaderState.Tag => "a tagged item",
        CborReaderState.Boolean => "a boolean",
        CborReaderState.Null => "null",
        CborReaderState.Undefined => "undefined",
        CborReaderState.SimpleValue => "a simple value",
        CborReaderState.HalfPrecisionFloat or CborReaderState.SinglePrecisionFloat
            or CborReaderState.DoublePrecisionFloat => "a floating-point number",
        CborReaderState.Finished => "end of input",
        _ => state.ToString(),
    };
}

/// <summary>
/// Range-checked readers for Ion's fixed-width integer types.
/// </summary>
/// <remarks>
/// <para>
/// CBOR integers carry no width: <c>256</c> and <c>-1</c> are ordinary, well-formed major-type-0/1
/// items. The declared Ion type is the only thing that says how wide the value is allowed to be,
/// so the reader is the only place the check can happen — and until this existed it did not,
/// because <c>(byte)reader.ReadInt32()</c> is a truncating cast. 256 into a <c>u1</c> became
/// <c>0</c>; -1 became <c>255</c>. Where that <c>u1</c> was the base type of an enum, the value
/// silently landed on a different member.
/// </para>
/// <para>
/// Writers are untouched: an in-range value encodes to exactly the same bytes as before, which is
/// what the <c>float</c>, <c>datetime</c>, <c>decimal</c>, <c>collections</c> and <c>partial</c>
/// golden files pin.
/// </para>
/// </remarks>
public static class IonInteger
{
    /// <summary>Reads a signed integer and rejects anything outside <c>[min, max]</c>.</summary>
    public static long ReadSigned(CborReader reader, long min, long max, string ionType)
    {
        long value;
        try
        {
            value = reader.ReadInt64();
        }
        catch (OverflowException e)
        {
            // A CBOR unsigned integer above long.MaxValue. It is well-formed and definitively out
            // of range for every signed Ion type.
            throw new IonIntegerRangeException(ionType, "a value wider than 64 bits", $"[{min}, {max}]", e);
        }

        if (value < min || value > max)
            throw new IonIntegerRangeException(ionType, value.ToString(), $"[{min}, {max}]");

        return value;
    }

    /// <summary>Reads an unsigned integer and rejects anything negative or above <paramref name="max"/>.</summary>
    public static ulong ReadUnsigned(CborReader reader, ulong max, string ionType)
    {
        // Peeked rather than caught, so a negative value is reported as what it is rather than as
        // an overflow. `-1` reaching a `u1` is the exact shape of the enum defect.
        if (reader.PeekState() == CborReaderState.NegativeInteger)
        {
            var negative = reader.ReadInt64();
            throw new IonIntegerRangeException(ionType, negative.ToString(), $"[0, {max}]");
        }

        var value = reader.ReadUInt64();
        if (value > max)
            throw new IonIntegerRangeException(ionType, value.ToString(), $"[0, {max}]");

        return value;
    }

    /// <summary>
    /// Reads an arbitrary-precision integer and rejects anything outside <c>[min, max]</c>.
    /// </summary>
    /// <remarks>
    /// <c>i16</c>/<c>u16</c> arrive as a CBOR bignum, so the range check has to happen in
    /// <see cref="BigInteger"/> before the narrowing conversion rather than after it.
    /// </remarks>
    public static BigInteger ReadBig(CborReader reader, BigInteger min, BigInteger max, string ionType)
    {
        var value = reader.ReadBigInteger();
        if (value < min || value > max)
            throw new IonIntegerRangeException(ionType, value.ToString(), $"[{min}, {max}]");
        return value;
    }
}
