namespace ion.runtime;

using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Net.WebSockets;
using System.Text;
using network;

/// <summary>
/// The wire contract of an Ion stream: one WebSocket per <c>stream</c> call.
/// </summary>
/// <remarks>
/// <para><b>Negotiation.</b> Over WebSocket the client offers <c>ion!ticket#&lt;base56&gt;!ver#2</c>
/// (or <c>ion!ver#2</c> without a ticket) as the sub-protocol and the server echoes it. Over
/// WebTransport, where a browser can set neither, the same values travel as
/// <c>?ticket=…&amp;ver=2</c>. Version 2 is the only one: the v1 wire, which had no heartbeat and
/// no goodbye and so could never tell a dead peer from a quiet one, is gone.</para>
///
/// <para><b>Framing.</b> Over WebSocket one message is one frame. Over WebTransport the client
/// opens one bidirectional stream and every frame is prefixed with its length as a QUIC varint
/// (<see cref="IonVarint"/>); a stream FIN is the graceful end of that side.</para>
///
/// <para><b>Handshake.</b> The first client frame is the CBOR argument array, with no opcode.
/// From then on every frame in either direction is <c>[opcode][payload]</c>.</para>
///
/// <para>Besides <see cref="OpData"/>, <see cref="OpEnd"/> and <see cref="OpError"/>:</para>
/// <list type="bullet">
///   <item><see cref="OpReady"/> — server to client, once, after the server's connect hooks
///   accepted the connection. Carries the connection id and the server's heartbeat settings. It is
///   the barrier SignalR lacks: a client that has seen it knows the server-side join is done.</item>
///   <item><see cref="OpPing"/> — both directions, sent whenever a side has been silent for its
///   keep-alive interval. Any frame resets the peer's timeout, so a busy stream never pings.</item>
///   <item><see cref="OpClose"/> — a deliberate goodbye before the WebSocket close handshake.
///   Server to client it carries <c>[reason, allowReconnect]</c>; client to server
///   <c>[reason]</c>. It exists so that "the other side chose to leave" never has to be inferred
///   from a close code a proxy may have rewritten.</item>
/// </list>
///
/// <para><b>Input streams</b> (client to server): <see cref="OpData"/> carrying
/// <c>array(1)[item]</c> per item, then <see cref="OpEnd"/>.</para>
///
/// <para><b>Resumable sessions.</b> A client that can resume adds <c>resume=1</c> to the upgrade
/// URL's query. A server that allows it answers with a six-element READY,
/// <c>[connectionId, keepAliveMs, clientTimeoutMs, resumeToken, resumeWindowMs, inputBudget]</c>,
/// and from then on the session outlives its transport:</para>
/// <list type="bullet">
///   <item>Each side numbers the <i>reliable</i> frames it sends — <see cref="OpData"/>,
///   <see cref="OpEnd"/>, <see cref="OpError"/>, <see cref="OpClose"/> — 1, 2, 3… for the session's
///   whole life; nothing on the wire carries the number, both sides count. Control frames (PING,
///   READY, ACK, RESUME, RESUMED) are not numbered.</item>
///   <item>A receiver confirms what it has taken with <see cref="OpAck"/> — at least every
///   <see cref="AckEveryBytes"/> bytes or <see cref="AckEveryFrames"/> frames, on its keep-alive
///   timer whenever something is unconfirmed, and at once for a goodbye. A sender keeps every
///   reliable frame until it is acknowledged, up to a byte budget; when the budget is spent it
///   stops sending reliable frames — backpressure, never loss — while control frames still flow.
///   The client's budget is at most the server's <c>inputBudget</c>: the server acknowledges input
///   only once its stream method has taken it, and keeps reading — for the ACKs behind it — while
///   the method does not, holding at most that much; a client that sends more has broken the
///   protocol.</item>
///   <item>A transport that closes without an Ion goodbye is a lost transport, not a leaving peer.
///   The server then keeps the session — the stream method running, pushes queued, groups kept,
///   <c>ConnectionAborted</c> not fired — for <c>resumeWindowMs</c>.</item>
///   <item>To resume, the client opens a new transport to the same endpoint (a fresh ticket, as
///   always) and sends <see cref="OpResume"/> <c>[resumeToken, n]</c> instead of the arguments,
///   <c>n</c> being the server frames it has received. The server aborts whatever transport the
///   session still had, answers <see cref="OpResumed"/> <c>[m]</c> — the client frames it has
///   received — and resends its frames after <c>n</c>; the client resends its frames after
///   <c>m</c>. Both continue as if nothing had happened.</item>
///   <item>A session that no longer exists — it ended, its window ran out, the token is unknown or
///   names another endpoint or user — is refused with an <see cref="OpError"/> whose code is
///   <see cref="NotResumableCode"/>. The client may then start the call afresh.</item>
///   <item>A goodbye from the server (END, ERROR, CLOSE) counts as delivered once it is
///   acknowledged; a transport that dies before that is resumed to deliver it.</item>
/// </list>
/// </remarks>
public static class IonStreamProtocol
{
    /// <summary>One stream item. Server to client: the CBOR item. Client to server: <c>array(1)[item]</c>.</summary>
    public const byte OpData = 0x00;

