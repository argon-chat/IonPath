namespace ion.runtime.network;

using System.Runtime.CompilerServices;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

/// <summary>One frame off the wire, or the peer's graceful end of its side.</summary>
/// <remarks><see cref="Payload"/> is borrowed: it stays valid until the next receive on the same transport.</remarks>
internal readonly struct IonTransportFrame(bool isClose, bool isBinary, ReadOnlyMemory<byte> payload)
{
    public static IonTransportFrame Close => new(true, false, default);

    public bool IsClose { get; } = isClose;
    public bool IsBinary { get; } = isBinary;
    public ReadOnlyMemory<byte> Payload { get; } = payload;
}

/// <summary>
/// What <see cref="IonStreamConnection"/> needs from a transport: whole frames in, whole frames out,
/// a graceful end of our side, and a kill switch. One reader and one writer at a time.
/// </summary>
internal abstract class IonStreamTransport : IAsyncDisposable
{
    /// <summary><c>ws</c> or <c>wt</c> — for logs and metrics.</summary>
    public abstract string Name { get; }

    /// <summary>The close code the peer sent, where the transport has one.</summary>
    public WebSocketCloseStatus? PeerCloseStatus { get; protected set; }

    /// <summary>The close description the peer sent, where the transport has one.</summary>
    public string? PeerCloseDescription { get; protected set; }

    /// <summary>True once both sides ended gracefully — nothing left to abort.</summary>
    public abstract bool IsClosed { get; }

    /// <exception cref="IonStreamMessageTooLargeException">The frame is larger than <paramref name="maxSize"/>.</exception>
    public abstract ValueTask<IonTransportFrame> ReceiveAsync(int maxSize, CancellationToken ct);

    public abstract ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct);

    /// <summary>Ends our side gracefully: a WebSocket close frame, or a FIN on a QUIC stream.</summary>
    public abstract ValueTask CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct);

    /// <summary>
    /// Reads and throws away everything until the peer ends its side — without assembling frames,
    /// so no size limit is needed and a peer that keeps sending cannot make it buffer anything.
    /// </summary>
    public abstract ValueTask DiscardUntilClosedAsync(CancellationToken ct);

    /// <summary>Kills the transport. Safe to call at any time, from any thread, more than once.</summary>
    public abstract void Abort();

    public abstract ValueTask DisposeAsync();
}

/// <summary>A frame is one WebSocket message; the WebSocket does the framing.</summary>
internal sealed class WebSocketStreamTransport(WebSocket ws) : IonStreamTransport
{
    private byte[] scratch = ArrayPool<byte>.Shared.Rent(IonStreamWire.ScratchSize);
    private byte[]? assembly;

    public override string Name => "ws";

    public override bool IsClosed => ws.State == WebSocketState.Closed;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public override async ValueTask<IonTransportFrame> ReceiveAsync(int maxSize, CancellationToken ct)
    {
        var result = await ws.ReceiveAsync(scratch.AsMemory(), ct).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close)
            return PeerClosed();

        if (result.Count > maxSize)
            throw new IonStreamMessageTooLargeException(maxSize);

        var binary = result.MessageType == WebSocketMessageType.Binary;
        if (result.EndOfMessage)
            return new IonTransportFrame(false, binary, scratch.AsMemory(0, result.Count));

        // Fragmented: assemble in a pooled buffer that grows by doubling and is kept for reuse.
        var length = result.Count;
        EnsureAssembly(Math.Max(length * 2, 4096), 0);
        scratch.AsSpan(0, length).CopyTo(assembly);

        while (!result.EndOfMessage)
        {
            result = await ws.ReceiveAsync(scratch.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return PeerClosed();

            if (length + result.Count > maxSize)
                throw new IonStreamMessageTooLargeException(maxSize);

            EnsureAssembly(length + result.Count, length);
            scratch.AsSpan(0, result.Count).CopyTo(assembly.AsSpan(length));
            length += result.Count;
        }

        return new IonTransportFrame(false, binary, assembly.AsMemory(0, length));
    }

    private IonTransportFrame PeerClosed()
    {
        PeerCloseStatus = ws.CloseStatus;
        PeerCloseDescription = ws.CloseStatusDescription;
        return IonTransportFrame.Close;
    }

    private void EnsureAssembly(int size, int keep)
    {
        if (assembly is not null && assembly.Length >= size)
            return;

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(size, (assembly?.Length ?? 0) * 2));
        if (assembly is not null)
        {
            assembly.AsSpan(0, keep).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(assembly);
        }

