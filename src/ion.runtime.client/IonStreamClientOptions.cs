namespace ion.runtime.client;

using System.Net.Security;
using System.Reflection;

/// <summary>The transports a stream call can run over.</summary>
public enum IonStreamTransportKind
{
    /// <summary>HTTP/3 WebTransport: one QUIC bidirectional stream per call. HTTPS only.</summary>
    WebTransport,

    /// <summary>A WebSocket per call. Works everywhere, including in-memory test servers.</summary>
    WebSocket
}

/// <summary>Client-side settings for <c>stream</c> calls; see <see cref="IonClient.WithStreamOptions"/>.</summary>
/// <remarks>
/// Any <see cref="TimeSpan"/> set to <see cref="TimeSpan.Zero"/> or
/// <see cref="Timeout.InfiniteTimeSpan"/> disables that check.
/// </remarks>
public sealed class IonStreamClientOptions
{
    /// <summary>
    /// The transports to try, in order. The first one that completes the handshake carries the call;
    /// one that fails before that falls through to the next. Transports that cannot work here —
    /// WebTransport over <c>http://</c>, or without QUIC support — are skipped silently. A call that
    /// reconnects starts again from the top of this list.
    /// </summary>
    public IList<IonStreamTransportKind> Transports { get; set; } =
        [IonStreamTransportKind.WebTransport, IonStreamTransportKind.WebSocket];

    /// <summary>
    /// How a call whose connection dropped comes back; null turns reconnecting off, and every
    /// disconnect is then thrown to the consumer.
    /// </summary>
    /// <remarks>
    /// <para>Only failures that retrying can fix are retried: a lost or silent transport, a transport
    /// that could not be opened, a server CLOSE that allows it (a restart, say), a ticket exchange
    /// that failed on the network or with 5xx/408/429. A normal end, a server ERROR, a CLOSE that
    /// does not allow it, a refused ticket or upgrade (4xx), a protocol violation and the consumer
    /// leaving are final. The first connection counts too: a server that is down at the start is
    /// waited for.</para>
    ///
    /// <para>With <see cref="Resume"/> the call first resumes its session — nothing lost, nothing
    /// twice, the consumer notices nothing but the pause. Only when the session is gone (the server
    /// restarted, the window ran out) is the call started afresh, which re-runs the stream method:
    /// items already received may come again, and items in flight when the connection dropped are
    /// lost. <see cref="OnReconnected"/> says which of the two happened.</para>
    /// </remarks>
    public IonStreamReconnectPolicy? Reconnect { get; set; } = new();

    /// <summary>
    /// Ask the server for a resumable session, so that a dropped connection is picked up exactly
    /// where it left off rather than started again. Needs <see cref="Reconnect"/>; the server decides
    /// whether it allows it (<c>IonStreamOptions.ResumeWindow</c>).
    /// </summary>
    public bool Resume { get; set; } = true;

    /// <summary>
    /// How many bytes of sent but unacknowledged input a resumable call keeps for replay. When they
    /// are spent, the input stream waits for the server's acknowledgements — it slows down, it never
    /// drops. A single item larger than this is still sent, alone.
    /// </summary>
    public int ResumeBufferSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Called before each reconnect attempt, on a background thread. It must not throw or block; what
    /// it throws is ignored.
    /// </summary>
    public Action<IonStreamReconnecting>? OnReconnecting { get; set; }

    /// <summary>
    /// Called once a call is back — resumed, or started afresh — on a background thread. It must not
    /// throw or block; what it throws is ignored.
    /// </summary>
    public Action<IonStreamReconnected>? OnReconnected { get; set; }

    /// <summary>How long the client may stay silent before it sends a PING. Lowered to half the server's announced timeout when that is shorter.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the server may stay silent before the call fails with <see cref="IonDisconnectReason.Timeout"/>. Raised to twice the server's announced keep-alive when that is longer.</summary>
    public TimeSpan ServerTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long connecting and waiting for the server's READY may take, per transport attempt.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a WebTransport attempt may take to connect before the next transport is tried. Short
    /// on purpose: a network that drops UDP fails by timing out, and every call pays for it until
    /// <see cref="WebTransportRetryAfter"/> kicks in.
    /// </summary>
    public TimeSpan WebTransportConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// After a WebTransport attempt to a server fails, how long later calls to that server go straight
    /// to the next transport.
    /// </summary>
    public TimeSpan WebTransportRetryAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a graceful close may take before the transport is aborted.</summary>
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The largest frame the server may send.</summary>
    public int MaxReceiveMessageSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>How many received items may wait for the consumer before the client stops reading.</summary>
    public int ReceiveQueueCapacity { get; set; } = 64;

