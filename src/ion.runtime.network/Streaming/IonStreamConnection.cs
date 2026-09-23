namespace ion.runtime.network;

using System.Runtime.CompilerServices;
using System.Buffers;
using System.Collections.Concurrent;
using System.Formats.Cbor;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// One stream call on the server: the transport (or, for a resumable session, the transports one
/// after another), the stream method feeding it, and everything that decides when and how it ends.
/// </summary>
/// <remarks>
/// <para><b>One reader, one writer, no locks.</b> The <b>receive loop</b> is the only reader and runs
/// for the whole life of a transport — even for a server-only stream, because the client's goodbye,
/// its heartbeat and its close all arrive on the read side, and a server that never reads never
/// learns the client left. The <b>send loop</b> is the only writer: the stream method's items, pushed
/// items, pings and the final goodbye all reach the transport through one bounded queue, in the
/// order they were queued. Nothing else touches the write side once it runs — no lock to contend
/// for, no allocation per contended send, and flushing the backlog before a goodbye is just the
/// goodbye being last in line.</para>
///
/// <para><b>Zero-copy items.</b> The stream method's frames are borrowed from the generated
/// executor's reused buffer. The pump queues the borrowed frame and waits on
/// <see cref="PumpAck"/> — a reusable value-task source — until the send loop has written it (or,
/// on a resumable session, copied it into the replay buffer), and only then asks the executor for
/// the next one.</para>
///
/// <para><b>One end.</b> Whatever ends the connection first calls <see cref="RequestDisconnect"/>,
/// and only that first call counts. It fires <see cref="ConnectionAborted"/>, and
/// <see cref="RunAsync"/> picks the goodbye the reason calls for. Two cancellation sources keep
/// "stop the user's work" apart from "kill the transport": cancelling a pending WebSocket send or
/// receive aborts the socket, so the user-visible token never reaches one.</para>
///
/// <para><b>Resumable sessions</b> (see <see cref="IonStreamProtocol"/>). The transport is a
/// <see cref="Link"/>, and losing one is not the end: <see cref="LinkLost"/> lets it go, the
/// session waits for <see cref="IonStreamOptions.ResumeWindow"/>, and <see cref="ResumeAsync"/>
/// attaches the client's next transport. Reliable frames are numbered and kept in a
/// <see cref="ReplayBuffer"/> until the client acknowledges them; the send loop replays what the
/// client missed before anything new. The queue, the pump, the replay buffer and the counters
/// belong to the session; only the loops belong to a link — and only one link's loops ever run, a
/// new link starting its own once the previous one's have stopped.</para>
/// </remarks>
internal sealed class IonStreamConnection : IIonHubConnection
{
    private static readonly byte[] PingFrame = [IonStreamProtocol.OpPing];
    private static readonly byte[] EndFrame = [IonStreamProtocol.OpEnd];

    /// <summary>Queue slots beyond the push capacity: the pump's one item, a ping, an ACK, the goodbye, the close and a stop.</summary>
    private const int ReservedSlots = 6;

    private readonly IonStreamOptions options;
    private readonly IonStreamConnectionManager manager;
    private readonly IonStreamResumeRegistry? resumeRegistry;
    private readonly ILogger log;
    private readonly bool detailedErrors;

    private readonly CancellationTokenSource connectionCts = new();
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly Channel<Outgoing> outbound;
    private readonly Channel<ReadOnlyMemory<byte>>? input;
    private readonly PumpAck pumpAck = new();
    private readonly TaskCompletionSource disconnectRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Link firstLink;

    /// <summary>Guards the link transitions: which link is current, and whether another may attach.</summary>
    private readonly Lock gate = new();

    private IonDisconnectInfo? disconnect;
    private int state = (int)IonStreamState.Connecting;
    private volatile bool discardPushes;    // the goodbye does not flush the backlog
    private volatile bool readySent;
    private bool inputEnded;                // receive loops only, one at a time
    private WebSocketCloseStatus? clientCloseStatus;
    private Task? pump;

    // ── links (under gate) ──
    private Link? link;                     // the link whose loops may run; null while detached
    private Link lastLink;                  // the newest link, current or not — the next one waits for it
    private bool ending;                    // RunAsync is past the point where a link may attach
    private bool expired;                   // the resume window ran out
    private bool goodbyeDelivered;          // the client acknowledged the goodbye
    private long lostAt;                    // when the last link was lost, 0 while attached
    private IonDisconnectReason lostReason;
    private Exception? lostException;
    private string lastTransportName;

    // ── resumable sessions ──
    private readonly bool resumable;        // asked for by the client and allowed by the server
    private string? resumeToken;
    private ReplayBuffer? replay;           // one send loop at a time, or ResumeAsync while none runs
    private long sentReliable;              // send loop writes, receive loop reads
    private long peerAcked;                 // receive loop writes, send loop reads
    private long goodbyeSeq;                // the goodbye's number, 0 until it is in the replay buffer
    private long inputReceived;             // receive loop writes
    private long inputReceivedBytes;        // receive loop writes
    private long inputAckedSent;            // send loop writes
    private long inputAckedBytes;           // send loop writes
    private volatile bool ackWanted;
    private int ackQueued;
    private Outgoing? pending;              // send loop: taken from the queue, not yet written or kept
    private bool goodbyeAppended;           // send loop: nothing reliable follows the goodbye
    private TaskCompletionSource? sendWake; // the send loop, waiting for replay room
    private TaskCompletionSource? stateWake;// the goodbye, waiting for its acknowledgement

    public IonStreamConnection(
        IonStreamTransport transport,
        HttpContext http,
        IServiceProvider services,
        Type @interface,
        MethodInfo method,
        Type elementType,
        bool acceptsInput,
        object? ticket,
        string? sessionId,
        string? correlationId,
        IonStreamOptions options,
        IonStreamConnectionManager manager,
        ILogger log,
        bool detailedErrors,
        IonStreamResumeRegistry? resumeRegistry = null)
    {
        this.options = options;
        this.manager = manager;
        this.log = log;
        this.detailedErrors = detailedErrors;
        this.resumeRegistry = resumeRegistry;
        resumable = resumeRegistry is not null && Millis(options.ResumeWindow) > 0;

        ConnectionId = Guid.NewGuid().ToString("N");
        HttpContext = http;
        Services = services;
        Interface = @interface;
        Method = method;
        ElementType = elementType;
        Ticket = ticket;
        SessionId = sessionId;
        CorrelationId = correlationId;
        UserIdentifier = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        ConnectedAt = DateTimeOffset.UtcNow;
        ConnectionAborted = connectionCts.Token;

        outbound = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(Math.Max(1, options.OutboundQueueCapacity) + ReservedSlots)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        if (acceptsInput)
            input = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(Math.Max(1, options.InputQueueCapacity))
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        firstLink = new Link(transport, http);
        link = lastLink = firstLink;
        lastTransportName = transport.Name;

        // Counting the client's frames starts with the first one after the arguments — a client may
        // send input before READY — so the replay buffer that marks a resumable session does too.
        if (resumable)
            replay = new ReplayBuffer();
    }

    public string ConnectionId { get; }
    public Type Interface { get; }
    public MethodInfo Method { get; }
    public Type ElementType { get; }
    public string TransportName => Volatile.Read(ref link)?.Transport.Name ?? Volatile.Read(ref lastTransportName);
    public ClaimsPrincipal User => HttpContext.User;
    public string? UserIdentifier { get; set; }
    public string? SessionId { get; }
    public string? CorrelationId { get; }
    public object? Ticket { get; }
    /// <summary>The upgrade request that started the session; public through <see cref="IonStreamContextHttpExtensions.GetHttpContext"/>.</summary>
    public HttpContext HttpContext { get; }
    public IServiceProvider Services { get; }
    public IDictionary<object, object?> Items { get; } = new ConcurrentDictionary<object, object?>();
    public IReadOnlyCollection<string> Groups => manager.GroupsOf(this);
    public DateTimeOffset ConnectedAt { get; }
    public IonStreamState State => (IonStreamState)Volatile.Read(ref state);
    public CancellationToken ConnectionAborted { get; }
    public IonDisconnectInfo? Disconnect => Volatile.Read(ref disconnect);
    public Task Completion => completion.Task;

    /// <summary>
    /// The group names. Also the monitor the manager takes for this connection's membership
    /// changes — per connection, so no two connections ever wait on each other.
    /// </summary>
    public HashSet<string> GroupSet { get; } = new(StringComparer.Ordinal);

    /// <summary>Set by the manager, under <see cref="GroupSet"/>'s monitor, once the connection has left the registry.</summary>
    public bool Removed { get; set; }

    /// <summary>The stream method's task, so the endpoint can keep the DI scope alive for a method that overran its stop timeout.</summary>
    internal Task? StreamTask => pump;

    /// <summary>How many times a client resumed this session.</summary>
    internal int Resumes { get; private set; }

