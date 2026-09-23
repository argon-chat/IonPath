namespace ion.runtime.network;

using System.Net.WebSockets;
using System.Reflection;
using System.Security.Claims;

/// <summary>Where a stream connection is in its life.</summary>
public enum IonStreamState
{
    /// <summary>Upgraded and handshaken; the connect hooks are running. Nothing has been sent yet.</summary>
    Connecting,

    /// <summary>Accepted. Items flow.</summary>
    Connected,

    /// <summary>
    /// A resumable session lost its transport and waits, for up to
    /// <see cref="IonStreamOptions.ResumeWindow"/>, for the client to come back. Nothing has
    /// ended: the stream method runs, pushes queue, groups hold, <c>ConnectionAborted</c> has not
    /// fired. Back to <see cref="Connected"/> when the client resumes.
    /// </summary>
    Reconnecting,

    /// <summary>A disconnect was requested; the goodbye and the close handshake are in progress.</summary>
    Closing,

    /// <summary>Gone. The disconnect hooks are running or have run.</summary>
    Closed
}

/// <summary>
/// One live <c>stream</c> call — the Ion counterpart of SignalR's <c>HubCallerContext</c>, plus the
/// group and push surface SignalR spreads over <c>Groups</c> and <c>Clients.Caller</c>.
/// </summary>
/// <remarks>
/// <para>Reach it from a service implementation through <see cref="IIonStreamContextAccessor"/>, or
/// take it as the argument of <see cref="IIonStreamLifecycle"/>'s hooks. It stays readable after the
/// connection has closed, so a disconnect hook can still see its groups and items.</para>
///
/// <para>It knows nothing about HTTP, so code that only pushes to connections — a worker that talks
/// to clients through the backplane — needs no web stack. On a server, the upgrade request is one
/// call away: <c>context.GetHttpContext()</c>, as SignalR's <c>Context.GetHttpContext()</c>.</para>
/// </remarks>
public interface IIonStreamContext
{
    /// <summary>Unique per connection, never reused. Announced to the client in READY.</summary>
    string ConnectionId { get; }

    /// <summary>The service interface the stream belongs to.</summary>
    Type Interface { get; }

    /// <summary>The stream method.</summary>
    MethodInfo Method { get; }

    /// <summary>The CLR type the stream yields — what <see cref="SendAsync{T}"/> accepts.</summary>
    Type ElementType { get; }

    /// <summary>
    /// <c>wt</c> for WebTransport, <c>ws</c> for WebSocket — the transport the session is on now: a
    /// resumed session may have come back on another kind than it started on.
    /// </summary>
    string TransportName { get; }

    /// <summary>The upgrade request's principal.</summary>
    ClaimsPrincipal User { get; }

    /// <summary>
    /// The key <see cref="IIonStreamConnections.User"/> matches on. Defaults to the principal's
    /// <see cref="ClaimTypes.NameIdentifier"/>; set it in <see cref="IIonStreamLifecycle.OnConnectedAsync"/>
    /// when identity comes from the ticket instead.
    /// </summary>
    string? UserIdentifier { get; set; }

    /// <summary>The client's <c>X-Ion-Session-Id</c> (or <c>sid</c> query parameter).</summary>
    string? SessionId { get; }

    /// <summary>The connection's correlation id — supplied by the client or generated.</summary>
    string? CorrelationId { get; }

    /// <summary>What <see cref="IIonTicketExchange.OnExchangeTransactionAsync"/> returned for this connection's ticket.</summary>
    object? Ticket { get; }

    /// <summary>The connection's DI scope — the one the service implementation was resolved from.</summary>
    IServiceProvider Services { get; }

    /// <summary>Free-form per-connection state, as SignalR's <c>Context.Items</c>.</summary>
    IDictionary<object, object?> Items { get; }

    /// <summary>The groups this connection is in right now (a snapshot).</summary>
    IReadOnlyCollection<string> Groups { get; }

    /// <summary>When the upgrade was accepted.</summary>
    DateTimeOffset ConnectedAt { get; }

    IonStreamState State { get; }

    /// <summary>
    /// Fires the moment the connection starts to end, for any reason — the client leaving, a
    /// timeout, a server-side close. It is the same token the stream method receives.
    /// </summary>
    /// <remarks>
    /// On a resumable session a lost transport is not an end: the state goes to
    /// <see cref="IonStreamState.Reconnecting"/> and this fires only if the client does not come
    /// back within <see cref="IonStreamOptions.ResumeWindow"/>.
    /// </remarks>
    CancellationToken ConnectionAborted { get; }

    /// <summary>Why the connection ended; null while it is alive.</summary>
    IonDisconnectInfo? Disconnect { get; }

    /// <summary>
    /// Completes after the connection is fully gone: the socket closed, the stream method stopped,
    /// every disconnect hook returned. Never faults.
    /// </summary>
    /// <remarks>Do not await it from the connection's own stream method — that is waiting for yourself to finish.</remarks>
    Task Completion { get; }