    /// <summary>Server to client: the stream method completed. Client to server: end of the input stream.</summary>
    public const byte OpEnd = 0x01;

    /// <summary>An <see cref="IonProtocolError"/>. Server to client it ends the stream; client to server it faults the input stream.</summary>
    public const byte OpError = 0x02;

    /// <summary>Heartbeat, either direction, no payload.</summary>
    public const byte OpPing = 0x03;

    /// <summary>Deliberate close, either direction. See the type remarks for the payloads.</summary>
    public const byte OpClose = 0x04;

    /// <summary>
    /// Server to client, once: <c>[connectionId, keepAliveMs, clientTimeoutMs]</c>, followed by
    /// <c>resumeToken, resumeWindowMs</c> when the session is resumable.
    /// </summary>
    public const byte OpReady = 0x05;

    /// <summary>Resumable sessions, either direction: <c>uint n</c> — "I have your reliable frames 1..n".</summary>
    public const byte OpAck = 0x06;

    /// <summary>Client to server, as the first frame of a resuming transport: <c>[resumeToken, n]</c>.</summary>
    public const byte OpResume = 0x07;

    /// <summary>Server to client, the first frame on a resumed transport: <c>[m]</c>.</summary>
    public const byte OpResumed = 0x08;

    /// <summary>The query parameter by which a client asks for a resumable session.</summary>
    public const string ResumeQueryParameter = "resume";

    /// <summary>The ERROR code of a RESUME the server cannot honour.</summary>
    public const string NotResumableCode = "STREAM_NOT_RESUMABLE";

    /// <summary>A receiver acknowledges at least every this many bytes of reliable frames…</summary>
    public const int AckEveryBytes = 16 * 1024;

    /// <summary>…or this many reliable frames, whichever comes first.</summary>
    public const int AckEveryFrames = 64;

    /// <summary>The largest ACK frame: the opcode and a CBOR <c>uint64</c>.</summary>
    public const int MaxAckFrameSize = 10;

    /// <summary>
    /// The most frames a sender keeps unacknowledged, whatever their size: tiny frames would
    /// otherwise put tens of thousands in flight under the byte budget alone. With an ACK every
    /// <see cref="AckEveryFrames"/> frames this is dozens of round trips of slack — no brake on
    /// throughput, a bound on memory.
    /// </summary>
    public const int MaxUnacknowledgedFrames = 4096;

    /// <summary>The protocol version this runtime speaks — the only one there is.</summary>
    public const int Version = 2;

    /// <summary>The longest close description RFC 6455 allows (125 bytes of payload minus the 2-byte code).</summary>
    public const int MaxCloseDescriptionBytes = 123;

