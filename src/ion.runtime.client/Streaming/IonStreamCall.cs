namespace ion.runtime.client;

using System.Runtime.CompilerServices;
using System.Buffers;
using System.Formats.Cbor;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using network;

/// <summary>What a stream call needs from its client: a way to a transport, in the configured order.</summary>
internal interface IIonStreamConnector
{
    Type Interface { get; }

    MethodInfo Method { get; }

    /// <summary>
    /// Opens a transport of each configured kind in turn and runs <paramref name="handshake"/> on it,
    /// until one succeeds. A transport that fails before the handshake completes falls through to the
    /// next; the server refusing the call does not.
    /// </summary>
    Task<T> ConnectAsync<T>(Func<IonClientTransport, CancellationToken, Task<T>> handshake, CancellationToken ct);
}

/// <summary>
/// One stream call, across every connection it takes: the client's half of the protocol, and the
/// decision of what to do when a connection ends.
/// </summary>
/// <remarks>
/// <para><b>One reader, one writer, no locks</b> — per connection, the same shape as the server. A
/// <see cref="Link"/> is one transport: its receive loop reads frames and hands DATA to the consumer
/// through a bounded channel; when the consumer falls behind the channel fills and the loop stops
/// reading, which is the backpressure the server sees. Its send loop is the only writer: input
/// items, pings and ACKs queue up for it in order.</para>
///
/// <para><b>The supervisor</b> owns the call's life. It connects, waits for the connection to end,
/// and decides: the server ended the stream (return, or throw its error), the connection was lost
/// (reconnect, per <see cref="IonStreamClientOptions.Reconnect"/>), or the consumer left (stop).
/// Reconnecting resumes the session when the server keeps one — the replay buffer on each side
/// makes that exact — and starts the call afresh when it does not.</para>
///
/// <para><b>Item storage is not pooled.</b> Each DATA payload gets an exact-size array of its own,
/// because decoding may keep it: <c>bytes</c> decodes to an <see cref="IonBytes"/> that points into the
/// payload rather than copying it. A pooled buffer handed back after decoding would be rented for the
/// next frame while the caller still holds the value — which is exactly the corruption this rule
/// exists to rule out. Everything around the items (receive scratch, outgoing frames) is pooled.</para>
///
/// <para><b>How a call ends</b> is how the item channel is completed: END completes it cleanly,
/// ERROR / CLOSE / a failure that is not retried complete it with the exception the consumer will
/// see, after it has read everything that arrived before.</para>
/// </remarks>
internal sealed class IonStreamCall : IAsyncDisposable
{
    private static readonly byte[] PingFrame = [IonStreamProtocol.OpPing];
    private static readonly byte[] EndOfInputFrame = [IonStreamProtocol.OpEnd];

    private readonly IIonStreamConnector connector;
    private readonly IonStreamClientOptions options;
    private readonly ReadOnlyMemory<byte> args;
    private readonly Channel<byte[]> items;
    private readonly Channel<Outgoing> outbound;
    private readonly CancellationTokenSource callCts = new();
    private readonly CancellationTokenSource inputCts = new();
    private readonly Lock gate = new();

    private Link? link;                     // under gate
    private Link? lastLink;                 // under gate: the newest link, for the clean-up
    private bool leaving;                   // under gate: the consumer left
    private Func<Task>? startInput;
    private Task? inputPump;
    private Task supervisor = Task.CompletedTask;
    private int disposed;

    // ── the session ──
    private IonStreamReady announced;       // the first READY: timings and the resume token
    private string? resumeToken;
    private long lostAt;
    private long received;                  // receive loop
    private long receivedBytes;             // receive loop
    private long ackSent;                   // send loop
    private long ackSentBytes;              // send loop
    private long sentReliable;              // send loop
    private long peerAcked;                 // receive loop, or the handshake while no loop runs
    private ReplayBuffer? replay;           // send loop, or the handshake while no loop runs
    private Outgoing? pending;              // send loop: taken from the queue, not yet written or kept
    private bool endSent;                   // send loop: END of input written or kept
    private bool resendEnd;                 // a fresh start after END was sent: say it again
    private volatile bool ackWanted;
    private int ackQueued;

    private IonStreamCall(IIonStreamConnector connector, IonStreamClientOptions options, ReadOnlyMemory<byte> args)
    {
        this.connector = connector;
        this.options = options;
        this.args = args;

        items = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Math.Max(1, options.ReceiveQueueCapacity))
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        outbound = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>The server's id for this connection, from READY; the same across resumes.</summary>
    public string? ConnectionId { get; private set; }

    /// <summary>
    /// Starts the call: connects in the background, and the consumer's first read waits for it. The
    /// client's <paramref name="input"/> stream — one DATA per item, then END — starts once the server
    /// has accepted the call; a call the server refuses never starts enumerating it.
    /// </summary>
    public static IonStreamCall Start<TRequest>(IIonStreamConnector connector, IonStreamClientOptions options,
        ReadOnlyMemory<byte> args, IAsyncEnumerable<TRequest>? input)
    {
        var call = new IonStreamCall(connector, options, args);
        if (input is not null)
            call.startInput = () => call.PumpInputAsync(input);
        call.supervisor = Task.Run(call.SuperviseAsync);
        return call;
    }