    /// <summary>Adds this connection to <paramref name="group"/>. A no-op once the connection is closing.</summary>
    ValueTask AddToGroupAsync(string group, CancellationToken ct = default);

    /// <summary>Removes this connection from <paramref name="group"/>.</summary>
    ValueTask RemoveFromGroupAsync(string group, CancellationToken ct = default);

    /// <summary>
    /// Pushes one item to this client out of band, alongside whatever the stream method yields.
    /// Pushed items keep their order among themselves.
    /// </summary>
    /// <returns>
    /// False when the connection is already closing, or when this push overflowed
    /// <see cref="IonStreamOptions.OutboundQueueCapacity"/> — in which case the connection is being
    /// dropped as <see cref="IonDisconnectReason.SlowConsumer"/>.
    /// </returns>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> is not what this stream yields.</exception>
    ValueTask<bool> SendAsync<T>(T item, CancellationToken ct = default);

    /// <summary>
    /// Ends the connection gracefully: pushed items still queued are flushed, the client gets a
    /// CLOSE carrying <paramref name="reason"/> and <paramref name="allowReconnect"/>, then the
    /// transport's own close runs. Returns at once; await <see cref="Completion"/> for the end.
    /// </summary>
    void Close(string? reason = null, bool allowReconnect = false);

    /// <summary>Kills the connection at once: no flush, no goodbye. The client sees a dropped transport.</summary>
    void Abort(string? reason = null);
}

/// <summary>Why and how a stream connection ended.</summary>
/// <param name="Reason">What ended it.</param>
/// <param name="Exception">
/// Null for a graceful end (<see cref="IsGraceful"/>), the cause otherwise — the same contract as
/// SignalR's <c>OnDisconnectedAsync(Exception?)</c>.
/// </param>
/// <param name="Message">The reason text: what server code passed to Close/Abort, or what the client said when it left.</param>
/// <param name="AllowReconnect">For a server-initiated close: whether the client was told it may come back.</param>
public sealed record IonDisconnectInfo(
    IonDisconnectReason Reason,
    Exception? Exception,
    string? Message,
    bool AllowReconnect)
{
    /// <summary>The close code the client sent, when it closed the WebSocket itself.</summary>
    public WebSocketCloseStatus? ClientCloseStatus { get; init; }

    /// <summary>True when both sides agreed the connection was over; false when it broke.</summary>
    public bool IsGraceful => Reason is IonDisconnectReason.Completed
        or IonDisconnectReason.ClientClosed
        or IonDisconnectReason.ServerClosed
        or IonDisconnectReason.ServerShutdown;

    /// <summary>True when the socket is killed without a close handshake.</summary>
    internal bool IsAbortive => Reason is IonDisconnectReason.TransportLost
        or IonDisconnectReason.Timeout
        or IonDisconnectReason.ServerAborted
        or IonDisconnectReason.SlowConsumer;

    public override string ToString()
        => Message is null ? Reason.ToString() : $"{Reason}: {Message}";
}

/// <summary>
/// Connect and disconnect hooks — SignalR's <c>OnConnectedAsync</c> / <c>OnDisconnectedAsync</c>.
/// </summary>
/// <remarks>
/// <para>Implement it on a service implementation to hook that service's streams, or register an
/// implementation with <c>AddStreamLifecycle&lt;T&gt;()</c> to hook every stream. Global hooks run
/// first on connect and last on disconnect.</para>
///
/// <para>The guarantees:</para>
/// <list type="bullet">
///   <item><see cref="OnConnectedAsync"/> runs before the client receives anything, and before the
///   stream method starts. Throwing from it rejects the connection: the client receives the error
///   (an <see cref="IonRequestException"/>'s own error, a sanitized one otherwise).</item>
///   <item><see cref="OnDisconnectedAsync"/> runs exactly once for every hook whose
///   <see cref="OnConnectedAsync"/> returned — whatever ended the connection, including a transport
///   that died silently (detected by the heartbeat) and a host shutdown.</item>
///   <item>By the time it runs the stream method has stopped (or overrun
///   <see cref="IonStreamOptions.StreamStopTimeout"/>), the connection has left every group, and the
///   connection's DI scope is still alive.</item>
///   <item>An exception it throws is logged and does not stop the other hooks.</item>
///   <item>On a resumable session, a transport that is lost and resumed is not a disconnect: the
///   hooks do not see it at all. <see cref="OnDisconnectedAsync"/> runs when the session ends —
///   and for a client that never came back, once <see cref="IonStreamOptions.ResumeWindow"/> has
///   run out, with the reason the transport was lost for.</item>
/// </list>
/// </remarks>
public interface IIonStreamLifecycle
{
    Task OnConnectedAsync(IIonStreamContext context) => Task.CompletedTask;

    Task OnDisconnectedAsync(IIonStreamContext context, IonDisconnectInfo info) => Task.CompletedTask;
}

/// <summary>
/// The current stream connection, for code resolved from a stream's DI scope — the service
/// implementation included. Null in a unary call's scope.
/// </summary>
public interface IIonStreamContextAccessor
{
    IIonStreamContext? Context { get; }
}