    /// <summary>Formats one sub-protocol entry.</summary>
    public static string SubProtocol(string? ticket, int version)
        => string.IsNullOrEmpty(ticket) ? $"ion!ver#{version}" : $"ion!ticket#{ticket}!ver#{version}";

    /// <summary>
    /// The version an <c>ion*</c> sub-protocol entry names; 1 (unsupported) when it names none, and
    /// 0 when the entry is not an Ion sub-protocol at all.
    /// </summary>
    public static int ParseVersion(string? subProtocol)
    {
        if (string.IsNullOrEmpty(subProtocol) || !subProtocol.StartsWith("ion", StringComparison.Ordinal))
            return 0;

        var marker = subProtocol.LastIndexOf("ver#", StringComparison.Ordinal);
        if (marker < 0)
            return 1;

        var start = marker + 4;
        var end = start;
        while (end < subProtocol.Length && char.IsAsciiDigit(subProtocol[end]))
            end++;

        return int.TryParse(subProtocol.AsSpan(start, end - start), out var version) && version > 0
            ? version
            : 1;
    }

    /// <summary>A frame with no payload.</summary>
    public static byte[] Frame(byte opcode) => [opcode];

    /// <summary><c>[opcode][payload]</c> in one array, which is what a WebSocket send wants.</summary>
    public static byte[] Frame(byte opcode, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 1];
        frame[0] = opcode;
        payload.CopyTo(frame.AsSpan(1));
        return frame;
    }

    /// <summary>Encodes a CBOR payload straight into a frame, without an intermediate array.</summary>
    public static byte[] Frame(byte opcode, CborWriter payload)
    {
        var frame = new byte[payload.BytesWritten + 1];
        frame[0] = opcode;
        payload.Encode(frame.AsSpan(1));
        return frame;
    }

    public static byte[] ErrorFrame(IonProtocolError error)
    {
        var writer = new CborWriter();
        IonFormatterStorage<IonProtocolError>.Write(writer, error);
        return Frame(OpError, writer);
    }

    public static IonProtocolError ReadError(ReadOnlyMemory<byte> payload)
        => IonFormatterStorage<IonProtocolError>.Read(new CborReader(payload));

    public static byte[] ReadyFrame(string connectionId, TimeSpan keepAlive, TimeSpan clientTimeout)
        => ReadyFrame(connectionId, keepAlive, clientTimeout, null, TimeSpan.Zero, 0);

    /// <summary>
    /// READY, carrying the resume token, the window and the input budget when
    /// <paramref name="resumeToken"/> is set.
    /// </summary>
    public static byte[] ReadyFrame(string connectionId, TimeSpan keepAlive, TimeSpan clientTimeout,
        string? resumeToken, TimeSpan resumeWindow, int inputBudget)
    {
        var writer = new CborWriter();
        writer.WriteStartArray(resumeToken is null ? 3 : 6);
        writer.WriteTextString(connectionId);
        writer.WriteUInt64(ToWireMs(keepAlive));
        writer.WriteUInt64(ToWireMs(clientTimeout));
        if (resumeToken is not null)
        {
            writer.WriteTextString(resumeToken);
            writer.WriteUInt64(ToWireMs(resumeWindow));
            writer.WriteUInt64((ulong)Math.Max(0, inputBudget));
        }

        writer.WriteEndArray();
        return Frame(OpReady, writer);
    }

    /// <summary>Reads a READY payload. Trailing elements a later version may add are skipped.</summary>
    public static IonStreamReady ReadReady(ReadOnlyMemory<byte> payload)
    {
        try
        {
            return ReadReadyCore(payload);
        }
        catch (InvalidOperationException ex)
        {
            // The reader says "wrong type here" this way; for a frame, that is malformed content.
            throw new CborContentException(ex.Message, ex);
        }
    }

    private static IonStreamReady ReadReadyCore(ReadOnlyMemory<byte> payload)
    {
        var reader = new CborReader(payload);
        var size = reader.ReadStartArray() ?? throw new CborContentException("READY must be a definite-length array");
        if (size < 3)
            throw new CborContentException($"READY carries {size} elements, expected at least 3");

        var id = reader.ReadTextString();
        var keepAlive = FromWireMs(reader.ReadUInt64());
        var timeout = FromWireMs(reader.ReadUInt64());

        // Positions 4 and 5 are the resume token and window. Read tolerantly: a server that adds
        // fields of its own without offering a resume puts anything else there (or null), and a
        // client that does not see a text token simply has no resumable session.
        string? token = null;
        var window = TimeSpan.Zero;
        var read = 3;
        if (size >= 4)
        {
            read++;
            if (reader.PeekState() == CborReaderState.TextString)
                token = reader.ReadTextString();
            else
                reader.SkipValue();

            if (token is { Length: 0 })
                throw new CborContentException("READY carries an empty resume token");
        }

        if (size >= 5)
        {
            read++;
            if (token is not null && reader.PeekState() == CborReaderState.UnsignedInteger)
                window = FromWireMs(reader.ReadUInt64());
            else
                reader.SkipValue();
        }

        long inputBudget = 0;
        if (size >= 6)
        {
            read++;
            if (token is not null && reader.PeekState() == CborReaderState.UnsignedInteger)
                inputBudget = (long)Math.Min(reader.ReadUInt64(), int.MaxValue);
            else
                reader.SkipValue();
        }

        for (var i = read; i < size; i++)
            reader.SkipValue();
        reader.ReadEndArray();

        return new IonStreamReady(id, keepAlive, timeout)
        {
            ResumeToken = token,
            ResumeWindow = token is null ? TimeSpan.Zero : window,
            ResumeInputBudget = (int)inputBudget
        };
    }

    /// <summary>Whether a frame with <paramref name="opcode"/> is numbered and acknowledged on a resumable session.</summary>
    public static bool IsReliable(byte opcode)
        => opcode is OpData or OpEnd or OpError or OpClose;

    /// <summary>
    /// Writes <c>[ACK][uint n]</c> into <paramref name="destination"/> (at least
    /// <see cref="MaxAckFrameSize"/> bytes) and returns its length. No writer, no allocation: an
    /// ACK goes out every few dozen frames.
    /// </summary>
    public static int WriteAck(Span<byte> destination, long received)
    {
        destination[0] = OpAck;
        var value = (ulong)received;
        var body = destination[1..];
        switch (value)
        {
            case < 24:
                body[0] = (byte)value;
                return 2;
            case <= byte.MaxValue:
                body[0] = 0x18;
                body[1] = (byte)value;
                return 3;
            case <= ushort.MaxValue:
                body[0] = 0x19;
                BinaryPrimitives.WriteUInt16BigEndian(body[1..], (ushort)value);
                return 4;
            case <= uint.MaxValue:
                body[0] = 0x1a;
                BinaryPrimitives.WriteUInt32BigEndian(body[1..], (uint)value);
                return 6;
            default:
                body[0] = 0x1b;
                BinaryPrimitives.WriteUInt64BigEndian(body[1..], value);
                return 10;
        }
    }

    /// <summary>Reads an ACK payload: one CBOR unsigned integer.</summary>
    public static long ReadAck(ReadOnlyMemory<byte> payload)
    {
        try
        {
            return ReadAckCore(payload);
        }
        catch (InvalidOperationException ex)
        {
            // The reader says "wrong type here" this way; for a frame, that is malformed content.
            throw new CborContentException(ex.Message, ex);
        }
    }

    /// <remarks>
    /// Decoded by hand, not with a <see cref="CborReader"/>: an ACK arrives every few dozen frames
    /// for the life of a stream, and one unsigned integer is not worth an allocation each.
    /// </remarks>
    private static long ReadAckCore(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        if (span.IsEmpty)
            throw new CborContentException("ACK carries no value");

        var initial = span[0];
        if (initial >> 5 != 0)
            throw new CborContentException("ACK is not an unsigned integer");

        var info = initial & 0x1f;
        ulong value;
        int size;
        switch (info)
        {
            case < 24:
                value = (ulong)info;
                size = 1;
                break;
            case 24 when span.Length >= 2:
                value = span[1];
                size = 2;
                break;
            case 25 when span.Length >= 3:
                value = BinaryPrimitives.ReadUInt16BigEndian(span[1..]);
                size = 3;
                break;
            case 26 when span.Length >= 5:
                value = BinaryPrimitives.ReadUInt32BigEndian(span[1..]);
                size = 5;
                break;
            case 27 when span.Length >= 9:
                value = BinaryPrimitives.ReadUInt64BigEndian(span[1..]);
                size = 9;
                break;
            default:
                throw new CborContentException("ACK is truncated or not a definite unsigned integer");
        }

        if (span.Length != size)
            throw new CborContentException("ACK carries more than one value");
        if (value > long.MaxValue)
            throw new CborContentException("ACK is out of range");
        return (long)value;
    }

    /// <summary>Client to server RESUME: <c>[resumeToken, received]</c>.</summary>
    public static byte[] ResumeFrame(string resumeToken, long received)
    {
        var writer = new CborWriter();
        writer.WriteStartArray(2);
        writer.WriteTextString(resumeToken);
        writer.WriteUInt64((ulong)received);
        writer.WriteEndArray();
        return Frame(OpResume, writer);
    }

    /// <summary>Reads a RESUME payload. Trailing elements a later version may add are skipped.</summary>
    public static (string Token, long Received) ReadResume(ReadOnlyMemory<byte> payload)
    {
        try
        {
            return ReadResumeCore(payload);
        }
        catch (InvalidOperationException ex)
        {
            // The reader says "wrong type here" this way; for a frame, that is malformed content.
            throw new CborContentException(ex.Message, ex);
        }
    }

    private static (string Token, long Received) ReadResumeCore(ReadOnlyMemory<byte> payload)
    {
        var reader = new CborReader(payload);
        var size = reader.ReadStartArray() ?? throw new CborContentException("RESUME must be a definite-length array");
        if (size < 2)
            throw new CborContentException($"RESUME carries {size} elements, expected at least 2");

        var token = reader.ReadTextString();
        var received = reader.ReadUInt64();
        for (var i = 2; i < size; i++)
            reader.SkipValue();
        reader.ReadEndArray();

        if (received > long.MaxValue)
            throw new CborContentException("RESUME count is out of range");
        return (token, (long)received);
    }

    /// <summary>Server to client RESUMED: <c>[received]</c>.</summary>
    public static byte[] ResumedFrame(long received)
    {
        var writer = new CborWriter();
        writer.WriteStartArray(1);
        writer.WriteUInt64((ulong)received);
        writer.WriteEndArray();
        return Frame(OpResumed, writer);
    }

    /// <summary>Reads a RESUMED payload. Trailing elements a later version may add are skipped.</summary>
    public static long ReadResumed(ReadOnlyMemory<byte> payload)
    {
        try
        {
            return ReadResumedCore(payload);
        }
        catch (InvalidOperationException ex)
        {
            // The reader says "wrong type here" this way; for a frame, that is malformed content.
            throw new CborContentException(ex.Message, ex);
        }
    }

    private static long ReadResumedCore(ReadOnlyMemory<byte> payload)
    {
        var reader = new CborReader(payload);
        var size = reader.ReadStartArray() ?? throw new CborContentException("RESUMED must be a definite-length array");
        if (size < 1)
            throw new CborContentException("RESUMED carries no count");

        var received = reader.ReadUInt64();
        for (var i = 1; i < size; i++)
            reader.SkipValue();
        reader.ReadEndArray();

        if (received > long.MaxValue)
            throw new CborContentException("RESUMED count is out of range");
        return (long)received;
    }

    /// <summary>Server to client CLOSE: <c>[reason?, allowReconnect]</c>.</summary>
    public static byte[] ServerCloseFrame(string? reason, bool allowReconnect)
    {
        var writer = new CborWriter();
        writer.WriteStartArray(2);
        if (reason is null) writer.WriteNull();
        else writer.WriteTextString(reason);
        writer.WriteBoolean(allowReconnect);
        writer.WriteEndArray();
        return Frame(OpClose, writer);
    }

    /// <summary>Client to server CLOSE: <c>[reason?]</c>.</summary>
    public static byte[] ClientCloseFrame(string? reason)
    {
        var writer = new CborWriter();
        writer.WriteStartArray(1);
        if (reason is null) writer.WriteNull();
        else writer.WriteTextString(reason);
        writer.WriteEndArray();
        return Frame(OpClose, writer);
    }

    /// <summary>
    /// Reads either direction's CLOSE payload. An empty payload is a close with no reason — the
    /// cheapest valid goodbye, and what a minimal client may send.
    /// </summary>
    public static (string? Reason, bool AllowReconnect) ReadClose(ReadOnlyMemory<byte> payload)
    {
        try
        {
            return ReadCloseCore(payload);
        }
        catch (InvalidOperationException ex)
        {
            // The reader says "wrong type here" this way; for a frame, that is malformed content.
            throw new CborContentException(ex.Message, ex);
        }
    }

    private static (string? Reason, bool AllowReconnect) ReadCloseCore(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
            return (null, false);

        var reader = new CborReader(payload);
        var size = reader.ReadStartArray() ?? throw new CborContentException("CLOSE must be a definite-length array");

        string? reason = null;
        var allowReconnect = false;

        if (size > 0)
        {
            if (reader.PeekState() == CborReaderState.Null) reader.ReadNull();
            else reason = reader.ReadTextString();
        }

        if (size > 1)
            allowReconnect = reader.ReadBoolean();

        for (var i = 2; i < size; i++)
            reader.SkipValue();
        reader.ReadEndArray();

        return (reason, allowReconnect);
    }

    /// <summary>
    /// Cuts a close description to what fits in a WebSocket close frame, on a UTF-8 boundary.
    /// </summary>
    /// <remarks>
    /// A longer description makes <c>CloseOutputAsync</c> throw, and a close that throws is a
    /// close that did not happen — the peer then sees a dropped connection instead of a goodbye.
    /// </remarks>
    public static string? TruncateCloseDescription(string? description)
    {
        if (description is null || Encoding.UTF8.GetByteCount(description) <= MaxCloseDescriptionBytes)
            return description;

        var length = description.Length;
        while (length > 0 && Encoding.UTF8.GetByteCount(description.AsSpan(0, length)) > MaxCloseDescriptionBytes - 3)
            length--;

        // Never split a surrogate pair.
        if (length > 0 && char.IsHighSurrogate(description[length - 1]))
            length--;

        return description[..length] + "...";
    }

    private static ulong ToWireMs(TimeSpan value)
        => value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan ? 0UL : (ulong)value.TotalMilliseconds;

    private static TimeSpan FromWireMs(ulong value)
        => value == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(value);
}