        assembly = grown;
    }

    public override ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        => ws.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);

    public override async ValueTask DiscardUntilClosedAsync(CancellationToken ct)
    {
        var buffer = scratch ?? throw new ObjectDisposedException(nameof(WebSocketStreamTransport));
        while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                PeerClosed();
                return;
            }
        }
    }

    public override async ValueTask CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
    {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await ws.CloseOutputAsync(status, IonStreamProtocol.TruncateCloseDescription(description), ct).ConfigureAwait(false);
    }

    public override void Abort() => ws.Abort();

    public override ValueTask DisposeAsync()
    {
        ws.Dispose();

        var s = Interlocked.Exchange(ref scratch, null!);
        if (s is not null)
            ArrayPool<byte>.Shared.Return(s);

        var a = Interlocked.Exchange(ref assembly, null);
        if (a is not null)
            ArrayPool<byte>.Shared.Return(a);

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A frame is <c>[varint length][frame]</c> on one bidirectional WebTransport stream; the stream's
/// FIN is the graceful end of a side, a reset is a dead transport.
/// </summary>
/// <remarks>
/// A frame that arrived in one pipe segment is handed out in place and released on the next
/// receive, so the common case copies nothing.
/// </remarks>
internal sealed class WebTransportStreamTransport(IWebTransportSession session, ConnectionContext stream) : IonStreamTransport
{
    /// <summary>H3_NO_ERROR — what a session that is going away on purpose is aborted with.</summary>
    private const int NoError = 0x100;

    private readonly PipeReader input = stream.Transport.Input;
    private readonly PipeWriter output = stream.Transport.Output;

    private SequencePosition consumed;
    private bool advancePending;
    private byte[]? spill;
    private volatile bool peerFinished;
    private volatile bool outputCompleted;
    private volatile bool outputCloseRequested;
    private int aborted;

    public override string Name => "wt";

    public override bool IsClosed => peerFinished && outputCompleted;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public override async ValueTask<IonTransportFrame> ReceiveAsync(int maxSize, CancellationToken ct)
    {
        if (advancePending)
        {
            input.AdvanceTo(consumed);
            advancePending = false;
        }

        while (true)
        {
            var result = await input.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (result.IsCanceled)
                throw new OperationCanceledException(ct);

            if (IonVarint.TryRead(buffer, out var length, out var prefix))
            {
                if (length > (ulong)maxSize)
                    throw new IonStreamMessageTooLargeException(maxSize);

                var total = prefix + (long)length;
                if (buffer.Length >= total)
                {
                    var frame = buffer.Slice(prefix, (long)length);
                    consumed = frame.End;
                    advancePending = true;

                    if (frame.IsSingleSegment)
                        return new IonTransportFrame(false, true, frame.First);

                    var size = (int)length;
                    if (spill is null || spill.Length < size)
                    {
                        if (spill is not null)
                            ArrayPool<byte>.Shared.Return(spill);
                        spill = ArrayPool<byte>.Shared.Rent(size);
                    }

                    frame.CopyTo(spill);
                    return new IonTransportFrame(false, true, spill.AsMemory(0, size));
                }
            }

            if (result.IsCompleted)
            {
                if (!buffer.IsEmpty)
                    throw new IonStreamProtocolException($"The stream ended inside a frame ({buffer.Length} bytes left over).");

                input.AdvanceTo(buffer.End);
                peerFinished = true;
                if (outputCloseRequested)
                    await CompleteOutputAsync().ConfigureAwait(false);
                return IonTransportFrame.Close;
            }

            input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public override async ValueTask DiscardUntilClosedAsync(CancellationToken ct)
    {
        if (advancePending)
        {
            input.AdvanceTo(consumed);
            advancePending = false;
        }

        while (!peerFinished)
        {
            var result = await input.ReadAsync(ct).ConfigureAwait(false);
            input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
                peerFinished = true;
            else if (result.IsCanceled)
                throw new OperationCanceledException(ct);
        }

        if (outputCloseRequested)
            await CompleteOutputAsync().ConfigureAwait(false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public override async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var prefix = IonVarint.Size((ulong)frame.Length);
        var span = output.GetSpan(prefix + frame.Length);
        IonVarint.Write(span, (ulong)frame.Length);
        frame.Span.CopyTo(span[prefix..]);
        output.Advance(prefix + frame.Length);

        var flushed = await output.FlushAsync(ct).ConfigureAwait(false);
        if (flushed.IsCanceled || flushed.IsCompleted)
            throw new IOException("The WebTransport stream no longer accepts data.");
    }

    /// <remarks>
    /// Kestrel ends the read side of a WebTransport stream the moment its write side completes, so a
    /// FIN sent first would leave the client's goodbye unreadable and the close could only end in an
    /// abort. Our FIN therefore waits for the client's: until then this only records the request, and
    /// the receive path completes the output once the client has finished its side. Everything the
    /// goodbye needed to say went out as a frame before this is called.
    /// </remarks>
    public override async ValueTask CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
    {
        if (outputCompleted)
            return;

        if (!peerFinished)
        {
            outputCloseRequested = true;
            return;
        }

        await CompleteOutputAsync().ConfigureAwait(false);
    }

    private async ValueTask CompleteOutputAsync()
    {
        if (outputCompleted)
            return;

        outputCompleted = true;
        await output.CompleteAsync().ConfigureAwait(false);
    }

    public override void Abort()
    {
        if (Interlocked.Exchange(ref aborted, 1) == 1)
            return;

        try { stream.Abort(new ConnectionAbortedException("The Ion stream was aborted.")); }
        catch (Exception) { }

        try { session.Abort(NoError); }
        catch (Exception) { }
    }

    public override async ValueTask DisposeAsync()
    {
        try { await stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { }

        var s = Interlocked.Exchange(ref spill, null);
        if (s is not null)
            ArrayPool<byte>.Shared.Return(s);
    }
}
