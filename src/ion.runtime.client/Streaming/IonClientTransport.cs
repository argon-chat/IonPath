namespace ion.runtime.client;

using System.Runtime.CompilerServices;
using System.Buffers;
using System.Net.WebSockets;

/// <summary>One frame off the wire, or the server's graceful end of its side.</summary>
/// <remarks><see cref="Payload"/> is borrowed: it stays valid until the next receive on the same transport.</remarks>
internal readonly struct IonClientFrame(bool isClose, bool isBinary, ReadOnlyMemory<byte> payload)
{
    public static IonClientFrame Close => new(true, false, default);

    public bool IsClose { get; } = isClose;
    public bool IsBinary { get; } = isBinary;
    public ReadOnlyMemory<byte> Payload { get; } = payload;
}

/// <summary>A frame larger than <see cref="IonStreamClientOptions.MaxReceiveMessageSize"/>.</summary>
internal sealed class IonClientFrameTooLargeException(int limit)
    : Exception($"The server sent a frame larger than {limit} bytes.");

/// <summary>
/// What a stream call needs from a transport: whole frames in, whole frames out, a graceful end of
/// our side, and a kill switch. One reader and one writer at a time.
/// </summary>
internal abstract class IonClientTransport : IAsyncDisposable
{
    public abstract IonStreamTransportKind Kind { get; }

    /// <summary>The close code the server sent, where the transport has one.</summary>
    public WebSocketCloseStatus? PeerCloseStatus { get; protected set; }

    public string? PeerCloseDescription { get; protected set; }

    /// <summary>True once both sides ended gracefully.</summary>
    public abstract bool IsClosed { get; }

    public abstract ValueTask<IonClientFrame> ReceiveAsync(int maxSize, CancellationToken ct);

    public abstract ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct);

    /// <summary>Ends our side gracefully: a WebSocket close frame, or a FIN on the QUIC stream.</summary>
    public abstract ValueTask CloseOutputAsync(string? description, CancellationToken ct);

    /// <summary>Kills the transport. Safe at any time, from any thread, more than once.</summary>
    public abstract void Abort();

    public abstract ValueTask DisposeAsync();
}

/// <summary>A frame is one WebSocket message.</summary>
internal sealed class WebSocketClientTransport(WebSocket ws) : IonClientTransport
{
    private const int ScratchSize = 16 * 1024;

    private byte[]? scratch = ArrayPool<byte>.Shared.Rent(ScratchSize);
    private byte[]? assembly;

    public override IonStreamTransportKind Kind => IonStreamTransportKind.WebSocket;

    public override bool IsClosed => ws.State == WebSocketState.Closed;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public override async ValueTask<IonClientFrame> ReceiveAsync(int maxSize, CancellationToken ct)
    {
        var buffer = scratch ?? throw new ObjectDisposedException(nameof(WebSocketClientTransport));
        var result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close)
            return PeerClosed();

        if (result.Count > maxSize)
            throw new IonClientFrameTooLargeException(maxSize);

        var binary = result.MessageType == WebSocketMessageType.Binary;
        if (result.EndOfMessage)
            return new IonClientFrame(false, binary, buffer.AsMemory(0, result.Count));

        var length = result.Count;
        Grow(Math.Max(length * 2, 4096), 0);
        buffer.AsSpan(0, length).CopyTo(assembly);

        while (!result.EndOfMessage)
        {
            result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return PeerClosed();

            if (length + result.Count > maxSize)
                throw new IonClientFrameTooLargeException(maxSize);

            Grow(length + result.Count, length);
            buffer.AsSpan(0, result.Count).CopyTo(assembly.AsSpan(length));
            length += result.Count;
        }

        return new IonClientFrame(false, binary, assembly.AsMemory(0, length));
    }

    private IonClientFrame PeerClosed()
    {
        PeerCloseStatus = ws.CloseStatus;
        PeerCloseDescription = ws.CloseStatusDescription;
        return IonClientFrame.Close;
    }

    private void Grow(int size, int keep)
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

    public override async ValueTask CloseOutputAsync(string? description, CancellationToken ct)
    {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,
                IonStreamProtocol.TruncateCloseDescription(description), ct).ConfigureAwait(false);
    }

    public override void Abort() => ws.Abort();

    public override ValueTask DisposeAsync()
    {
        ws.Dispose();

        var s = Interlocked.Exchange(ref scratch, null);
        if (s is not null)
            ArrayPool<byte>.Shared.Return(s);

        var a = Interlocked.Exchange(ref assembly, null);
        if (a is not null)
            ArrayPool<byte>.Shared.Return(a);

        return ValueTask.CompletedTask;
    }
}