/// <summary>What the server announces in its READY frame.</summary>
/// <param name="ConnectionId">The server's id for this connection.</param>
/// <param name="KeepAliveInterval">How often the server pings when idle; infinite when it does not.</param>
/// <param name="ClientTimeout">How long the server waits for any frame before dropping the client; infinite when it does not.</param>
public readonly record struct IonStreamReady(string ConnectionId, TimeSpan KeepAliveInterval, TimeSpan ClientTimeout)
{
    /// <summary>The secret a RESUME presents; null when the session is not resumable.</summary>
    public string? ResumeToken { get; init; }

    /// <summary>How long the server keeps the session after losing its transport; infinite when it does not say.</summary>
    public TimeSpan ResumeWindow { get; init; }

    /// <summary>
    /// The most bytes of unacknowledged input the client may have in flight — the server holds
    /// that much for a stream method that is not reading, and no more. 0 when it does not say.
    /// </summary>
    public int ResumeInputBudget { get; init; }
}

/// <summary>Why a stream connection ended. Shared by the server hooks and the client exceptions.</summary>
public enum IonDisconnectReason
{
    /// <summary>The stream method returned and the client was told so.</summary>
    Completed,

    /// <summary>The client left on purpose: CLOSE frame, WebSocket close handshake, or a cancelled enumeration.</summary>
    ClientClosed,