    public ValueTask AddToGroupAsync(string group, CancellationToken ct = default)
    {
        manager.AddToGroup(this, group);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveFromGroupAsync(string group, CancellationToken ct = default)
    {
        manager.RemoveFromGroup(this, group);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> SendAsync<T>(T item, CancellationToken ct = default)
    {
        if (!Accepts(typeof(T), item))
            throw new ArgumentException(
                $"{Interface.Name}.{Method.Name} streams {ElementType.Name}; a {typeof(T).Name} cannot be pushed to it.",
                nameof(item));

        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryEnqueue(IonStreamPush.Encode(ElementType, item)));
    }

    public void Close(string? reason = null, bool allowReconnect = false)
        => RequestDisconnect(IonDisconnectReason.ServerClosed, null, reason, allowReconnect);

    public void Abort(string? reason = null)
        => RequestDisconnect(IonDisconnectReason.ServerAborted, new IonStreamAbortedException(reason), reason);

    public void Shutdown()
        => RequestDisconnect(IonDisconnectReason.ServerShutdown, null, "The server is shutting down.", allowReconnect: true);

    public bool Accepts(Type type, object? item)
        => ElementType.IsAssignableFrom(type) || (item is not null && ElementType.IsInstanceOfType(item));

    /// <summary>Queues a pushed frame; overflowing the queue drops the connection as a slow consumer.</summary>
    /// <remarks>The frame may be shared with other connections of the same broadcast; it is never written to.</remarks>
    public bool TryEnqueue(byte[] frame)
    {
        if (Volatile.Read(ref disconnect) is not null)
            return false;

        // The reserved slots are for the pump, pings, ACKs and the goodbye; pushes stop short of them.
        if (outbound.Reader.Count < options.OutboundQueueCapacity &&
            outbound.Writer.TryWrite(new Outgoing(OutgoingKind.Push, frame)))
            return true;

        // Refused: the queue is full, or completed — which happens only after a disconnect was
        // requested, in which case this call is a no-op.
        RequestDisconnect(IonDisconnectReason.SlowConsumer,
            new IonStreamSlowConsumerException(options.OutboundQueueCapacity));
        return false;
    }

    /// <summary>
    /// Records why the connection ends — the first caller wins — and starts the end: the stream
    /// method is cancelled, and an abortive reason kills the transport on the spot.
    /// </summary>
    internal bool RequestDisconnect(
        IonDisconnectReason reason,
        Exception? exception = null,
        string? message = null,
        bool allowReconnect = false)
    {
        var info = new IonDisconnectInfo(reason, exception, message ?? exception?.Message, allowReconnect)
        {
            ClientCloseStatus = clientCloseStatus
        };

        if (Interlocked.CompareExchange(ref disconnect, info, null) is not null)
            return false;

        if (Interlocked.CompareExchange(ref state, (int)IonStreamState.Closing, (int)IonStreamState.Connected) != (int)IonStreamState.Connected)
            Interlocked.CompareExchange(ref state, (int)IonStreamState.Closing, (int)IonStreamState.Reconnecting);

        // CancelAsync marks the token synchronously but runs its callbacks elsewhere, so a callback
        // registered by user code never runs inside the receive loop or on the caller's stack.
        var cancelled = connectionCts.CancelAsync();
        if (!cancelled.IsCompletedSuccessfully)
            _ = ObserveAsync(cancelled, log);

        if (info.IsAbortive)
            AbortTransport();

        disconnectRequested.TrySetResult();
        WakeState();
        return true;

        static async Task ObserveAsync(Task task, ILogger log)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "A ConnectionAborted callback threw"); }
        }
    }

    /// <summary>Runs the connection from handshake to the last disconnect hook. Never throws.</summary>
    internal async Task RunAsync(
        IReadOnlyList<IIonStreamLifecycle> hooks,
        IServiceStreamExecutorRouter router,
        string methodName,
        ReadOnlyMemory<byte> args)
    {
        var connected = new List<IIonStreamLifecycle>(hooks.Count);
        Task? heartbeat = null;

        manager.Add(this);

        // Reading starts before the connect hooks, so a client that leaves while they run is noticed
        // — and ConnectionAborted fires for the hook still working on its behalf.
        StartReceiving(firstLink);

        try
        {
            foreach (var hook in hooks)
            {
                try
                {
                    await hook.OnConnectedAsync(this).ConfigureAwait(false);
                    connected.Add(hook);
                }
                catch (Exception ex)
                {
                    RequestDisconnect(IonDisconnectReason.Rejected, ex);
                    break;
                }

                if (Volatile.Read(ref disconnect) is not null)
                    break;
            }

            if (Volatile.Read(ref disconnect) is null)
            {
                Volatile.Write(ref state, (int)IonStreamState.Connected);

                if (resumable)
                    resumeToken = resumeRegistry!.Register(this);

                // READY goes out before the send loop exists, so nothing queued during the connect
                // hooks — a push to the new member of a group, say — can overtake it.
                var ready = IonStreamProtocol.ReadyFrame(ConnectionId, options.KeepAliveInterval, options.ClientTimeout,
                    resumeToken, options.ResumeWindow, InputBudget);
                if (!await TrySendDirectAsync(firstLink, ready).ConfigureAwait(false))
                {
                    RequestDisconnect(IonDisconnectReason.TransportLost, new IOException("READY could not be sent."));
                }
                else
                {
                    // The client's silence clock starts now: a slow connect hook is not the client's silence.
                    Volatile.Write(ref firstLink.LastReceivedAt, Environment.TickCount64);
                    readySent = true;
                    StartSending(firstLink, replayFirst: false);
                    _ = SignalStoppedAsync(firstLink);
                    heartbeat = HeartbeatAsync();
                    pump = Task.Run(() => PumpAsync(router, methodName, args));
                }
            }

            await disconnectRequested.Task.ConfigureAwait(false);
            Volatile.Write(ref state, (int)IonStreamState.Closing);
            input?.Writer.TryComplete();

            await CloseTransportAsync(Volatile.Read(ref disconnect)!).ConfigureAwait(false);
            await StopSendingAsync().ConfigureAwait(false);

            if (pump is not null)
                await AwaitStreamStopAsync(pump).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Every path above handles its own failures; reaching here is a bug in this class.
            log.LogError(ex, "Stream connection {ConnectionId} failed unexpectedly", ConnectionId);
            RequestDisconnect(IonDisconnectReason.Faulted, ex);
            AbortTransport();
        }
        finally
        {
            Link? current;
            Link newest;
            lock (gate)
            {
                ending = true;
                current = link;
                newest = lastLink;
                link = null;
            }

            // A resuming transport whose loops never started belongs to its own handler, which
            // sees the session is over and refuses it properly.
            if (current is not null && Started(current) && !current.Transport.IsClosed)
                AbortLink(current);
            if (Started(newest) && !newest.Transport.IsClosed)
                AbortLink(newest);
            if (!firstLink.Transport.IsClosed)
                AbortLink(firstLink);

            if (resumeToken is not null)
                resumeRegistry!.Remove(resumeToken, this);

            try { lifetimeCts.Cancel(); }
            catch (AggregateException) { }

            outbound.Writer.TryComplete();

            // Every link's loops, the first one's (which this method started) and the newest's —
            // each link starts only after the one before it stopped, so that covers them all.
            await QuietlyAsync(firstLink.Receive).ConfigureAwait(false);
            if (firstLink.Send is not null)
                await QuietlyAsync(firstLink.Send).ConfigureAwait(false);
            await StopFeederAsync(firstLink).ConfigureAwait(false);
            firstLink.Stopped.TrySetResult();
            if (!ReferenceEquals(newest, firstLink))
                await newest.Stopped.Task.ConfigureAwait(false);

            if (heartbeat is not null)
                await QuietlyAsync(heartbeat).ConfigureAwait(false);

            ReleaseQueued();
            replay?.Clear();

            input?.Writer.TryComplete();
            manager.Remove(this);
            Volatile.Write(ref state, (int)IonStreamState.Closed);

            var info = Volatile.Read(ref disconnect)!;
            for (var i = connected.Count - 1; i >= 0; i--)
            {
                try
                {
                    await connected[i].OnDisconnectedAsync(this, info).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "OnDisconnectedAsync of {Hook} failed for connection {ConnectionId}",
                        connected[i].GetType().Name, ConnectionId);
                }
            }

            await firstLink.HttpAborted.DisposeAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
    }

    // ── links ─────────────────────────────────────────────────────────────────────────────────────

    private void StartReceiving(Link l)
    {
        // The host noticing the connection is gone (a reset, a server-side abort) is one more way
        // for the transport to die. After a graceful close it fires too, and is then a no-op.
        l.HttpAborted = l.Http.RequestAborted.Register(static s =>
        {
            var (connection, lost) = ((IonStreamConnection, Link))s!;
            connection.LinkLost(lost, IonDisconnectReason.TransportLost,
                new ConnectionAbortedException("The HTTP connection under the stream was aborted."));
        }, (this, l));

        if (replay is not null && input is not null)
        {
            l.Overflow = Channel.CreateUnbounded<InputItem>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            l.Feeder = FeedLoopAsync(l);
        }

        l.Receive = ReceiveLoopAsync(l);
    }

    private void StartSending(Link l, bool replayFirst) => l.Send = SendLoopAsync(l, replayFirst);

    /// <summary>Whether this session runs <paramref name="l"/>'s loops — the first link always does.</summary>
    private bool Started(Link l) => ReferenceEquals(l, firstLink) || l.Send is not null;

    /// <summary>Completes <see cref="Link.Stopped"/> of the first link once both its loops are done — a resume that takes over waits for it.</summary>
    private async Task SignalStoppedAsync(Link l)
    {
        await QuietlyAsync(l.Receive).ConfigureAwait(false);
        if (l.Send is not null)
            await QuietlyAsync(l.Send).ConfigureAwait(false);
        await StopFeederAsync(l).ConfigureAwait(false);
        l.Stopped.TrySetResult();
    }

    /// <summary>A link's feeder counts the client's frames too: it must have stopped before another link counts on.</summary>
    private async Task StopFeederAsync(Link l)
    {
        if (l.Feeder is null)
            return;

        if (!l.Cts.IsCancellationRequested)
            l.Overflow!.Writer.TryComplete();
        await QuietlyAsync(l.Feeder).ConfigureAwait(false);
    }

    /// <summary>
    /// After the goodbye: no link may attach any more, the last one's send loop is done, and a frame
    /// the pump lent and nobody sent is handed back as failed — so the stream method stops at once
    /// instead of waiting out its stop timeout on a send that will never happen.
    /// </summary>
    private async Task StopSendingAsync()
    {
        Link newest;
        lock (gate)
        {
            ending = true;
            newest = lastLink;
        }

        outbound.Writer.TryComplete();
        if (Started(newest) && !newest.Transport.IsClosed)
            AbortLink(newest);

        if (readySent)
            await newest.Stopped.Task.ConfigureAwait(false);
        else if (newest.Send is not null)
            await QuietlyAsync(newest.Send).ConfigureAwait(false);

        ReleaseQueued();
    }

    /// <summary>
    /// A link died — its receive or send failed, it went silent, its HTTP request was aborted, or
    /// its transport closed without a goodbye. A resumable session lets it go and waits for the
    /// client to come back; any other connection ends here.
    /// </summary>
    private void LinkLost(Link l, IonDisconnectReason reason, Exception? exception)
    {
        bool detached, wasCurrent;
        lock (gate)
        {
            if (l.Lost)
                return;
            l.Lost = true;

            wasCurrent = ReferenceEquals(link, l);
            detached = wasCurrent && CanDetach();
            if (detached)
            {
                link = null;
                lastTransportName = l.Transport.Name;
                lostAt = Math.Max(1, Environment.TickCount64);
                lostReason = reason;
                lostException = exception;
                Interlocked.CompareExchange(ref state, (int)IonStreamState.Reconnecting, (int)IonStreamState.Connected);
            }
        }

        if (detached)
        {
            log.LogInformation(exception,
                "Stream connection {ConnectionId} lost its {Transport} transport ({Reason}); keeping the session for {Window}",
                ConnectionId, l.Transport.Name, reason, options.ResumeWindow);
            AbortLink(l);
            WakeState();
            return;
        }

        if (wasCurrent)
            RequestDisconnect(reason, exception);

        AbortLink(l);
    }

    /// <summary>Whether losing the current link leaves the session waiting rather than ending it. Under <see cref="gate"/>.</summary>
    private bool CanDetach()
    {
        if (!resumable || !readySent || ending || expired || GoodbyeAcknowledged)
            return false;

        var info = Volatile.Read(ref disconnect);
        return info is null || GoodbyeNeedsDelivery(info.Reason);
    }

    /// <summary>Whether a client may attach a transport now. Under <see cref="gate"/>.</summary>
    private bool CanAttach()
    {
        if (resumeToken is null || !CanDetach())
            return false;

        // Past the window the heartbeat is about to end the session; do not race it.
        return link is not null || lostAt == 0 || Environment.TickCount64 - lostAt <= Millis(options.ResumeWindow);
    }

    /// <summary>
    /// The client has the goodbye. Read from the counters, not from <see cref="goodbyeDelivered"/>
    /// alone: the client acknowledges the goodbye and closes its side at once, and the receive loop
    /// reads the close before the goodbye's waiter has woken to set the flag — a close that must be
    /// the end, not a lost transport to wait out a resume window for.
    /// </summary>
    private bool GoodbyeAcknowledged
    {
        get
        {
            if (goodbyeDelivered)
                return true;
            var seq = Volatile.Read(ref goodbyeSeq);
            return seq != 0 && Volatile.Read(ref peerAcked) >= seq;
        }
    }

    /// <summary>The goodbyes a resumable session keeps its promise to deliver, across transports.</summary>
    private static bool GoodbyeNeedsDelivery(IonDisconnectReason reason)
        => reason is IonDisconnectReason.Completed or IonDisconnectReason.ServerClosed or IonDisconnectReason.Faulted;

    /// <summary>
    /// Attaches a client's new transport to this session, and runs it until it is lost or the session
    /// ends. The client's frames up to <paramref name="clientReceived"/> are not sent again.
    /// </summary>
    internal async Task<IonResumeOutcome> ResumeAsync(IonStreamTransport transport, HttpContext http, long clientReceived)
    {
        var fresh = new Link(transport, http);
        Link before;
        Link? takenOver;

        lock (gate)
        {
            if (!CanAttach())
                return IonResumeOutcome.NotResumable;

            takenOver = link;
            before = lastLink;
            link = fresh;
            lastLink = fresh;
        }

        // The client is back while the old transport still looks alive — a network switch, say, that
        // the client noticed first. The old one is done for; the new one takes over.
        if (takenOver is not null)
        {
            lock (gate)
                takenOver.Lost = true;
            AbortLink(takenOver);
        }

        // Whatever ran before must have stopped: its loops touch the replay buffer and the counters.
        await before.Stopped.Task.ConfigureAwait(false);

        var outcome = IonResumeOutcome.Resumed;
        lock (gate)
        {
            if (!ReferenceEquals(link, fresh) || !CanAttach())
            {
                // Another transport took over while this one waited, or the session ended.
                fresh.Lost = true;
                outcome = ReferenceEquals(link, fresh) ? IonResumeOutcome.NotResumable : IonResumeOutcome.Superseded;
                if (ReferenceEquals(link, fresh))
                    link = null;
            }
            else if (clientReceived < Volatile.Read(ref peerAcked) || clientReceived > Volatile.Read(ref sentReliable))
            {
                fresh.Lost = true;
                link = null;
                outcome = IonResumeOutcome.Violation;
            }
        }

        if (outcome != IonResumeOutcome.Resumed)
        {
            if (outcome == IonResumeOutcome.Violation)
                RequestDisconnect(IonDisconnectReason.ProtocolViolation, new IonStreamProtocolException(
                    $"RESUME claims {clientReceived} frames received; the server sent {Volatile.Read(ref sentReliable)} " +
                    $"and {Volatile.Read(ref peerAcked)} were acknowledged."));
            fresh.Stopped.TrySetResult();
            return outcome;
        }

        // No loop runs now: the replay buffer and the counters are this method's until the new
        // link's loops start.
        Volatile.Write(ref peerAcked, clientReceived);
        replay!.TrimTo(clientReceived);
        var replayed = replay.Count;
        Volatile.Write(ref fresh.LastReceivedAt, Environment.TickCount64);

        if (!await TrySendDirectAsync(fresh, IonStreamProtocol.ResumedFrame(Volatile.Read(ref inputReceived))).ConfigureAwait(false))
        {
            LinkLost(fresh, IonDisconnectReason.TransportLost, new IOException("RESUMED could not be sent."));
            fresh.Stopped.TrySetResult();
            return IonResumeOutcome.Lost;
        }

        bool superseded;
        lock (gate)
        {
            superseded = fresh.Lost;
            if (!superseded)
            {
                lostAt = 0;
                lostException = null;
                lastTransportName = transport.Name;
                Interlocked.CompareExchange(ref state, (int)IonStreamState.Connected, (int)IonStreamState.Reconnecting);
                Resumes++;
            }
        }

        if (superseded)
        {
            // Yet another transport took over while RESUMED was on its way; it waits for this one.
            fresh.Stopped.TrySetResult();
            return IonResumeOutcome.Superseded;
        }

        log.LogInformation("Stream connection {ConnectionId} resumed on {Transport}, replaying {Frames} frame(s)",
            ConnectionId, transport.Name, replayed);

        StartReceiving(fresh);
        StartSending(fresh, replayFirst: true);
        WakeState();

        await QuietlyAsync(fresh.Receive).ConfigureAwait(false);
        await QuietlyAsync(fresh.Send!).ConfigureAwait(false);
        await StopFeederAsync(fresh).ConfigureAwait(false);
        await fresh.HttpAborted.DisposeAsync().ConfigureAwait(false);
        fresh.Stopped.TrySetResult();
        return IonResumeOutcome.Resumed;
    }

    /// <summary>Kills one transport and wakes whatever of the session waits on it.</summary>
    private void AbortLink(Link l)
    {
        try { l.Cts.Cancel(); }
        catch (AggregateException) { }

        try { l.Transport.Abort(); }
        catch (Exception) { }

        // An idle send loop waits on the queue, not on the token: a stop item wakes it.
        outbound.Writer.TryWrite(new Outgoing(OutgoingKind.Stop, default));
        WakeSend();
    }

    /// <summary>The session is over: kill whatever transport it has and stop the queue.</summary>
    private void AbortTransport()
    {
        var current = Volatile.Read(ref link);
        if (current is not null)
            AbortLink(current);
        else if (!resumable)
            AbortLink(firstLink);

        // Wakes a pump blocked on a full queue and stops the send loop.
        outbound.Writer.TryComplete();
    }

    // ── the stream method ─────────────────────────────────────────────────────────────────────────

    private async Task PumpAsync(IServiceStreamExecutorRouter router, string methodName, ReadOnlyMemory<byte> args)
    {
        try
        {
            var reader = new CborReader(args);
            var inputStream = input?.Reader.ReadAllAsync(ConnectionAborted);

            // The executor decodes the arguments when it is invoked, before the first frame; a
            // failure there is the client's malformed call, not the stream method failing.
            IAsyncEnumerable<Memory<byte>> frames;
            try
            {
                frames = router.StreamRouteFramesAsync(methodName, reader, inputStream, ConnectionAborted);
            }
            catch (Exception ex) when (ex is IonDecodeException or CborContentException or FormatException or OverflowException)
            {
                RequestDisconnect(IonDisconnectReason.ProtocolViolation,
                    new IonStreamProtocolException($"The arguments of {Interface.Name}.{Method.Name} could not be decoded: {ex.Message}"));
                return;
            }

            // The executor hands over finished frames from a buffer it reuses; the send completes
            // before the executor is asked for the next one, so nothing is copied.
            await foreach (var frame in frames.WithCancellation(ConnectionAborted).ConfigureAwait(false))
                await SendBorrowedAsync(frame).ConfigureAwait(false);

            RequestDisconnect(IonDisconnectReason.Completed);
        }
        catch (IonStreamSendException)
        {
            // The frame could not be sent because the connection is gone; that is already recorded.
        }
        catch (Exception ex) when (Volatile.Read(ref disconnect) is not null)
        {
            // The connection was already ending. Whatever the method threw on its way out —
            // usually the cancellation it was asked for — is not why the connection ended.
            if (ex is not OperationCanceledException)
                log.LogDebug(ex, "Stream method threw after connection {ConnectionId} began closing", ConnectionId);
        }
        catch (Exception ex)
        {
            RequestDisconnect(IonDisconnectReason.Faulted, ex);
        }
    }

    /// <summary>Queues a frame the pump only lends, and returns once the send loop is done with it.</summary>
    /// <exception cref="IonStreamSendException">The frame was not taken; the connection is ending.</exception>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask SendBorrowedAsync(ReadOnlyMemory<byte> frame)
    {
        pumpAck.Reset();

        try
        {
            await outbound.Writer.WriteAsync(new Outgoing(OutgoingKind.Borrowed, frame)).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            throw new IonStreamSendException(ex);
        }

        await pumpAck.AsValueTask().ConfigureAwait(false);
    }

    // ── sending ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The only code that writes to a link's transport while it runs.</summary>
    /// <param name="replayFirst">A resumed link: resend what the client has not acknowledged before anything new.</param>
    private async Task SendLoopAsync(Link l, bool replayFirst)
    {
        try
        {
            if (l.Lost)
                return;

            if (replayFirst && replay is not null)
            {
                for (var i = 0; i < replay.Count; i++)
                {
                    if (ackWanted)
                        await WriteAckAsync(l).ConfigureAwait(false);
                    await WriteAsync(l, replay[i]).ConfigureAwait(false);
                }
            }

            while (true)
            {
                if (ackWanted)
                    await WriteAckAsync(l).ConfigureAwait(false);

                Outgoing item;
                if (pending is { } held)
                {
                    item = held;
                }
                else if (outbound.Reader.TryRead(out item))
                {
                    pending = item;
                }
                else
                {
                    // No token: waits then use the channel's pooled operations. A stop item, or
                    // completing the queue, is what wakes a link that is going away.
                    if (!await outbound.Reader.WaitToReadAsync().ConfigureAwait(false))
                        return;
                    if (l.Lost)
                        return;
                    continue;
                }

                switch (item.Kind)
                {
                    case OutgoingKind.Borrowed:
                        if (goodbyeAppended)
                        {
                            pending = null;
                            pumpAck.Fail(null);
                            break;
                        }

                        if (replay is not null)
                        {
                            // Kept before it is written, so the pump may go on at once: the frame
                            // is safe in the replay buffer, and written from there.
                            await ReserveAsync(l, item.Frame.Length).ConfigureAwait(false);
                            var kept = Keep(item.Frame, copy: true);
                            pending = null;
                            pumpAck.Succeed();
                            await WriteAsync(l, kept).ConfigureAwait(false);
                            break;
                        }

                        try
                        {
                            await WriteAsync(l, item.Frame).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            pending = null;
                            pumpAck.Fail(ex);
                            throw;
                        }

                        pending = null;
                        pumpAck.Succeed();
                        break;

                    case OutgoingKind.Push:
                        if (goodbyeAppended || discardPushes)
                        {
                            pending = null;
                            break;
                        }

                        if (replay is not null)
                        {
                            await ReserveAsync(l, item.Frame.Length).ConfigureAwait(false);
                            var kept = Keep(item.Frame, copy: false);
                            pending = null;
                            await WriteAsync(l, kept).ConfigureAwait(false);
                            break;
                        }

                        pending = null;
                        await WriteAsync(l, item.Frame).ConfigureAwait(false);
                        break;

                    case OutgoingKind.Ping:
                        pending = null;
                        if (!goodbyeAppended)
                            await WriteAsync(l, item.Frame).ConfigureAwait(false);
                        break;

                    case OutgoingKind.Ack:
                        pending = null;
                        Volatile.Write(ref ackQueued, 0);
                        if (ackWanted)
                            await WriteAckAsync(l).ConfigureAwait(false);
                        break;

                    case OutgoingKind.Goodbye:
                        if (replay is not null)
                        {
                            await ReserveAsync(l, item.Frame.Length).ConfigureAwait(false);
                            var kept = Keep(item.Frame, copy: false);
                            Volatile.Write(ref goodbyeSeq, Volatile.Read(ref sentReliable));
                            goodbyeAppended = true;
                            pending = null;
                            WakeState();
                            await WriteAsync(l, kept).ConfigureAwait(false);
                            break;
                        }

                        pending = null;
                        await WriteAsync(l, item.Frame).ConfigureAwait(false);
                        goodbyeAppended = true;
                        break;

                    case OutgoingKind.CloseOutput:
                        pending = null;
                        await l.Transport.CloseOutputAsync(item.CloseStatus, item.CloseDescription, l.Cts.Token)
                            .ConfigureAwait(false);
                        return;

                    case OutgoingKind.Stop:
                        pending = null;
                        if (l.Lost)
                            return;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !l.Cts.IsCancellationRequested)
        {
            LinkLost(l, IonDisconnectReason.TransportLost, ex);
        }
        catch (OperationCanceledException)
        {
            // Aborted on purpose; the abort recorded why.
        }
        finally
        {
            if (!resumable || !readySent)
            {
                outbound.Writer.TryComplete();
                ReleaseQueued();
            }
        }
    }

    /// <summary>Fails the pump's borrowed frame, wherever it is waiting, so the pump can stop.</summary>
    private void ReleaseQueued()
    {
        if (pending is { Kind: OutgoingKind.Borrowed })
            pumpAck.Fail(null);
        pending = null;

        // A borrowed frame still queued has a pump waiting on it.
        while (outbound.Reader.TryRead(out var left))
            if (left.Kind == OutgoingKind.Borrowed)
                pumpAck.Fail(null);
    }

    /// <summary>Numbers a reliable frame and keeps it until the client acknowledges it; returns the kept bytes.</summary>
    private ReadOnlyMemory<byte> Keep(ReadOnlyMemory<byte> frame, bool copy)
    {
        var kept = replay!.Append(frame, copy);
        Volatile.Write(ref sentReliable, replay.NextSeq - 1);
        return kept;
    }

    /// <summary>
    /// Waits until the replay buffer has room for a frame of <paramref name="size"/> bytes — the
    /// client's acknowledgements make it — while still answering the client's frames with ACKs, so
    /// two sides waiting on each other's acknowledgements can never deadlock.
    /// </summary>
    private async ValueTask ReserveAsync(Link l, int size)
    {
        var budget = Math.Max(options.ResumeBufferSize, 2 * IonStreamProtocol.AckEveryBytes);
        while (true)
        {
            replay!.TrimTo(Volatile.Read(ref peerAcked));
            if (replay.Count == 0 ||
                (replay.Bytes + size <= budget && replay.Count < IonStreamProtocol.MaxUnacknowledgedFrames))
                return;

            if (ackWanted)
            {
                await WriteAckAsync(l).ConfigureAwait(false);
                continue;
            }

            var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref sendWake, wake);

            // Checked again after publishing the wake-up: an ACK that landed in between is not lost.
            if (ackWanted || replay.FirstSeq <= Volatile.Read(ref peerAcked))
                continue;

            await wake.Task.WaitAsync(l.Cts.Token).ConfigureAwait(false);
        }
    }

    private void WakeSend() => Interlocked.Exchange(ref sendWake, null)?.TrySetResult();

    private void WakeState() => Interlocked.Exchange(ref stateWake, null)?.TrySetResult();

    /// <summary>Asks the send loop for an ACK of everything received so far, without waiting in line.</summary>
    private void RequestAck()
    {
        ackWanted = true;
        if (Interlocked.Exchange(ref ackQueued, 1) == 0 && !outbound.Writer.TryWrite(new Outgoing(OutgoingKind.Ack, default)))
            Volatile.Write(ref ackQueued, 0);
        WakeSend();
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask WriteAckAsync(Link l)
    {
        ackWanted = false;
        var bytes = Volatile.Read(ref inputReceivedBytes);
        var received = Volatile.Read(ref inputReceived);
        var length = IonStreamProtocol.WriteAck(l.AckScratch, received);
        await WriteAsync(l, l.AckScratch.AsMemory(0, length)).ConfigureAwait(false);
        Volatile.Write(ref inputAckedSent, received);
        Volatile.Write(ref inputAckedBytes, bytes);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask WriteAsync(Link l, ReadOnlyMemory<byte> frame)
    {
        await l.Transport.SendAsync(frame, l.Cts.Token).ConfigureAwait(false);
        Volatile.Write(ref l.LastSentAt, Environment.TickCount64);
    }

    /// <summary>Writes a frame while no send loop runs on <paramref name="l"/> — READY, RESUMED, or the goodbye of a connection that never started one.</summary>
    private async ValueTask<bool> TrySendDirectAsync(Link l, ReadOnlyMemory<byte> frame)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(l.Cts.Token);
        var timeout = Bounded(options.CloseTimeout);
        if (timeout != Timeout.InfiniteTimeSpan)
            bound.CancelAfter(timeout);

        try
        {
            await l.Transport.SendAsync(frame, bound.Token).ConfigureAwait(false);
            Volatile.Write(ref l.LastSentAt, Environment.TickCount64);
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Direct send on connection {ConnectionId} failed", ConnectionId);
            return false;
        }
    }

    // ── receiving ─────────────────────────────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(Link l)
    {
        try
        {
            while (true)
            {
                var received = await l.Transport
                    .ReceiveAsync(options.MaxReceiveMessageSize, l.Cts.Token)
                    .ConfigureAwait(false);

                Volatile.Write(ref l.LastReceivedAt, Environment.TickCount64);

                if (received.IsClose)
                {
                    clientCloseStatus = l.Transport.PeerCloseStatus;

                    // On a resumable session only an Ion goodbye means the client left; a transport
                    // that merely closed — a proxy restarting, a load balancer rebalancing — is lost.
                    // One another transport has taken over from says nothing about the session.
                    bool current, lost;
                    lock (gate)
                    {
                        current = ReferenceEquals(link, l) || (link is null && !l.Lost);
                        lost = current && ReferenceEquals(link, l) && CanDetach();
                    }

                    if (!current)
                        return;

                    if (lost)
                    {
                        LinkLost(l, IonDisconnectReason.TransportLost, new WebSocketException(
                            $"The transport closed without an Ion goodbye ({l.Transport.PeerCloseStatus} {l.Transport.PeerCloseDescription})."));
                        return;
                    }

                    RequestDisconnect(IonDisconnectReason.ClientClosed, null, l.Transport.PeerCloseDescription);
                    return;
                }

                var message = received.Payload;
                if (!received.IsBinary || message.IsEmpty)
                {
                    Violation("Expected a non-empty binary frame.");
                    await DiscardUntilClosedAsync(l).ConfigureAwait(false);
                    return;
                }

                // Borrowed until the next receive; only DATA outlives this iteration, and it is copied.
                var payload = message[1..];
                var opcode = message.Span[0];

                switch (opcode)
                {
                    case IonStreamProtocol.OpData:
                        if (input is null)
                        {
                            Violation($"{Interface.Name}.{Method.Name} takes no input stream, but the client sent DATA.");
                            await DiscardUntilClosedAsync(l).ConfigureAwait(false);
                            return;
                        }

                        if (payload.IsEmpty)
                        {
                            Violation("An empty DATA frame; the end of the input stream is END.");
                            await DiscardUntilClosedAsync(l).ConfigureAwait(false);
                            return;
                        }

                        if (inputEnded)
                        {
                            Violation("DATA after the end of the input stream.");
                            await DiscardUntilClosedAsync(l).ConfigureAwait(false);
                            return;
                        }

                        // Its own exact array, not a pooled one: the decoded item may keep it (a
                        // `bytes` input decodes to an IonBytes over these very bytes).
                        ReadOnlyMemory<byte> item = payload.ToArray();

                        if (l.Overflow is not null)
                        {
                            if (!Feed(l, new InputItem(InputKind.Data, item, null, message.Length)))
                            {
                                await DiscardUntilClosedAsync(l).ConfigureAwait(false);
                                return;
                            }

                            break;
                        }

                        if (!input.Writer.TryWrite(item))
                        {
                            // The stream method is behind. Stop reading until it catches up — the
                            // heartbeat knows not to mistake the silence that causes for a dead client.
                            l.ReceiveParked = true;
                            try
                            {
                                await input.Writer.WriteAsync(item, l.Cts.Token).ConfigureAwait(false);
                            }
                            catch (ChannelClosedException)
                            {
                                // The stream method no longer reads its input; the item has nowhere to go.
                            }
                            finally
                            {
                                l.ReceiveParked = false;
                                Volatile.Write(ref l.LastReceivedAt, Environment.TickCount64);
                            }
                        }

                        // Counted once handed over, not when read: an item the transport died holding
                        // is not counted, so the client sends it again.
                        CountReceived(message.Length);
                        break;

                    case IonStreamProtocol.OpEnd:
                        inputEnded = true;
                        if (l.Overflow is not null)
                        {
                            Feed(l, new InputItem(InputKind.End, default, null, message.Length));
                            break;
                        }

                        input?.Writer.TryComplete();
                        CountReceived(message.Length);
                        break;

                    case IonStreamProtocol.OpError:
                        inputEnded = true;
                        var inputError = new IonStreamInputException(ReadClientError(payload));
                        if (l.Overflow is not null)
                        {
                            Feed(l, new InputItem(InputKind.Error, default, inputError, message.Length));
                            break;
                        }

                        input?.Writer.TryComplete(inputError);
                        CountReceived(message.Length);
                        break;

                    case IonStreamProtocol.OpPing:
                        break;

                    case IonStreamProtocol.OpClose:
                        inputEnded = true;
                        input?.Writer.TryComplete();
                        CountReceived(message.Length);
                        string? reason = null;
                        try { reason = IonStreamProtocol.ReadClose(payload).Reason; }
                        catch (CborContentException) { }
                        RequestDisconnect(IonDisconnectReason.ClientClosed, null, reason);
                        // Keep reading: the transport's own close follows the goodbye.
                        break;

                    case IonStreamProtocol.OpAck:
                        if (!ReceiveAck(payload))
                        {
                            await DiscardUntilClosedAsync(l).ConfigureAwait(false);
                            return;
                        }

                        break;

                    default:
                        Violation($"Unknown opcode 0x{opcode:x2}.");
                        await DiscardUntilClosedAsync(l).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (IonStreamMessageTooLargeException ex)
        {
            RequestDisconnect(IonDisconnectReason.ProtocolViolation, ex);
            await DiscardUntilClosedAsync(l).ConfigureAwait(false);
        }
        catch (IonStreamProtocolException ex)
        {
            RequestDisconnect(IonDisconnectReason.ProtocolViolation, ex);
            await DiscardUntilClosedAsync(l).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (l.Cts.IsCancellationRequested)
        {
            // The transport was aborted on purpose; the abort recorded why.
        }
        catch (Exception ex)
        {
            LinkLost(l, IonDisconnectReason.TransportLost, ex);
            if (Volatile.Read(ref disconnect) is not null)
                log.LogDebug(ex, "Receive loop of connection {ConnectionId} ended after it began closing", ConnectionId);
        }
        finally
        {
            // A plain connection's input ends with its transport. A resumable session's outlives the
            // transport — the client resends what was lost — and ends with the session instead.
            if (!resumable || !readySent)
                input?.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Hands one input frame of a resumable session to the stream method — at once when nothing is
    /// waiting ahead of it and the method has room, through the link's overflow otherwise. The
    /// receive loop never waits for the method.
    /// </summary>
    /// <remarks>
    /// <para>It must not: a client acknowledges the server's frames on the same transport, behind its
    /// input. A stream method that waits to send (the replay budget spent, waiting for an ACK) and so
    /// does not read its input, with a receive loop that waits for the method to read before it reads
    /// on, never sees the ACK it waits for — a duplex stream under load would deadlock.</para>
    ///
    /// <para>What waits in the overflow is not counted, so not acknowledged, so the client keeps it
    /// for replay: the overflow is the link's alone and dies with it. And it is bounded: the client
    /// may have no more unacknowledged input in flight than the budget READY announced, which is
    /// enforced here.</para>
    /// </remarks>
    /// <returns>False when the client overran the budget; a violation is recorded.</returns>
    private bool Feed(Link l, InputItem item)
    {
        var overflow = l.Overflow!;
        if (Volatile.Read(ref l.OverflowCount) == 0 && !l.FeederBusy)
        {
            switch (item.Kind)
            {
                case InputKind.Data when input!.Writer.TryWrite(item.Payload):
                    CountReceived(item.Bytes);
                    return true;
                case InputKind.End:
                    input?.Writer.TryComplete();
                    CountReceived(item.Bytes);
                    return true;
                case InputKind.Error:
                    input?.Writer.TryComplete(item.Error);
                    CountReceived(item.Bytes);
                    return true;
            }
        }

        var held = Interlocked.Add(ref l.OverflowBytes, item.Bytes);
        if (held > InputBudget + options.MaxReceiveMessageSize)
        {
            Violation($"The client has {held} bytes of unacknowledged input in flight; the budget is {InputBudget}.");
            return false;
        }

        Interlocked.Increment(ref l.OverflowCount);
        overflow.Writer.TryWrite(item);
        return true;
    }

    /// <summary>Moves a link's overflow into the stream method's input, in order, as the method makes room.</summary>
    private async Task FeedLoopAsync(Link l)
    {
        var overflow = l.Overflow!;
        try
        {
            while (await overflow.Reader.WaitToReadAsync(l.Cts.Token).ConfigureAwait(false))
            {
                // Busy before the first item leaves the queue, and each counted out only once it is
                // with the method — so the receive loop never finds the overflow empty and idle while
                // an item is still on its way, and overtakes it.
                l.FeederBusy = true;
                try
                {
                    while (overflow.Reader.TryRead(out var item))
                    {
                        switch (item.Kind)
                        {
                            case InputKind.Data:
                                try
                                {
                                    await input!.Writer.WriteAsync(item.Payload, l.Cts.Token).ConfigureAwait(false);
                                }
                                catch (ChannelClosedException)
                                {
                                    // The stream method no longer reads its input; the item has nowhere to go.
                                }

                                break;
                            case InputKind.End:
                                input?.Writer.TryComplete();
                                break;
                            case InputKind.Error:
                                input?.Writer.TryComplete(item.Error);
                                break;
                        }

                        Interlocked.Add(ref l.OverflowBytes, -item.Bytes);
                        Interlocked.Decrement(ref l.OverflowCount);
                        CountReceived(item.Bytes);
                    }
                }
                finally
                {
                    l.FeederBusy = false;
                }
            }
        }
        catch (OperationCanceledException) when (l.Cts.IsCancellationRequested)
        {
            // The link is gone, and what it held with it: uncounted, so the client sends it again.
        }
    }

    /// <summary>The unacknowledged input a client may have in flight, announced in READY.</summary>
    private int InputBudget => Math.Max(options.ResumeBufferSize, 2 * IonStreamProtocol.AckEveryBytes);

    /// <summary>One more of the client's reliable frames taken; acknowledged in batches.</summary>
    private void CountReceived(int bytes)
    {
        if (replay is null)
            return;

        // The receive loop and a link's feeder both count, never at once — but atomically all the same.
        var count = Interlocked.Increment(ref inputReceived);
        var total = Interlocked.Add(ref inputReceivedBytes, bytes);

        if (count - Volatile.Read(ref inputAckedSent) >= IonStreamProtocol.AckEveryFrames ||
            total - Volatile.Read(ref inputAckedBytes) >= IonStreamProtocol.AckEveryBytes)
            RequestAck();
    }

    /// <summary>The client acknowledged our frames up to n. False (and a violation recorded) when n makes no sense.</summary>
    private bool ReceiveAck(ReadOnlyMemory<byte> payload)
    {
        if (replay is null)
        {
            Violation("ACK on a stream that is not resumable.");
            return false;
        }

        long acked;
        try
        {
            acked = IonStreamProtocol.ReadAck(payload);
        }
        catch (CborContentException ex)
        {
            Violation($"A malformed ACK: {ex.Message}");
            return false;
        }

        var previous = Volatile.Read(ref peerAcked);
        var sent = Volatile.Read(ref sentReliable);
        if (acked < previous || acked > sent)
        {
            Violation($"ACK {acked} is outside what the server sent ({sent}) and the client acknowledged before ({previous}).");
            return false;
        }

        Volatile.Write(ref peerAcked, acked);
        WakeSend();
        if (Volatile.Read(ref goodbyeSeq) != 0)
            WakeState();
        return true;
    }

    private void Violation(string message)
        => RequestDisconnect(IonDisconnectReason.ProtocolViolation, new IonStreamProtocolException(message));

    /// <summary>
    /// After a protocol violation: keep the read side going, discarding everything, until the
    /// client ends its side — the goodbye's close handshake needs someone to read the client's
    /// close. Stopping here instead would leave it unread, the handshake would never complete, and
    /// the abort that follows can overtake the ERROR frame on its way out. The goodbye's close
    /// timeout bounds how long this runs.
    /// </summary>
    private static async Task DiscardUntilClosedAsync(Link l)
    {
        try
        {
            await l.Transport.DiscardUntilClosedAsync(l.Cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Aborted, or the client went away without a close: either way there is nothing to read.
        }
    }

    private static IonProtocolError ReadClientError(ReadOnlyMemory<byte> payload)
    {
        try
        {
            return payload.IsEmpty
                ? new IonProtocolError("INPUT_FAULTED", "The client's input stream failed.")
                : IonStreamProtocol.ReadError(payload);
        }
        catch (Exception)
        {
            return new IonProtocolError("INPUT_FAULTED", "The client's input stream failed with an unreadable error.");
        }
    }

    // ── heartbeat ─────────────────────────────────────────────────────────────────────────────────

    private async Task HeartbeatAsync()
    {
        var keepAlive = Millis(options.KeepAliveInterval);
        var timeout = Millis(options.ClientTimeout);
        var window = Millis(options.ResumeWindow);
        var smallest = keepAlive > 0 && timeout > 0 ? Math.Min(keepAlive, timeout) : Math.Max(keepAlive, timeout);
        if (resumable)
            smallest = smallest > 0 ? Math.Min(smallest, window) : window;
        if (smallest <= 0)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(smallest / 4, 5, 1000)));
        try
        {
            // A plain connection's heartbeat ends with the connection; a resumable session's runs on
            // through its goodbye, which may have to wait out a lost transport.
            var token = resumable ? lifetimeCts.Token : ConnectionAborted;
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var now = Environment.TickCount64;
                var current = Volatile.Read(ref link);

                // Ending without a goodbye to deliver: the close timeout bounds what is left.
                if (Volatile.Read(ref disconnect) is { } closing && !GoodbyeNeedsDelivery(closing.Reason))
                    continue;

                if (current is null)
                {
                    CheckResumeWindow(now, window);
                    continue;
                }

                // Silence is the only death sentence. A client that reads slowly but keeps pinging is
                // alive — its sends stall, the stream method waits, pushes queue until
                // OutboundQueueCapacity decides otherwise — exactly as a SignalR client is not dropped
                // for being slow. A dead one stops pinging, and that is what this catches, whether
                // or not a send is stuck behind it.
                if (timeout > 0 && !current.ReceiveParked && now - Volatile.Read(ref current.LastReceivedAt) > timeout)
                {
                    LinkLost(current, IonDisconnectReason.Timeout,
                        new TimeoutException($"The client sent nothing for longer than {options.ClientTimeout}."));
                    if (!resumable)
                        return;
                    continue;
                }

                // Only into an empty queue: anything already queued is as good as a ping.
                if (keepAlive > 0 &&
                    now - Volatile.Read(ref current.LastSentAt) >= keepAlive &&
                    outbound.Reader.Count == 0)
                    outbound.Writer.TryWrite(new Outgoing(OutgoingKind.Ping, PingFrame));

                // Whatever the client sent since the last ACK is acknowledged at least this often.
                if (replay is not null && Volatile.Read(ref inputReceived) > Volatile.Read(ref inputAckedSent))
                    RequestAck();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Ends a detached session whose client did not come back in time.</summary>
    private void CheckResumeWindow(long now, long window)
    {
        IonDisconnectReason reason;
        Exception? cause;
        lock (gate)
        {
            if (link is not null || lostAt == 0 || expired || now - lostAt <= window)
                return;

            expired = true;
            reason = lostReason;
            cause = lostException;
        }

        log.LogInformation("Stream connection {ConnectionId} was not resumed within {Window}", ConnectionId, options.ResumeWindow);

        if (!RequestDisconnect(reason, new IonStreamNotResumedException(options.ResumeWindow, cause)))
            WakeState(); // a goodbye was waiting to be delivered; it never will be
    }

    // ── the end ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Says goodbye the way <paramref name="info"/> calls for, then closes or kills the transport.</summary>
    private async Task CloseTransportAsync(IonDisconnectInfo info)
    {
        if (resumable && readySent && GoodbyeNeedsDelivery(info.Reason))
        {
            var (frame, status, description, flush) = info.Reason switch
            {
                IonDisconnectReason.Completed => (EndFrame, WebSocketCloseStatus.NormalClosure, "completed", true),
                IonDisconnectReason.ServerClosed => (IonStreamProtocol.ServerCloseFrame(info.Message, info.AllowReconnect),
                    WebSocketCloseStatus.NormalClosure, info.Message ?? "closed", true),
                _ => (IonStreamProtocol.ErrorFrame(ErrorFor(info)), CloseStatusFor(info), CloseDescriptionFor(info), false)
            };

            await DeliverGoodbyeAsync(frame, status, description, flush).ConfigureAwait(false);
            return;
        }

        var current = Volatile.Read(ref link);
        if (current is null)
            return; // detached: there is nobody to say goodbye to

        if (readySent && current.Send is null)
        {
            // A resuming transport that has not started yet: its handler owns it until it does.
            AbortTransport();
            return;
        }

        switch (info.Reason)
        {
            case IonDisconnectReason.Completed:
                await GoodbyeAsync(current, EndFrame, WebSocketCloseStatus.NormalClosure, "completed", flush: true, patient: true)
                    .ConfigureAwait(false);
                break;

            case IonDisconnectReason.ServerClosed:
            case IonDisconnectReason.ServerShutdown:
                await GoodbyeAsync(current,
                    IonStreamProtocol.ServerCloseFrame(info.Message, info.AllowReconnect),
                    info.Reason == IonDisconnectReason.ServerShutdown
                        ? WebSocketCloseStatus.EndpointUnavailable
                        : WebSocketCloseStatus.NormalClosure,
                    info.Message ?? "closed",
                    flush: true,
                    // A kick is patient; a host that is stopping cannot be.
                    patient: info.Reason == IonDisconnectReason.ServerClosed).ConfigureAwait(false);
                break;

            case IonDisconnectReason.ClientClosed:
                // Nobody is listening for the backlog any more.
                await GoodbyeAsync(current, null, WebSocketCloseStatus.NormalClosure, "goodbye", flush: false)
                    .ConfigureAwait(false);
                break;

            case IonDisconnectReason.Faulted:
            case IonDisconnectReason.Rejected:
            case IonDisconnectReason.ProtocolViolation:
                await GoodbyeAsync(current, IonStreamProtocol.ErrorFrame(ErrorFor(info)), CloseStatusFor(info),
                    CloseDescriptionFor(info), flush: false).ConfigureAwait(false);
                break;

            default:
                AbortTransport();
                break;
        }
    }

    /// <summary>
    /// Queues the final frame and the end of our side behind whatever is already queued (or drops
    /// the backlog first), then waits for the client to end its side — which the receive loop
    /// observes. Anything that does not happen within the close timeout aborts the transport.
    /// </summary>
    /// <param name="patient">
    /// Wait for the client for as long as it is alive, not for the close timeout. A stream that
    /// completed — or that server code closed on purpose — may still be on its way to a client that
    /// reads slowly; aborting it on a clock would cut off the tail of a successful stream. The client
    /// keeps pinging while it drains, so silence still ends the wait, and
    /// <see cref="IonStreamOptions.CompletionDrainTimeout"/> caps it for a client that pings but
    /// never reads.
    /// </param>
    private async Task GoodbyeAsync(Link l, byte[]? finalFrame, WebSocketCloseStatus status, string description, bool flush,
        bool patient = false)
    {
        if (!flush)
            discardPushes = true;

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(l.Cts.Token);
        Task? watchdog = null;
        if (patient && Millis(options.ClientTimeout) > 0)
        {
            watchdog = WatchDrainAsync(l, bound);
        }
        else
        {
            var timeout = Bounded(options.CloseTimeout);
            if (timeout != Timeout.InfiniteTimeSpan)
                bound.CancelAfter(timeout);
        }

        try
        {
            if (l.Send is null)
            {
                // Never got as far as a send loop: this is the only writer there is.
                if (finalFrame is not null)
                    await l.Transport.SendAsync(finalFrame, bound.Token).ConfigureAwait(false);
                await l.Transport.CloseOutputAsync(status, description, bound.Token).ConfigureAwait(false);
            }
            else
            {
                if (finalFrame is not null)
                    await outbound.Writer.WriteAsync(new Outgoing(OutgoingKind.Goodbye, finalFrame), bound.Token).ConfigureAwait(false);
                await outbound.Writer.WriteAsync(new Outgoing(status, description), bound.Token).ConfigureAwait(false);
                await l.Send.WaitAsync(bound.Token).ConfigureAwait(false);
            }

            await l.Receive.WaitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or WebSocketException
                                       or ObjectDisposedException or InvalidOperationException or IOException)
        {
            log.LogDebug(ex, "Goodbye on connection {ConnectionId} did not complete", ConnectionId);
        }
        finally
        {
            if (!l.Transport.IsClosed)
                AbortTransport();

            if (watchdog is not null)
            {
                await bound.CancelAsync().ConfigureAwait(false);
                await watchdog.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The goodbye of a resumable session: it is delivered when the client acknowledges it, on
    /// whichever transport that happens. A transport lost on the way is resumed to deliver it; the
    /// session gives up only when the resume window or <see cref="IonStreamOptions.CompletionDrainTimeout"/>
    /// runs out. Then the transport it ended on is closed properly.
    /// </summary>
    private async Task DeliverGoodbyeAsync(byte[] finalFrame, WebSocketCloseStatus status, string description, bool flush)
    {
        if (!flush)
            discardPushes = true;

        using var bound = new CancellationTokenSource();
        var cap = Bounded(options.CompletionDrainTimeout);
        if (cap != Timeout.InfiniteTimeSpan)
            bound.CancelAfter(cap);

        try
        {
            await outbound.Writer.WriteAsync(new Outgoing(OutgoingKind.Goodbye, finalFrame), bound.Token).ConfigureAwait(false);

            while (true)
            {
                var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref stateWake, wake);

                var seq = Volatile.Read(ref goodbyeSeq);
                if (seq != 0 && Volatile.Read(ref peerAcked) >= seq)
                {
                    lock (gate)
                        goodbyeDelivered = true;
                    break;
                }

                bool gaveUp;
                lock (gate)
                    gaveUp = expired || ending;
                if (gaveUp)
                    break;

                await wake.Task.WaitAsync(bound.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            log.LogDebug("Connection {ConnectionId} stopped waiting for its goodbye to be acknowledged", ConnectionId);
        }

        var current = Volatile.Read(ref link);
        if (current is null)
            return;

        if (!goodbyeDelivered)
        {
            AbortTransport();
            return;
        }

        // Acknowledged: end our side, and give the client the close timeout to end its own.
        using var close = CancellationTokenSource.CreateLinkedTokenSource(current.Cts.Token);
        var timeout = Bounded(options.CloseTimeout);
        if (timeout != Timeout.InfiniteTimeSpan)
            close.CancelAfter(timeout);

        try
        {
            await outbound.Writer.WriteAsync(new Outgoing(status, description), close.Token).ConfigureAwait(false);
            if (current.Send is not null)
                await current.Send.WaitAsync(close.Token).ConfigureAwait(false);
            await current.Receive.WaitAsync(close.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or WebSocketException
                                       or ObjectDisposedException or InvalidOperationException or IOException)
        {
            log.LogDebug(ex, "Closing connection {ConnectionId} after its goodbye did not complete", ConnectionId);
        }
        finally
        {
            if (!current.Transport.IsClosed)
                AbortTransport();
        }
    }

    /// <summary>
    /// Ends a patient goodbye when the client goes silent for <see cref="IonStreamOptions.ClientTimeout"/>
    /// — its pings keep arriving while it drains, the receive loop keeps reading them — or when the
    /// drain cap runs out.
    /// </summary>
    private async Task WatchDrainAsync(Link l, CancellationTokenSource bound)
    {
        var silence = Millis(options.ClientTimeout);
        var cap = Millis(options.CompletionDrainTimeout);
        var started = Environment.TickCount64;

        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(silence / 4, 5, 1000)), bound.Token).ConfigureAwait(false);

                var now = Environment.TickCount64;
                if ((!l.ReceiveParked && now - Volatile.Read(ref l.LastReceivedAt) > silence) ||
                    (cap > 0 && now - started > cap))
                {
                    log.LogDebug("Connection {ConnectionId} stopped waiting for its client to drain: silent for {Silent} ms, draining for {Draining} ms",
                        ConnectionId, now - Volatile.Read(ref l.LastReceivedAt), now - started);
                    await bound.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private IonProtocolError ErrorFor(IonDisconnectInfo info) => info.Reason switch
    {
        IonDisconnectReason.ProtocolViolation => new IonProtocolError("PROTOCOL_VIOLATION",
            info.Message ?? "The client broke the stream protocol."),
        _ => IonErrorSanitizer.Sanitize(info.Exception ?? new InvalidOperationException(info.Message), detailedErrors)
    };

    private static WebSocketCloseStatus CloseStatusFor(IonDisconnectInfo info) => info.Reason switch
    {
        IonDisconnectReason.Rejected => WebSocketCloseStatus.PolicyViolation,
        IonDisconnectReason.ProtocolViolation when info.Exception is IonStreamMessageTooLargeException
            => WebSocketCloseStatus.MessageTooBig,
        IonDisconnectReason.ProtocolViolation => WebSocketCloseStatus.ProtocolError,
        _ => WebSocketCloseStatus.InternalServerError
    };

    private static string CloseDescriptionFor(IonDisconnectInfo info) => info.Reason switch
    {
        IonDisconnectReason.Rejected => "rejected",
        IonDisconnectReason.ProtocolViolation => "protocol violation",
        _ => "error"
    };

    private async Task AwaitStreamStopAsync(Task streamTask)
    {
        try
        {
            await streamTask.WaitAsync(Bounded(options.StreamStopTimeout)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            log.LogWarning(
                "{Interface}.{Method} did not stop within {Timeout} of connection {ConnectionId} ending; " +
                "running the disconnect hooks without waiting for it. Pass the CancellationToken on.",
                Interface.Name, Method.Name, options.StreamStopTimeout, ConnectionId);
        }
    }

    private async Task QuietlyAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { log.LogDebug(ex, "Connection {ConnectionId} loop ended with an exception", ConnectionId); }
    }

    private static long Millis(TimeSpan value)
        => value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan ? 0 : (long)value.TotalMilliseconds;

    private static TimeSpan Bounded(TimeSpan value)
        => value <= TimeSpan.Zero ? Timeout.InfiniteTimeSpan : value;

    /// <summary>One transport of the session, and the two loops that run it.</summary>
    private sealed class Link(IonStreamTransport transport, HttpContext http)
    {
        public IonStreamTransport Transport { get; } = transport;
        public HttpContext Http { get; } = http;
        public CancellationTokenSource Cts { get; } = new();
        public Task Receive = Task.CompletedTask;
        public Task? Send;
        public CancellationTokenRegistration HttpAborted;
        public long LastReceivedAt = Environment.TickCount64;
        public long LastSentAt = Environment.TickCount64;
        public volatile bool ReceiveParked;

        /// <summary>Lost or taken over; set under the connection's gate, read by the loops.</summary>
        public volatile bool Lost;

        /// <summary>Completes once this link's loops have stopped — or, for one that never ran, once it knows it will not.</summary>
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The send loop's ACK frame, reused.</summary>
        public readonly byte[] AckScratch = new byte[IonStreamProtocol.MaxAckFrameSize];

        /// <summary>A resumable session's input that the stream method had no room for yet; see <c>Feed</c>.</summary>
        public Channel<InputItem>? Overflow;
        public long OverflowBytes;
        public int OverflowCount;
        public volatile bool FeederBusy;
        public Task? Feeder;
    }

    private enum InputKind : byte
    {
        Data,
        End,
        Error
    }

    /// <summary>One of the client's input frames on its way to the stream method.</summary>
    private readonly record struct InputItem(InputKind Kind, ReadOnlyMemory<byte> Payload, Exception? Error, int Bytes);

    private enum OutgoingKind : byte
    {
        Borrowed,
        Push,
        Ping,
        Ack,
        Goodbye,
        CloseOutput,
        Stop
    }

    private readonly struct Outgoing
    {
        public Outgoing(OutgoingKind kind, ReadOnlyMemory<byte> frame)
        {
            Kind = kind;
            Frame = frame;
        }

        public Outgoing(WebSocketCloseStatus status, string? description)
        {
            Kind = OutgoingKind.CloseOutput;
            CloseStatus = status;
            CloseDescription = description;
        }

        public OutgoingKind Kind { get; }
        public ReadOnlyMemory<byte> Frame { get; }
        public WebSocketCloseStatus CloseStatus { get; }
        public string? CloseDescription { get; }
    }

    /// <summary>
    /// The reliable frames sent and not yet acknowledged, oldest first, numbered from
    /// <see cref="FirstSeq"/>. Borrowed frames are copied into pooled arrays; pushed and goodbye
    /// frames are immutable arrays and are kept as they are. One send loop at a time touches it — or
    /// <see cref="ResumeAsync"/>, while none runs — so it needs no lock.
    /// </summary>
    private sealed class ReplayBuffer
    {
        private Entry[] ring = new Entry[64];
        private int head;

        public int Count { get; private set; }
        public long Bytes { get; private set; }
        public long FirstSeq { get; private set; } = 1;
        public long NextSeq => FirstSeq + Count;

        public ReadOnlyMemory<byte> this[int index]
        {
            get
            {
                var e = ring[(head + index) % ring.Length];
                return e.Array.AsMemory(0, e.Length);
            }
        }

        public ReadOnlyMemory<byte> Append(ReadOnlyMemory<byte> frame, bool copy)
        {
            if (Count == ring.Length)
                Grow();

            Entry entry;
            if (!copy && MemoryMarshal.TryGetArray(frame, out var segment) && segment.Offset == 0)
            {
                entry = new Entry(segment.Array!, segment.Count, pooled: false);
            }
            else
            {
                var array = ArrayPool<byte>.Shared.Rent(frame.Length);
                frame.Span.CopyTo(array);
                entry = new Entry(array, frame.Length, pooled: true);
            }

            ring[(head + Count) % ring.Length] = entry;
            Count++;
            Bytes += entry.Length;
            return entry.Array.AsMemory(0, entry.Length);
        }

        /// <summary>Lets go of every frame numbered <paramref name="acknowledged"/> or lower.</summary>
        public void TrimTo(long acknowledged)
        {
            while (Count > 0 && FirstSeq <= acknowledged)
            {
                ref var e = ref ring[head];
                Bytes -= e.Length;
                if (e.Pooled)
                    ArrayPool<byte>.Shared.Return(e.Array);
                e = default;
                head = (head + 1) % ring.Length;
                Count--;
                FirstSeq++;
            }
        }

        public void Clear() => TrimTo(long.MaxValue);

        private void Grow()
        {
            var grown = new Entry[ring.Length * 2];
            for (var i = 0; i < Count; i++)
                grown[i] = ring[(head + i) % ring.Length];
            ring = grown;
            head = 0;
        }

        private readonly struct Entry(byte[] array, int length, bool pooled)
        {
            public byte[] Array { get; } = array;
            public int Length { get; } = length;
            public bool Pooled { get; } = pooled;
        }
    }

    /// <summary>
    /// The pump's one outstanding send, as a reusable value-task source: awaiting a send costs no
    /// allocation. The send loop completes it exactly once per <see cref="Reset"/>.
    /// </summary>
    private sealed class PumpAck : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> core = new() { RunContinuationsAsynchronously = true };
        private int completed;

        public void Reset()
        {
            core.Reset();
            Volatile.Write(ref completed, 0);
        }

        public ValueTask AsValueTask() => new(this, core.Version);

        public void Succeed()
        {
            if (Interlocked.Exchange(ref completed, 1) == 0)
                core.SetResult(true);
        }

        public void Fail(Exception? cause)
        {
            if (Interlocked.Exchange(ref completed, 1) == 0)
                core.SetException(new IonStreamSendException(cause));
        }

        public void GetResult(short token) => core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
            => core.OnCompleted(continuation, state, token, flags);
    }
}

/// <summary>How an attempt to resume a session ended.</summary>
internal enum IonResumeOutcome
{
    /// <summary>The transport ran the session until it was lost or the session ended.</summary>
    Resumed,

    /// <summary>The session is over, or cannot take a transport now.</summary>
    NotResumable,

    /// <summary>A newer transport of the same client took over before this one started.</summary>
    Superseded,

    /// <summary>The RESUME made no sense against what the server sent; the session ended over it.</summary>
    Violation,

    /// <summary>The transport died before RESUMED could be sent; the session waits for the next.</summary>
    Lost
}

/// <summary>The resumable sessions of this server, by resume token.</summary>
internal sealed class IonStreamResumeRegistry
{
    private readonly ConcurrentDictionary<string, IonStreamConnection> sessions = new(StringComparer.Ordinal);

    public int Count => sessions.Count;

    /// <summary>A fresh, unguessable token for <paramref name="connection"/> — never the connection id, which other code sees.</summary>
    public string Register(IonStreamConnection connection)
    {
        while (true)
        {
            var token = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            if (sessions.TryAdd(token, connection))
                return token;
        }
    }

    public IonStreamConnection? Find(string token)
        => sessions.TryGetValue(token, out var connection) ? connection : null;

    public void Remove(string token, IonStreamConnection connection)
        => sessions.TryRemove(new KeyValuePair<string, IonStreamConnection>(token, connection));
}