    /// <summary>
    /// The next item's payload — the caller's to keep — or null once the server ended the stream
    /// normally. Throws how the call failed.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<byte[]?> ReadAsync(CancellationToken ct)
    {
        try
        {
            while (await items.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                if (items.Reader.TryRead(out var item))
                    return item;
        }
        catch (ChannelClosedException closed) when (closed.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(closed.InnerException).Throw();
        }
        return null;
    }

    // ── the supervisor ────────────────────────────────────────────────────────────────────────────

    private async Task SuperviseAsync()
    {
        var ct = callCts.Token;
        var policy = options.Reconnect;
        var attempt = 0;
        var reconnecting = false;
        var backOff = false;
        Exception? failure = null;

        try
        {
            while (true)
            {
                if (backOff)
                {
                    attempt++;
                    if (attempt > policy!.MaxAttempts)
                    {
                        Fail(failure!);
                        return;
                    }

                    var delay = policy.DelayFor(attempt);
                    Notify(options.OnReconnecting, new IonStreamReconnecting(connector.Interface, connector.Method, attempt, delay, failure!));
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    backOff = false;
                }

                var resuming = CanResume();
                if (!resuming)
                    ForgetSession();

                Link established;
                try
                {
                    established = await connector.ConnectAsync((t, token) => HandshakeAsync(t, resuming, token), ct)
                        .ConfigureAwait(false);
                }
                catch (IonStreamNotResumableException)
                {
                    // An answer, not a failure: the session is gone, so the call starts afresh — now.
                    ForgetSession();
                    continue;
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (policy is not null && IsRetryable(ex))
                {
                    failure = ex;
                    backOff = true;
                    continue;
                }
                catch (Exception ex)
                {
                    Fail(ex);
                    return;
                }

                if (reconnecting)
                    Notify(options.OnReconnected, new IonStreamReconnected(connector.Interface, connector.Method,
                        Math.Max(1, attempt), resuming, ConnectionId ?? ""));

                reconnecting = true;
                attempt = 0;

                if (startInput is { } start)
                {
                    startInput = null;
                    inputPump = start();
                }

                var end = await established.Ended.Task.WaitAsync(ct).ConfigureAwait(false);
                switch (end.Kind)
                {
                    case EndKind.Completed:
                        items.Writer.TryComplete();
                        inputCts.Cancel();
                        _ = FinishOnceAsync(established, graceful: true);
                        return;

                    case EndKind.Lost when policy is not null:
                        failure = end.Exception;
                        lostAt = Environment.TickCount64;
                        await StopLinkAsync(established).ConfigureAwait(false);
                        backOff = true;
                        continue;

                    case EndKind.Closed when policy is not null && end.Exception is IonStreamClosedException { AllowReconnect: true }:
                        // The server ended the session and invites the client back: a fresh call.
                        failure = end.Exception;
                        await StopLinkAsync(established, graceful: true).ConfigureAwait(false);
                        ForgetSession();
                        backOff = true;
                        continue;

                    default:
                        Fail(end.Exception!);
                        _ = FinishOnceAsync(established, graceful: end.Kind is EndKind.Failed or EndKind.Closed);
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The consumer left; DisposeAsync says goodbye.
        }
        catch (Exception ex)
        {
            // Every path above handles its own failures; reaching here is a bug in this class.
            Fail(ex);
        }
    }

    /// <summary>Whether a failure is one that trying again can fix.</summary>
    private static bool IsRetryable(Exception ex) => ex switch
    {
        IonStreamDisconnectedException d => d.Reason != IonDisconnectReason.ProtocolViolation,
        IonStreamClosedException c => c.AllowReconnect,
        // A ticket exchange (or an upgrade) the server answered: only a busy or broken server is worth retrying.
        IonRequestException r => r.HttpStatusCode is 408 or 429 or >= 500,
        HttpRequestException or IOException or SocketException or WebSocketException or TimeoutException => true,
        // An HttpClient timeout, not the caller's cancellation (that is filtered before this).
        OperationCanceledException => true,
        _ => false
    };

    private bool CanResume()
    {
        if (!options.Resumable || resumeToken is null)
            return false;

        var window = announced.ResumeWindow;
        return window == Timeout.InfiniteTimeSpan || window <= TimeSpan.Zero ||
               Environment.TickCount64 - lostAt < (long)window.TotalMilliseconds;
    }

    /// <summary>
    /// The session is gone; the next connection starts the call afresh. Input the dead session never
    /// acknowledged is lost with it — the new stream method knows nothing of it — but an END it had
    /// is said again, and an item taken from the input and not yet sent is sent on the new one.
    /// </summary>
    private void ForgetSession()
    {
        resumeToken = null;
        received = receivedBytes = ackSent = ackSentBytes = sentReliable = peerAcked = 0;
        ackWanted = false;
        replay?.Clear();
        if (endSent)
        {
            endSent = false;
            resendEnd = true;
        }
    }

    private void Fail(Exception failure)
    {
        items.Writer.TryComplete(failure);
        inputCts.Cancel();
    }

    private static void Notify<T>(Action<T>? callback, T args)
    {
        try { callback?.Invoke(args); }
        catch (Exception) { }
    }

    // ── one connection ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the handshake on a freshly opened transport — the arguments, or a RESUME — and waits for
    /// READY or RESUMED. On success the link runs; on failure the transport is let go (gracefully,
    /// when the server said goodbye) and the failure is thrown.
    /// </summary>
    private async Task<Link> HandshakeAsync(IonClientTransport transport, bool resuming, CancellationToken ct)
    {
        var l = new Link(transport, resuming);
        lock (gate)
        {
            if (leaving)
            {
                _ = DisposeTransportAsync(transport);
                throw new OperationCanceledException(ct);
            }

            link = l;
            lastLink = l;
        }

        try
        {
            var first = resuming ? IonStreamProtocol.ResumeFrame(resumeToken!, received) : args;
            await transport.SendAsync(first, ct).ConfigureAwait(false);
            Volatile.Write(ref l.LastSentAt, Environment.TickCount64);

            l.Receive = ReceiveLoopAsync(l);

            var handshake = options.HandshakeTimeout > TimeSpan.Zero ? options.HandshakeTimeout : Timeout.InfiniteTimeSpan;
            IonStreamReady ready;
            try
            {
                ready = await l.Ready.Task.WaitAsync(handshake, ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new IonStreamDisconnectedException(IonDisconnectReason.Timeout,
                    resuming
                        ? $"The server did not resume the stream within {options.HandshakeTimeout}."
                        : $"The server did not accept the stream within {options.HandshakeTimeout}.", ex);
            }

            if (resuming)
            {
                var serverReceived = l.ResumedCount;
                if (serverReceived < Volatile.Read(ref peerAcked) || serverReceived > sentReliable)
                    throw new IonStreamDisconnectedException(IonDisconnectReason.ProtocolViolation,
                        $"RESUMED claims {serverReceived} frames received; the client sent {sentReliable}.");

                Volatile.Write(ref peerAcked, serverReceived);
                replay?.TrimTo(serverReceived);
            }
            else
            {
                announced = ready;
                ConnectionId = ready.ConnectionId;
                if (options.Resumable && ready.ResumeToken is { } token)
                {
                    resumeToken = token;
                    replay ??= new ReplayBuffer();
                }
                else
                {
                    replay?.Clear();
                    replay = null;
                }
            }

            l.AdoptServerTimings(announced, options);

            // The send loop exists before the link counts as running, so whoever sees it running
            // can wait for the loop; it starts sending once told to.
            l.Send = SendLoopAsync(l);
            lock (gate)
            {
                if (leaving)
                    throw new OperationCanceledException(ct);
                l.Running = true;
            }

            l.Go.TrySetResult(true);
            l.Heartbeat = HeartbeatAsync(l);
            return l;
        }
        catch (Exception)
        {
            bool leave;
            lock (gate)
            {
                leave = leaving;
                if (ReferenceEquals(link, l))
                    link = null;
            }

            l.Go.TrySetResult(false);
            if (leave && !l.Terminal)
                _ = LeaveDirectAsync(l);
            else
                _ = FinishOnceAsync(l, graceful: l.Terminal);
            throw;
        }
    }

    /// <summary>How a link ended: <paramref name="end"/>, unless it already ended otherwise.</summary>
    private void End(Link l, LinkEnd end, bool abort)
    {
        if (!l.Ended.TrySetResult(end))
            return;

        l.Ready.TrySetException(end.Exception ?? new IonStreamDisconnectedException(IonDisconnectReason.ProtocolViolation,
            "The server ended the stream before accepting it."));
        if (abort)
            AbortLink(l);
    }

    private void AbortLink(Link l)
    {
        l.Lost = true;
        try { l.Cts.Cancel(); }
        catch (AggregateException) { }

        try { l.Transport.Abort(); }
        catch (Exception) { }

        // An idle send loop waits on the queue, not on the token: a wake item stops it.
        outbound.Writer.TryWrite(new Outgoing(OutgoingKind.Wake, default, null));
        l.WakeSend();
    }

    private async Task ReceiveLoopAsync(Link l)
    {
        try
        {
            while (true)
            {
                var frame = await l.Transport.ReceiveAsync(options.MaxReceiveMessageSize, l.Cts.Token).ConfigureAwait(false);
                Volatile.Write(ref l.LastReceivedAt, Environment.TickCount64);

                if (frame.IsClose)
                {
                    if (!l.Terminal)
                        End(l, LinkEnd.Lost(new IonStreamDisconnectedException(IonDisconnectReason.TransportLost,
                            "The server closed the transport without ending the stream.", null,
                            l.Transport.PeerCloseStatus, l.Transport.PeerCloseDescription)), abort: false);

                    // Answer the server's close, if our side is still open.
                    RequestClose(l);
                    return;
                }

                var message = frame.Payload;
                if (!frame.IsBinary || message.IsEmpty)
                {
                    Violation(l, "The server sent a non-binary or empty frame.");
                    return;
                }

                if (l.Terminal)
                    continue; // After the goodbye only the transport's own close matters.

                var payload = message[1..];
                var accepted = l.Ready.Task.IsCompletedSuccessfully;
                switch (message.Span[0])
                {
                    case IonStreamProtocol.OpData:
                        if (!accepted)
                        {
                            Violation(l, "The server sent DATA before READY.");
                            return;
                        }

                        var item = payload.ToArray();

                        if (!items.Writer.TryWrite(item))
                        {
                            // The consumer is behind: stop reading until it catches up. The heartbeat
                            // knows the silence that follows is ours, not the server's.
                            l.ReceiveParked = true;
                            try
                            {
                                await items.Writer.WriteAsync(item, l.Cts.Token).ConfigureAwait(false);
                            }
                            catch (ChannelClosedException)
                            {
                                // The call is over; nobody reads any more.
                            }
                            finally
                            {
                                l.ReceiveParked = false;
                                Volatile.Write(ref l.LastReceivedAt, Environment.TickCount64);
                            }
                        }

                        // Counted once handed over: an item the transport died holding is sent again.
                        CountReceived(message.Length);
                        break;

                    case IonStreamProtocol.OpReady:
                        if (l.Resuming || l.Ready.Task.IsCompleted)
                        {
                            Violation(l, "The server sent an unexpected READY.");
                            return;
                        }

                        l.Ready.TrySetResult(IonStreamProtocol.ReadReady(payload));
                        break;

                    case IonStreamProtocol.OpResumed:
                        if (!l.Resuming || l.Ready.Task.IsCompleted)
                        {
                            Violation(l, "The server sent an unexpected RESUMED.");
                            return;
                        }

                        l.ResumedCount = IonStreamProtocol.ReadResumed(payload);
                        l.Ready.TrySetResult(announced);
                        break;

                    case IonStreamProtocol.OpPing:
                        break;

                    case IonStreamProtocol.OpAck:
                        if (replay is null || !accepted)
                        {
                            Violation(l, "The server sent ACK on a stream that is not resumable.");
                            return;
                        }

                        var acked = IonStreamProtocol.ReadAck(payload);
                        if (acked < Volatile.Read(ref peerAcked) || acked > Volatile.Read(ref sentReliable))
                        {
                            Violation(l, $"The server acknowledged {acked} frames; the client sent {Volatile.Read(ref sentReliable)}.");
                            return;
                        }

                        Volatile.Write(ref peerAcked, acked);
                        l.WakeSend();
                        break;

                    case IonStreamProtocol.OpEnd:
                        if (!accepted)
                        {
                            Violation(l, "The server ended the stream before accepting it.");
                            return;
                        }

                        CountReceived(message.Length);
                        Goodbye(l, LinkEnd.Completed);
                        break;

                    case IonStreamProtocol.OpError:
                        var error = IonStreamProtocol.ReadError(payload);
                        if (l.Resuming && !accepted && error.code == IonStreamProtocol.NotResumableCode)
                        {
                            Goodbye(l, LinkEnd.NotResumable);
                            break;
                        }

                        if (accepted)
                            CountReceived(message.Length);
                        Goodbye(l, LinkEnd.Failed(new IonRequestException(error)));
                        break;

                    case IonStreamProtocol.OpClose:
                        var (reason, allowReconnect) = IonStreamProtocol.ReadClose(payload);
                        if (accepted)
                            CountReceived(message.Length);
                        Goodbye(l, LinkEnd.Closed(new IonStreamClosedException(reason, allowReconnect)));
                        break;

                    default:
                        Violation(l, $"The server sent unknown opcode 0x{message.Span[0]:x2}.");
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (l.Cts.IsCancellationRequested)
        {
            // Aborted on purpose; whoever aborted recorded why.
        }
        catch (Exception ex) when (ex is CborContentException or IonDecodeException)
        {
            Violation(l, $"The server sent a malformed control frame: {ex.Message}");
        }
        catch (IonClientFrameTooLargeException ex)
        {
            End(l, LinkEnd.Violated(new IonStreamDisconnectedException(IonDisconnectReason.ProtocolViolation, ex.Message, ex)), abort: true);
        }
        catch (Exception ex)
        {
            End(l, LinkEnd.Lost(new IonStreamDisconnectedException(IonDisconnectReason.TransportLost,
                $"The {l.Transport.Kind} transport failed: {ex.Message}", ex)), abort: true);
        }
    }

    private void Violation(Link l, string message)
        => End(l, LinkEnd.Violated(new IonStreamDisconnectedException(IonDisconnectReason.ProtocolViolation, message)), abort: true);

    /// <summary>
    /// The server said goodbye: acknowledge it at once (a resumable server holds it until then), stop
    /// the input, and end our side behind the ACK.
    /// </summary>
    private void Goodbye(Link l, LinkEnd end)
    {
        l.Terminal = true;
        if (end.Kind != EndKind.NotResumable && replay is not null && l.Running)
            RequestAck();

        End(l, end, abort: false);
        RequestClose(l);
    }

    /// <summary>One more of the server's reliable frames taken; acknowledged in batches.</summary>
    private void CountReceived(int bytes)
    {
        var count = received + 1;
        var total = receivedBytes + bytes;
        Volatile.Write(ref receivedBytes, total);
        Volatile.Write(ref received, count);

        if (replay is not null &&
            (count - Volatile.Read(ref ackSent) >= IonStreamProtocol.AckEveryFrames ||
             total - Volatile.Read(ref ackSentBytes) >= IonStreamProtocol.AckEveryBytes))
            RequestAck();
    }

    /// <summary>Asks the send loop for an ACK of everything received so far, without waiting in line.</summary>
    private void RequestAck()
    {
        ackWanted = true;
        if (Interlocked.Exchange(ref ackQueued, 1) == 0 && !outbound.Writer.TryWrite(new Outgoing(OutgoingKind.Ack, default, null)))
            Volatile.Write(ref ackQueued, 0);
        Volatile.Read(ref link)?.WakeSend();
    }

    /// <summary>Asks the link's send loop to end our side once what must go first has gone.</summary>
    private void RequestClose(Link l)
    {
        l.CloseRequested = true;
        outbound.Writer.TryWrite(new Outgoing(OutgoingKind.Wake, default, null));
        l.WakeSend();
    }

    /// <summary>The only code that writes to a link's transport once it runs.</summary>
    private async Task SendLoopAsync(Link l)
    {
        try
        {
            if (!await l.Go.Task.ConfigureAwait(false))
                return;

            if (l.Resuming && replay is not null)
            {
                // What the server did not get of the input, before anything new.
                for (var i = 0; i < replay.Count; i++)
                {
                    if (ackWanted)
                        await WriteAckAsync(l).ConfigureAwait(false);
                    await WriteAsync(l, replay[i]).ConfigureAwait(false);
                }
            }

            if (resendEnd && !l.Resuming)
            {
                pending ??= new Outgoing(OutgoingKind.Static, EndOfInputFrame, null);
                resendEnd = false;
            }

            while (true)
            {
                if (ackWanted)
                    await WriteAckAsync(l).ConfigureAwait(false);

                if (l.LeaveRequested)
                {
                    await LeaveAsync(l).ConfigureAwait(false);
                    return;
                }

                if (l.CloseRequested)
                {
                    // The server's goodbye is acknowledged before our side ends — checked here, not
                    // only at the top: the receive loop asks for the ACK and for the close one after
                    // the other, and this loop may have looked for the one before it saw the other.
                    if (replay is not null && Volatile.Read(ref received) > Volatile.Read(ref ackSent))
                        await WriteAckAsync(l).ConfigureAwait(false);
                    await l.Transport.CloseOutputAsync("client closed", l.Cts.Token).ConfigureAwait(false);
                    return;
                }

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
                    // No token: waits then use the channel's pooled operations. A wake item — or,
                    // at the end, completing the queue — is what stops an idle loop.
                    if (!await outbound.Reader.WaitToReadAsync().ConfigureAwait(false))
                        return;
                    if (l.Lost)
                        return;
                    continue;
                }

                switch (item.Kind)
                {
                    case OutgoingKind.Static:
                    case OutgoingKind.Pooled:
                        if (l.Terminal)
                        {
                            pending = null;
                            item.Release();
                            break;
                        }

                        var isEnd = item.Frame.Length == 1 && item.Frame.Span[0] == IonStreamProtocol.OpEnd;
                        if (!await SendReliableAsync(l, item).ConfigureAwait(false))
                            break; // a leave or a close came first; the top of the loop takes it
                        if (isEnd)
                            endSent = true;
                        break;

                    case OutgoingKind.Ping:
                        pending = null;
                        await WriteAsync(l, item.Frame).ConfigureAwait(false);
                        break;

                    case OutgoingKind.Ack:
                        pending = null;
                        Volatile.Write(ref ackQueued, 0);
                        if (ackWanted)
                            await WriteAckAsync(l).ConfigureAwait(false);
                        break;

                    case OutgoingKind.Wake:
                        pending = null;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (l.Cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            End(l, LinkEnd.Lost(new IonStreamDisconnectedException(IonDisconnectReason.TransportLost,
                $"Sending on the {l.Transport.Kind} transport failed: {ex.Message}", ex)), abort: true);
        }
    }

    /// <summary>
    /// Sends one of the client's reliable frames: kept for replay (and written from there) on a
    /// resumable session, written and released otherwise. <see cref="pending"/> is cleared once the
    /// frame is safe — kept, or written.
    /// </summary>
    private async ValueTask<bool> SendReliableAsync(Link l, Outgoing item)
    {
        if (replay is not null)
        {
            if (!await ReserveAsync(l, item.Frame.Length).ConfigureAwait(false))
                return false;
            var kept = replay.Append(item.Frame, item.PooledArray);
            Volatile.Write(ref sentReliable, replay.NextSeq - 1);
            pending = null;
            await WriteAsync(l, kept).ConfigureAwait(false);
            return true;
        }

        try
        {
            await WriteAsync(l, item.Frame).ConfigureAwait(false);
            Volatile.Write(ref sentReliable, sentReliable + 1);
        }
        finally
        {
            pending = null;
            item.Release();
        }

        return true;
    }

    /// <summary>
    /// The consumer left: what the server must still hear — an input stream's ERROR — goes out, the
    /// input items queued behind nobody's interest do not; then CLOSE and the end of our side.
    /// </summary>
    private async Task LeaveAsync(Link l)
    {
        var drained = pending;
        pending = null;
        while (true)
        {
            if (drained is { } item)
            {
                if (item.Kind == OutgoingKind.Static && item.Frame.Length > 1 && item.Frame.Span[0] == IonStreamProtocol.OpError)
                    await WriteAsync(l, item.Frame).ConfigureAwait(false);
                item.Release();
            }

            if (!outbound.Reader.TryRead(out var next))
                break;
            drained = next;
        }

        await WriteAsync(l, IonStreamProtocol.ClientCloseFrame(null)).ConfigureAwait(false);
        await l.Transport.CloseOutputAsync("client closed", l.Cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the replay buffer has room — the server's acknowledgements make it — while still
    /// answering the server's frames with ACKs, so two sides waiting on each other can never deadlock.
    /// </summary>
    /// <returns>False when a leave or a close came first; the frame stays pending.</returns>
    private async ValueTask<bool> ReserveAsync(Link l, int size)
    {
        // Ours, or the server's if that is smaller: it holds no more for a stream method that is not reading.
        var budget = Math.Max(options.ResumeBufferSize, 2 * IonStreamProtocol.AckEveryBytes);
        if (announced.ResumeInputBudget > 0)
            budget = Math.Min(budget, announced.ResumeInputBudget);
        while (true)
        {
            replay!.TrimTo(Volatile.Read(ref peerAcked));
            if (replay.Count == 0 ||
                (replay.Bytes + size <= budget && replay.Count < IonStreamProtocol.MaxUnacknowledgedFrames))
                return true;

            if (l.LeaveRequested || l.CloseRequested)
                return false;

            if (ackWanted)
            {
                await WriteAckAsync(l).ConfigureAwait(false);
                continue;
            }

            var wake = l.ArmWake();
            if (ackWanted || l.LeaveRequested || l.CloseRequested || replay.FirstSeq <= Volatile.Read(ref peerAcked))
                continue;

            await wake.WaitAsync(l.Cts.Token).ConfigureAwait(false);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask WriteAckAsync(Link l)
    {
        ackWanted = false;
        var bytes = Volatile.Read(ref receivedBytes);
        var count = Volatile.Read(ref received);
        var length = IonStreamProtocol.WriteAck(l.AckScratch, count);
        await WriteAsync(l, l.AckScratch.AsMemory(0, length)).ConfigureAwait(false);
        Volatile.Write(ref ackSent, count);
        Volatile.Write(ref ackSentBytes, bytes);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask WriteAsync(Link l, ReadOnlyMemory<byte> frame)
    {
        await l.Transport.SendAsync(frame, l.Cts.Token).ConfigureAwait(false);
        Volatile.Write(ref l.LastSentAt, Environment.TickCount64);
    }

    private async Task HeartbeatAsync(Link l)
    {
        var keepAlive = l.KeepAliveMs;
        var timeout = l.ServerTimeoutMs;
        if (keepAlive <= 0 && timeout <= 0 && replay is null)
            return;

        var tick = Math.Min(Positive(keepAlive), Positive(timeout));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(tick == long.MaxValue ? 1000 : tick / 4, 5, 1000)));

        try
        {
            while (await timer.WaitForNextTickAsync(l.Cts.Token).ConfigureAwait(false))
            {
                if (l.Terminal || l.LeaveRequested)
                    return;

                var now = Environment.TickCount64;

                if (timeout > 0 && !l.ReceiveParked && now - Volatile.Read(ref l.LastReceivedAt) > timeout)
                {
                    End(l, LinkEnd.Lost(new IonStreamDisconnectedException(IonDisconnectReason.Timeout,
                        $"The server sent nothing for {timeout} ms.")), abort: true);
                    return;
                }

                if (keepAlive > 0 && now - Volatile.Read(ref l.LastSentAt) >= keepAlive && outbound.Reader.Count == 0)
                    outbound.Writer.TryWrite(new Outgoing(OutgoingKind.Ping, PingFrame, null));

                // Whatever the server sent since the last ACK is acknowledged at least this often.
                if (replay is not null && Volatile.Read(ref received) > Volatile.Read(ref ackSent))
                    RequestAck();
            }
        }
        catch (OperationCanceledException)
        {
        }

        static long Positive(long v) => v > 0 ? v : long.MaxValue;
    }

    // ── input ─────────────────────────────────────────────────────────────────────────────────────

    private async Task PumpInputAsync<TRequest>(IAsyncEnumerable<TRequest> input)
    {
        var writer = new CborWriter();
        var ct = inputCts.Token;
        try
        {
            await foreach (var item in input.WithCancellation(ct).ConfigureAwait(false))
            {
                writer.Reset();
                writer.WriteStartArray(1);
                IonFormatterStorage<TRequest>.Write(writer, item);
                writer.WriteEndArray();

                var size = writer.BytesWritten + 1;
                var frame = ArrayPool<byte>.Shared.Rent(size);
                frame[0] = IonStreamProtocol.OpData;
                writer.Encode(frame.AsSpan(1));

                var outgoing = new Outgoing(OutgoingKind.Pooled, frame.AsMemory(0, size), frame);
                try
                {
                    await outbound.Writer.WriteAsync(outgoing, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    outgoing.Release();
                    throw;
                }
            }

            await outbound.Writer.WriteAsync(new Outgoing(OutgoingKind.Static, EndOfInputFrame, null), ct)
                .ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception ex)
        {
            // Tell the server its input broke, then fail the call with the caller's own exception —
            // it is the caller's enumerable that threw, and that is what the caller should see.
            outbound.Writer.TryWrite(new Outgoing(OutgoingKind.Static,
                IonStreamProtocol.ErrorFrame(new IonProtocolError("INPUT_FAULTED", ex.Message)), null));
            items.Writer.TryComplete(ex);
        }
    }

    // ── the end ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Stops a link that is lost before the next one starts: its loops share the session's state.</summary>
    private async Task StopLinkAsync(Link l, bool graceful = false)
    {
        lock (gate)
        {
            if (ReferenceEquals(link, l))
                link = null;
        }

        if (!graceful)
            AbortLink(l);
        await FinishOnceAsync(l, graceful).ConfigureAwait(false);
    }

    /// <summary>Releases a link once, however many ends reach it.</summary>
    private Task FinishOnceAsync(Link l, bool graceful)
    {
        lock (gate)
            return l.Finished ??= FinishLinkAsync(l, graceful);
    }

    /// <summary>
    /// Waits for the server's side of the close (when the end was graceful), then releases the link.
    /// Never throws.
    /// </summary>
    private async Task FinishLinkAsync(Link l, bool graceful)
    {
        await Task.Yield();

        if (graceful)
        {
            using var bound = new CancellationTokenSource(options.CloseTimeout > TimeSpan.Zero ? options.CloseTimeout : Timeout.InfiniteTimeSpan);
            try
            {
                if (l.Running && l.Send is not null)
                    await l.Send.WaitAsync(bound.Token).ConfigureAwait(false);
                else if (!l.Lost)
                    // No send loop ever ran (the server said goodbye during the handshake): this is the only writer.
                    await l.Transport.CloseOutputAsync("client closed", bound.Token).ConfigureAwait(false);
                await l.Receive.WaitAsync(bound.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The server did not finish its side in time.
            }
        }

        if (!l.Transport.IsClosed)
            AbortLink(l);

        try { l.Cts.Cancel(); }
        catch (AggregateException) { }
        l.WakeSend();

        foreach (var task in new[] { l.Receive, l.Send, l.Heartbeat })
            if (task is not null)
                try { await task.ConfigureAwait(false); } catch (Exception) { }

        await DisposeTransportAsync(l.Transport).ConfigureAwait(false);
    }

    /// <summary>The consumer left during the handshake: the handshake owns the transport, so it says goodbye itself.</summary>
    private async Task LeaveDirectAsync(Link l)
    {
        using var bound = new CancellationTokenSource(options.CloseTimeout > TimeSpan.Zero ? options.CloseTimeout : Timeout.InfiniteTimeSpan);
        try
        {
            await l.Transport.SendAsync(IonStreamProtocol.ClientCloseFrame(null), bound.Token).ConfigureAwait(false);
            await l.Transport.CloseOutputAsync("client closed", bound.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        await FinishOnceAsync(l, graceful: true).ConfigureAwait(false);
    }

    private static async Task DisposeTransportAsync(IonClientTransport transport)
    {
        try { await transport.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { }
    }

    /// <summary>Asks the running link, if there is one, to say goodbye; returns once that is on the wire.</summary>
    private async Task LeaveAsync()
    {
        Link? l;
        lock (gate)
        {
            leaving = true;
            l = link;
        }

        if (l is null || !l.Running || l.Terminal || l.Lost)
            return;

        l.LeaveRequested = true;
        outbound.Writer.TryWrite(new Outgoing(OutgoingKind.Wake, default, null));
        l.WakeSend();

        using var bound = new CancellationTokenSource(options.CloseTimeout > TimeSpan.Zero ? options.CloseTimeout : Timeout.InfiniteTimeSpan);
        try
        {
            await l.Send!.WaitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timed out or the transport is already gone; the background half aborts it.
        }

        _ = FinishOnceAsync(l, graceful: true);
    }

    /// <summary>
    /// Leaves gracefully when the server has not said goodbye — CLOSE, then the end of our side —
    /// and returns as soon as those are on the wire. Waiting for the server's side of the close is
    /// not the caller's business: a <c>break</c> or a cancellation returns at once, even while the
    /// server is busy (in a slow connect hook, say), and the rest finishes in the background within
    /// the close timeout. The server has what it needs to see a client that left on purpose.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
            return;

        // Leaving is decided before anything is cancelled: a handshake the cancellation interrupts
        // must know to say goodbye rather than drop the transport.
        lock (gate)
            leaving = true;

        await inputCts.CancelAsync().ConfigureAwait(false);
        await callCts.CancelAsync().ConfigureAwait(false);
        await LeaveAsync().ConfigureAwait(false);

        items.Writer.TryComplete();
        _ = CleanUpAsync();
    }

    private async Task CleanUpAsync()
    {
        try { await supervisor.ConfigureAwait(false); }
        catch (Exception) { }

        if (inputPump is not null)
            try { await inputPump.ConfigureAwait(false); } catch (Exception) { }

        // The newest link's loops may still be writing from the replay buffer.
        Link? newest;
        lock (gate)
            newest = lastLink;
        if (newest is not null)
            await FinishOnceAsync(newest, graceful: true).ConfigureAwait(false);

        outbound.Writer.TryComplete();
        while (outbound.Reader.TryRead(out var left))
            left.Release();
        pending?.Release();
        pending = null;
        replay?.Clear();

        callCts.Dispose();
        inputCts.Dispose();
    }

    // ── types ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One transport of the call, and the loops that run it.</summary>
    private sealed class Link(IonClientTransport transport, bool resuming)
    {
        private TaskCompletionSource? wake;

        public IonClientTransport Transport { get; } = transport;
        public bool Resuming { get; } = resuming;
        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource<IonStreamReady> Ready { get; } = Observed(new TaskCompletionSource<IonStreamReady>(TaskCreationOptions.RunContinuationsAsynchronously));
        public TaskCompletionSource<LinkEnd> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True when the send loop may start, false when the link will never run.</summary>
        public TaskCompletionSource<bool> Go { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Receive = Task.CompletedTask;
        public Task? Send;
        public Task? Heartbeat;
        public Task? Finished;              // under the call's gate
        public long LastReceivedAt = Environment.TickCount64;
        public long LastSentAt = Environment.TickCount64;
        public long KeepAliveMs;
        public long ServerTimeoutMs;
        public long ResumedCount;
        public volatile bool ReceiveParked;
        public volatile bool Running;
        public volatile bool Terminal;       // the server said goodbye on this link
        public volatile bool Lost;
        public volatile bool LeaveRequested;
        public volatile bool CloseRequested;
        public readonly byte[] AckScratch = new byte[IonStreamProtocol.MaxAckFrameSize];

        public Task ArmWake()
        {
            var w = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref wake, w);
            return w.Task;
        }

        public void WakeSend() => Interlocked.Exchange(ref wake, null)?.TrySetResult();

        /// <summary>A READY that fails after the handshake stopped waiting is nobody's error to observe.</summary>
        private static TaskCompletionSource<IonStreamReady> Observed(TaskCompletionSource<IonStreamReady> source)
        {
            source.Task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return source;
        }

        public void AdoptServerTimings(IonStreamReady announced, IonStreamClientOptions options)
        {
            var keepAlive = Millis(options.KeepAliveInterval);
            var serverTimeout = Millis(options.ServerTimeout);

            // Ping at least twice per server timeout, and give the server at least two of its own
            // keep-alive intervals before calling it dead.
            if (announced.ClientTimeout > TimeSpan.Zero && announced.ClientTimeout != Timeout.InfiniteTimeSpan)
            {
                var half = (long)announced.ClientTimeout.TotalMilliseconds / 2;
                keepAlive = keepAlive > 0 ? Math.Min(keepAlive, half) : half;
            }

            if (announced.KeepAliveInterval > TimeSpan.Zero && announced.KeepAliveInterval != Timeout.InfiniteTimeSpan && serverTimeout > 0)
            {
                var twice = (long)announced.KeepAliveInterval.TotalMilliseconds * 2;
                serverTimeout = Math.Max(serverTimeout, twice);
            }

            KeepAliveMs = keepAlive;
            ServerTimeoutMs = serverTimeout;
        }

        private static long Millis(TimeSpan value)
            => value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan ? 0 : (long)value.TotalMilliseconds;
    }

    private enum EndKind
    {
        Completed,
        Failed,
        Closed,
        Lost,
        Violated,
        NotResumable
    }

    /// <summary>How a link ended.</summary>
    private sealed record LinkEnd(EndKind Kind, Exception? Exception)
    {
        public static readonly LinkEnd Completed = new(EndKind.Completed, null);

        public static readonly LinkEnd NotResumable = new(EndKind.NotResumable, new IonStreamNotResumableException());

        public static LinkEnd Failed(Exception e) => new(EndKind.Failed, e);

        public static LinkEnd Closed(IonStreamClosedException e) => new(EndKind.Closed, e);

        public static LinkEnd Lost(Exception e) => new(EndKind.Lost, e);

        public static LinkEnd Violated(Exception e) => new(EndKind.Violated, e);
    }

    private enum OutgoingKind : byte
    {
        Static,
        Pooled,
        Ping,
        Ack,
        Wake
    }

    private readonly struct Outgoing(OutgoingKind kind, ReadOnlyMemory<byte> frame, byte[]? pooled)
    {
        public OutgoingKind Kind { get; } = kind;
        public ReadOnlyMemory<byte> Frame { get; } = frame;
        public byte[]? PooledArray { get; } = pooled;

        public void Release()
        {
            if (PooledArray is not null)
                ArrayPool<byte>.Shared.Return(PooledArray);
        }
    }

    /// <summary>
    /// The client's reliable frames sent and not yet acknowledged, oldest first. A pooled input frame
    /// is kept as it is — the buffer changes owner, from the queue to the replay — and returned to the
    /// pool once acknowledged. One send loop at a time touches it, or a handshake while none runs.
    /// </summary>
    private sealed class ReplayBuffer
    {
        private Entry[] ring = new Entry[64];
        private int head;

        public int Count { get; private set; }
        public long Bytes { get; private set; }
        public long FirstSeq { get; private set; } = 1;
        public long NextSeq => FirstSeq + Count;

        public ReadOnlyMemory<byte> this[int index] => ring[(head + index) % ring.Length].Frame;

        public ReadOnlyMemory<byte> Append(ReadOnlyMemory<byte> frame, byte[]? pooled)
        {
            if (Count == ring.Length)
            {
                var grown = new Entry[ring.Length * 2];
                for (var i = 0; i < Count; i++)
                    grown[i] = ring[(head + i) % ring.Length];
                ring = grown;
                head = 0;
            }

            ring[(head + Count) % ring.Length] = new Entry(frame, pooled);
            Count++;
            Bytes += frame.Length;
            return frame;
        }

        public void TrimTo(long acknowledged)
        {
            while (Count > 0 && FirstSeq <= acknowledged)
            {
                ref var e = ref ring[head];
                Bytes -= e.Frame.Length;
                if (e.Pooled is not null)
                    ArrayPool<byte>.Shared.Return(e.Pooled);
                e = default;
                head = (head + 1) % ring.Length;
                Count--;
                FirstSeq++;
            }
        }

        /// <summary>Lets go of everything, and numbers from 1 again — for a session that is gone.</summary>
        public void Clear()
        {
            TrimTo(long.MaxValue);
            FirstSeq = 1;
        }

        private readonly record struct Entry(ReadOnlyMemory<byte> Frame, byte[]? Pooled);
    }
}

/// <summary>The server refused to resume the session: it is gone, and the call must start afresh.</summary>
internal sealed class IonStreamNotResumableException()
    : IonRequestException(new IonProtocolError(IonStreamProtocol.NotResumableCode, "The stream session cannot be resumed."), (Exception?)null);