    /// <summary>Server code closed the connection gracefully.</summary>
    ServerClosed,

    /// <summary>Server code aborted the connection — no goodbye, no flush.</summary>
    ServerAborted,

    /// <summary>The host is stopping. Clients are told they may reconnect.</summary>
    ServerShutdown,

    /// <summary>The stream method threw.</summary>
    Faulted,

    /// <summary>A connect hook refused the connection.</summary>
    Rejected,

    /// <summary>The socket died without a close handshake — a dead network, a killed process, a closed laptop lid.</summary>
    TransportLost,

    /// <summary>The peer went silent for longer than the timeout, or stopped reading long enough that a send stalled.</summary>
    Timeout,

    /// <summary>The peer sent something the protocol does not allow.</summary>
    ProtocolViolation,

    /// <summary>The connection could not keep up with pushed messages and its outbound queue overflowed.</summary>
    SlowConsumer
}

/// <summary>
/// The server closed the stream on purpose with a CLOSE frame — a kick, a revoked session, a
/// restart. <see cref="AllowReconnect"/> says whether coming straight back is welcome.
/// </summary>
public sealed class IonStreamClosedException(string? reason, bool allowReconnect)
    : IonRequestException(new IonProtocolError("STREAM_CLOSED", reason ?? "The server closed the stream."), (Exception?)null)
{
    /// <summary>The reason the server gave, or null when it gave none.</summary>
    public string? Reason { get; } = reason;

    /// <summary>True when the server expects the client to reconnect (a restart, a rebalance).</summary>
    public bool AllowReconnect { get; } = allowReconnect;
}

