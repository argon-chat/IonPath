namespace ion.runtime.network;

/// <summary>
/// Server-side settings for <c>stream</c> calls. Configure through
/// <c>AddIonProtocol(i =&gt; i.ConfigureStreams(o =&gt; …))</c> or
/// <c>services.Configure&lt;IonStreamOptions&gt;(…)</c>.
/// </summary>
/// <remarks>
/// The defaults mirror SignalR's (15 s keep-alive, 30 s client timeout), so a deployment that
/// moved from a hub keeps the same failure-detection latency. Any <see cref="TimeSpan"/> set to
/// <see cref="TimeSpan.Zero"/> or <see cref="Timeout.InfiniteTimeSpan"/> disables that check.
/// </remarks>
public sealed class IonStreamOptions
{
    /// <summary>
    /// How long the server may stay silent before it sends a PING. Must be well below
    /// the client's own server-timeout; the server announces it in READY so the client can adapt.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a client may stay silent before the connection is dropped as
    /// <see cref="IonDisconnectReason.Timeout"/>. Only silence counts: a client that reads slowly but
    /// keeps pinging is slow, not dead, and is not dropped for it.
    /// </summary>
    public TimeSpan ClientTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the server waits for the argument message after the upgrade.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a close may take — the final frames, the close handshake — before the transport is
    /// aborted. Applies to errors, to a client leaving, and to host shutdown; a stream that
    /// completed, or that server code closed, waits for a live client instead
    /// (<see cref="CompletionDrainTimeout"/>).
    /// </summary>
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest a completed (or server-closed) stream waits for a slow client to take the rest of
    /// it. Within it, only the client's silence for <see cref="ClientTimeout"/> ends the wait early —
    /// a slow reader that keeps pinging gets everything, as it would have had the stream gone on.
    /// </summary>
    public TimeSpan CompletionDrainTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long the server waits for the stream method to observe cancellation before it runs the
    /// disconnect hooks anyway. A method that ignores its token is logged, not waited on forever.
    /// </summary>
    public TimeSpan StreamStopTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The largest single message a client may send, argument message included. Larger closes the connection with 1009.</summary>
    public int MaxReceiveMessageSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// How many pushed messages may wait for one connection. A connection that overflows it is
    /// dropped as <see cref="IonDisconnectReason.SlowConsumer"/> rather than allowed to stall the
    /// broadcaster. Items the stream method yields itself are not queued — they are paced by the socket.
    /// </summary>
    public int OutboundQueueCapacity { get; set; } = 1024;

    /// <summary>How many input items may wait for the stream method before the server stops reading the socket.</summary>
    public int InputQueueCapacity { get; set; } = 64;

    /// <summary>
    /// How long a resumable session outlives its transport: the stream method keeps running, pushes
    /// queue up, groups are kept, and a client that comes back within this window picks up exactly
    /// where it left off — nothing lost, nothing twice. Only then does the connection end, with the
    /// reason the transport was lost for. <see cref="TimeSpan.Zero"/> turns resuming off; clients
    /// that do not ask for it (<c>resume=1</c>) never get it.
    /// </summary>
    /// <remarks>
    /// The same trade-off as SignalR's stateful reconnect: a session that is never resumed holds its
    /// stream method, its DI scope and up to <see cref="ResumeBufferSize"/> bytes for this long
    /// after the client vanished. Resuming needs the client to reach the same server again — behind a
    /// load balancer, that means sticky sessions.
    /// </remarks>
    public TimeSpan ResumeWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many bytes of sent but unacknowledged frames a resumable session keeps for replay. When
    /// they are spent the session stops sending until the client acknowledges — it slows down, it
    /// never drops. A single frame larger than this is still sent, alone.
    /// </summary>
    public int ResumeBufferSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// WebSocket-level PING/PONG timeout, on top of the protocol's own heartbeat. Off by default: a
    /// client that only processes control frames while its consumer is reading would be killed for
    /// being slow rather than dead, and the protocol heartbeat already covers a dead one.
    /// </summary>
    public TimeSpan? TransportKeepAliveTimeout { get; set; }
}