    /// <summary>
    /// Certificate validation for the connections this client opens itself — WebTransport, and
    /// WebSockets when no custom factory is given. Null uses the platform's default validation.
    /// </summary>
    public RemoteCertificateValidationCallback? ServerCertificateValidation { get; set; }

    /// <summary>Whether calls ask for resumable sessions: <see cref="Resume"/>, and a way back to use them.</summary>
    internal bool Resumable => Resume && Reconnect is not null;

    internal IonStreamClientOptions Clone() => new()
    {
        Transports = Transports.ToList(),
        Reconnect = Reconnect?.Clone(),
        Resume = Resume,
        ResumeBufferSize = ResumeBufferSize,
        OnReconnecting = OnReconnecting,
        OnReconnected = OnReconnected,
        KeepAliveInterval = KeepAliveInterval,
        ServerTimeout = ServerTimeout,
        HandshakeTimeout = HandshakeTimeout,
        WebTransportConnectTimeout = WebTransportConnectTimeout,
        WebTransportRetryAfter = WebTransportRetryAfter,
        CloseTimeout = CloseTimeout,
        MaxReceiveMessageSize = MaxReceiveMessageSize,
        ReceiveQueueCapacity = ReceiveQueueCapacity,
        ServerCertificateValidation = ServerCertificateValidation
    };
}

/// <summary>
/// How long to wait between reconnect attempts: exponential from <see cref="InitialDelay"/>, capped
/// at <see cref="MaxDelay"/>, with full jitter — the same policy as the TypeScript client.
/// </summary>
public sealed class IonStreamReconnectPolicy
{
    /// <summary>The upper bound of the first delay.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The upper bound of any delay.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Reconnect attempts in a row before the last failure is thrown. The count starts over once a connection is back.</summary>
    public int MaxAttempts { get; set; } = int.MaxValue;

    /// <summary>
    /// Each delay is uniform in <c>[0, bound)</c> rather than exactly the bound — so a server restart
    /// is not met by every client at the same instant.
    /// </summary>
    public bool Jitter { get; set; } = true;

    /// <summary>The delay before attempt <paramref name="attempt"/>, counting from 1.</summary>
    public TimeSpan DelayFor(int attempt)
    {
        var initial = Math.Max(0, InitialDelay.TotalMilliseconds);
        var bound = Math.Min(Math.Max(0, MaxDelay.TotalMilliseconds), initial * Math.Pow(2, Math.Min(attempt - 1, 30)));
        return TimeSpan.FromMilliseconds(Jitter ? Random.Shared.NextDouble() * bound : bound);
    }

    internal IonStreamReconnectPolicy Clone() => new()
    {
        InitialDelay = InitialDelay,
        MaxDelay = MaxDelay,
        MaxAttempts = MaxAttempts,
        Jitter = Jitter
    };
}

/// <summary>A stream call is about to try to reconnect.</summary>
/// <param name="Interface">The service interface.</param>
/// <param name="Method">The stream method.</param>
/// <param name="Attempt">1 for the first attempt after a drop.</param>
/// <param name="Delay">How long the call waits before the attempt.</param>
/// <param name="Cause">What the previous attempt, or the connection, ended with.</param>
public sealed record IonStreamReconnecting(Type Interface, MethodInfo Method, int Attempt, TimeSpan Delay, Exception Cause);

/// <summary>A stream call is connected again.</summary>
/// <param name="Interface">The service interface.</param>
/// <param name="Method">The stream method.</param>
/// <param name="Attempts">How many attempts it took.</param>
/// <param name="Resumed">
/// True when the session was resumed: nothing was lost or repeated. False when the call was started
/// afresh: the stream method runs again from the start.
/// </param>
/// <param name="ConnectionId">The server's id for the connection — unchanged when resumed.</param>
public sealed record IonStreamReconnected(Type Interface, MethodInfo Method, int Attempts, bool Resumed, string ConnectionId);