/// <summary>
/// The stream ended without the server saying so: the transport died, went silent past the
/// timeout, or violated the protocol. Always safe to retry.
/// </summary>
public sealed class IonStreamDisconnectedException : IonRequestException
{
    public IonStreamDisconnectedException(
        IonDisconnectReason reason,
        string message,
        Exception? innerException = null,
        WebSocketCloseStatus? closeStatus = null,
        string? closeDescription = null)
        : base(new IonProtocolError(CodeFor(reason), message), innerException)
    {
        Reason = reason;
        CloseStatus = closeStatus;
        CloseDescription = closeDescription;
    }

    /// <summary><see cref="IonDisconnectReason.TransportLost"/>, <see cref="IonDisconnectReason.Timeout"/> or <see cref="IonDisconnectReason.ProtocolViolation"/>.</summary>
    public IonDisconnectReason Reason { get; }

    /// <summary>The close code the server sent, when it closed the socket without an Ion goodbye.</summary>
    public WebSocketCloseStatus? CloseStatus { get; }

    /// <summary>The close description the server sent alongside <see cref="CloseStatus"/>.</summary>
    public string? CloseDescription { get; }

    private static string CodeFor(IonDisconnectReason reason) => reason switch
    {
        IonDisconnectReason.Timeout => "STREAM_TIMEOUT",
        IonDisconnectReason.ProtocolViolation => "PROTOCOL_VIOLATION",
        _ => "STREAM_DISCONNECTED"
    };
}
