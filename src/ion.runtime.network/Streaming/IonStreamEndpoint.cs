namespace ion.runtime.network;

using System.Diagnostics;
using System.Formats.Cbor;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// <c>/ion/{interface}/{method}.ws</c> and <c>.wt</c>: everything between the upgrade request —
/// a WebSocket upgrade or a WebTransport extended CONNECT — and a running <see cref="IonStreamConnection"/>.
/// </summary>
/// <remarks>
/// Both paths accept both transports; the request says which one it is. A WebTransport client
/// cannot set sub-protocols or headers from a browser, so it passes
/// <c>?ticket=&lt;base56&gt;&amp;ver=2&amp;sid=…&amp;cid=…</c> in the query string instead.
/// </remarks>
internal static class IonStreamEndpoint
{
    public static async Task HandleAsync(HttpContext http, string interfaceName, string methodName)
    {
        var services = http.RequestServices;
        var store = services.GetRequiredService<IonDescriptorStorage>();
        var transport = services.GetRequiredService<IOptions<IonTransportOptions>>().Value;
        var options = services.GetRequiredService<IOptions<IonStreamOptions>>().Value;
        var manager = services.GetRequiredService<IonStreamConnectionManager>();
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Ion.Streams");

        var endpoint = $"{interfaceName}/{methodName}";
        var sw = Stopwatch.StartNew();

        var webTransport = http.Features.Get<IHttpWebTransportFeature>();
        var isWebTransport = webTransport?.IsWebTransportRequest == true;
        var metric = isWebTransport ? "wt" : "ws";

        IonInstruments.IncrementActiveConnections(metric);
        try
        {
            // Browsers cannot set headers on an upgrade, so the query string stands in for them.
            var sessionId = http.Request.Headers[IonCorrelationHeaders.SessionId].FirstOrDefault()
                            ?? http.Request.Query["sid"].FirstOrDefault();
            var correlationId = http.Request.Headers[IonCorrelationHeaders.CorrelationId].FirstOrDefault()
                                ?? http.Request.Query["cid"].FirstOrDefault();
            if (string.IsNullOrEmpty(correlationId) && transport.GenerateCorrelationIdIfMissing)
                correlationId = Guid.NewGuid().ToString("N");

            if (!string.IsNullOrEmpty(correlationId))
                http.Response.Headers.Append(IonCorrelationHeaders.CorrelationId, correlationId);
            if (!string.IsNullOrEmpty(sessionId))
                http.Response.Headers.Append(IonCorrelationHeaders.SessionId, sessionId);

            using var logScope = log.BeginScope(new Dictionary<string, object?>
            {
                ["SessionId"] = sessionId,
                ["CorrelationId"] = correlationId
            });

            if (!isWebTransport && !http.WebSockets.IsWebSocketRequest)
            {
                await RefuseAsync(http, log, metric, endpoint, sw, StatusCodes.Status412PreconditionFailed,
                    "UNSUPPORTED_TRANSPORT", "Transport must be WebSocket or WebTransport");
                return;
            }

            if (!store.IsServiceAllowedOnPort(interfaceName, http.Connection.LocalPort))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                Record(metric, endpoint, sw, http.Response.StatusCode, null);
                return;
            }

            var @interface = store.GetTransportInterface(interfaceName);
            var method = store.GetTransportMethod(interfaceName, methodName);
            var elementType = IonStreamWire.StreamElementType(method);

            if (@interface is null || method is null || elementType is null)
            {
                await RefuseAsync(http, log, metric, endpoint, sw, StatusCodes.Status412PreconditionFailed,
                    "ENTRYPOINT_NOT_FOUND", $"Method {methodName} is not a stream of {interfaceName}");
                return;
            }

            string? subProtocol = null;
            string? ticketText;
            int version;

            if (isWebTransport)
            {
                version = int.TryParse(http.Request.Query["ver"].FirstOrDefault(), out var v) ? v : 0;
                ticketText = http.Request.Query["ticket"].FirstOrDefault();
            }
            else
            {
                subProtocol = SelectSubProtocol(http.WebSockets.WebSocketRequestedProtocols);
                version = IonStreamProtocol.ParseVersion(subProtocol);
                ticketText = subProtocol;
            }

            if (version != IonStreamProtocol.Version)
            {
                await RefuseAsync(http, log, metric, endpoint, sw, StatusCodes.Status426UpgradeRequired,
                    "UNSUPPORTED_PROTOCOL_VERSION",
                    $"This server speaks Ion stream protocol v{IonStreamProtocol.Version}; the client offered " +
                    (version == 0 ? "none" : $"v{version}"));
                return;
            }

            var scope = services.CreateAsyncScope();
            IonStreamConnection? connection = null;
            try
            {
                var router = store.GetStreamRouter(interfaceName, scope);
                if (router is null)
                {
                    await RefuseAsync(http, log, metric, endpoint, sw, StatusCodes.Status412PreconditionFailed,
                        "ENTRYPOINT_NOT_FOUND", $"Method {methodName} is not server-streaming");
                    return;
                }

                var ticketExchange = scope.ServiceProvider.GetService<IIonTicketExchange>();

                var ticket = string.IsNullOrEmpty(ticketText)
                    ? null
                    : isWebTransport
                        ? IonTicketExtractor.DecodeTicket(ticketText)
                        : IonTicketExtractor.ExtractTicketBytes(ticketText);

                if (ticket is null && ticketExchange is not null)
                {
                    await RefuseAsync(http, log, metric, endpoint, sw, StatusCodes.Status412PreconditionFailed,
                        "TICKET_BROKEN", "Transport ticket has been broken");
                    return;
                }

                object? ticketData = null;
                if (ticketExchange is not null)
                {
                    var (error, data) = await ticketExchange.OnExchangeTransactionAsync(ticket!.Value).ConfigureAwait(false);
                    if (error is not null)
                    {
                        // 401, not the neighbouring 412: the ticket parsed fine, it was refused.
                        log.LogWarning("Ticket refused for {Endpoint}: {Error}", endpoint, error);
                        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await WriteErrorAsync(http.Response, error.Value);
                        Record(metric, endpoint, sw, http.Response.StatusCode, error.Value.code);
                        return;
                    }

                    ticketData = data;
                }

                await using var streamTransport = isWebTransport
                    ? await AcceptWebTransportAsync(webTransport!, options, http.RequestAborted, log, endpoint).ConfigureAwait(false)
                    : await AcceptWebSocketAsync(http, subProtocol, options).ConfigureAwait(false);

                if (streamTransport is null)
                {
                    Record(metric, endpoint, sw, StatusCodes.Status400BadRequest, "HANDSHAKE_FAILED");
                    return;
                }

                var args = await ReceiveArgumentsAsync(streamTransport, options, http.RequestAborted, log, endpoint)
                    .ConfigureAwait(false);
                if (args is null)
                {
                    Record(metric, endpoint, sw, StatusCodes.Status400BadRequest, "HANDSHAKE_FAILED");
                    return;
                }

                // An argument message is a CBOR array; a first byte that is an opcode instead is a
                // client coming back to a session it lost the transport of.
                if (args[0] == IonStreamProtocol.OpResume)
                {
                    var outcome = await ResumeAsync(http, streamTransport, @interface, method, args, options, log, endpoint)
                        .ConfigureAwait(false);
                    Record(metric, endpoint, sw, StatusFor(outcome), outcome == IonResumeOutcome.Resumed ? null : outcome.ToString());
                    return;
                }

                ticketExchange?.OnTicketApply(ticketData!);

                var resumable = options.ResumeWindow > TimeSpan.Zero &&
                                http.Request.Query[IonStreamProtocol.ResumeQueryParameter].FirstOrDefault() == "1";

                connection = new IonStreamConnection(
                    streamTransport, http, scope.ServiceProvider, @interface, method, elementType,
                    router.IsAllowInputStream(methodName), ticketData, sessionId, correlationId,
                    options, manager, log, transport.DetailedErrors,
                    resumable ? services.GetRequiredService<IonStreamResumeRegistry>() : null);

                if (scope.ServiceProvider.GetService<IIonStreamContextAccessor>() is IonStreamContextHolder holder)
                    holder.Context = connection;

                await connection.RunAsync(ResolveHooks(scope.ServiceProvider, transport, @interface), router, methodName, args)
                    .ConfigureAwait(false);

                var info = connection.Disconnect!;
                Record(metric, endpoint, sw, StatusFor(info), info.IsGraceful ? null : info.Reason.ToString());
                IonInstruments.RecordStreamDisconnect(endpoint, info.Reason.ToString());
            }
            finally
            {
                // A stream method that ignored its cancellation token is still running with services
                // from this scope; disposing them under it would turn a warning into a crash.
                if (connection?.StreamTask is { IsCompleted: false } lingering)
                    _ = DisposeAfterAsync(lingering, scope);
                else
                    await scope.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            IonInstruments.DecrementActiveConnections(metric);
        }
    }

    /// <summary>
    /// Hands a resuming client's transport to its session, and runs it there until it is lost or the
    /// session ends. A session that cannot be resumed is refused with <see cref="IonStreamProtocol.NotResumableCode"/>,
    /// so the client knows to start the call afresh rather than try again.
    /// </summary>
    /// <remarks>
    /// The ticket was checked like any other — a user signed out meanwhile does not get back in — but
    /// it is not applied: the session keeps the scope, the ticket and the principal it started with.
    /// A principal with an identity of its own must be the same one.
    /// </remarks>
    private static async Task<IonResumeOutcome> ResumeAsync(HttpContext http, IonStreamTransport transport, Type @interface,
        MethodInfo method, byte[] frame, IonStreamOptions options, ILogger log, string endpoint)
    {
        string token;
        long received;
        try
        {
            (token, received) = IonStreamProtocol.ReadResume(frame.AsMemory(1));
        }
        catch (Exception ex) when (ex is CborContentException or FormatException or OverflowException)
        {
            log.LogWarning("Resume of {Endpoint} refused: a malformed RESUME ({Message})", endpoint, ex.Message);
            await RefuseResumeAsync(transport, "PROTOCOL_VIOLATION", $"A malformed RESUME: {ex.Message}",
                WebSocketCloseStatus.ProtocolError, options).ConfigureAwait(false);
            IonInstruments.RecordStreamResume(endpoint, "violation");
            return IonResumeOutcome.Violation;
        }

        var registry = http.RequestServices.GetRequiredService<IonStreamResumeRegistry>();
        var session = registry.Find(token);
        var outcome = IonResumeOutcome.NotResumable;

        if (session is null)
        {
            log.LogInformation("Resume of {Endpoint} refused: no such session (it ended, or its window ran out)", endpoint);
        }
        else if (session.Interface != @interface || session.Method != method)
        {
            log.LogWarning("Resume of {Endpoint} refused: the session {ConnectionId} belongs to {Interface}.{Method}",
                endpoint, session.ConnectionId, session.Interface.Name, session.Method.Name);
        }
        else if (session.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } owner &&
                 owner != http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value)
        {
            log.LogWarning("Resume of {Endpoint} refused: session {ConnectionId} belongs to another user", endpoint, session.ConnectionId);
        }
        else
        {
            outcome = await session.ResumeAsync(transport, http, received).ConfigureAwait(false);
        }

        switch (outcome)
        {
            case IonResumeOutcome.NotResumable:
                await RefuseResumeAsync(transport, IonStreamProtocol.NotResumableCode,
                    "The stream session cannot be resumed; start the call again.", WebSocketCloseStatus.NormalClosure, options)
                    .ConfigureAwait(false);
                IonInstruments.RecordStreamResume(endpoint, "not_resumable");
                break;
            case IonResumeOutcome.Violation:
                await RefuseResumeAsync(transport, "PROTOCOL_VIOLATION",
                    "RESUME does not match what the server sent.", WebSocketCloseStatus.ProtocolError, options)
                    .ConfigureAwait(false);
                IonInstruments.RecordStreamResume(endpoint, "violation");
                break;
            case IonResumeOutcome.Superseded:
                transport.Abort();
                IonInstruments.RecordStreamResume(endpoint, "superseded");
                break;
            default:
                IonInstruments.RecordStreamResume(endpoint, outcome == IonResumeOutcome.Resumed ? "resumed" : "lost");
                break;
        }

        return outcome;
    }

    private static async Task RefuseResumeAsync(IonStreamTransport transport, string code, string message,
        WebSocketCloseStatus status, IonStreamOptions options)
    {
        using (var bound = options.CloseTimeout > TimeSpan.Zero ? new CancellationTokenSource(options.CloseTimeout) : new CancellationTokenSource())
        {
            try
            {
                await transport.SendAsync(IonStreamProtocol.ErrorFrame(new IonProtocolError(code, message)), bound.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                transport.Abort();
                return;
            }
        }

        await CloseQuietlyAsync(transport, status, code == IonStreamProtocol.NotResumableCode ? "not resumable" : "protocol violation", options)
            .ConfigureAwait(false);
    }

    private static int StatusFor(IonResumeOutcome outcome) => outcome switch
    {
        IonResumeOutcome.Resumed or IonResumeOutcome.Superseded or IonResumeOutcome.Lost => StatusCodes.Status200OK,
        IonResumeOutcome.Violation => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status410Gone
    };

    /// <summary>
    /// The offered <c>ion*</c> sub-protocol naming the version this server speaks; failing that, the
    /// first <c>ion*</c> one (so the refusal can say what was offered); null when there is none.
    /// </summary>
    internal static string? SelectSubProtocol(IList<string> offered)
    {
        string? first = null;

        foreach (var candidate in offered)
        {
            var version = IonStreamProtocol.ParseVersion(candidate);
            if (version == IonStreamProtocol.Version)
                return candidate;
            if (version != 0)
                first ??= candidate;
        }

        return first;
    }

    private static async Task<IonStreamTransport?> AcceptWebSocketAsync(HttpContext http, string? subProtocol, IonStreamOptions options)
    {
        var accept = new WebSocketAcceptContext { SubProtocol = subProtocol };
        if (options.KeepAliveInterval > TimeSpan.Zero)
            accept.KeepAliveInterval = options.KeepAliveInterval;
        if (options.TransportKeepAliveTimeout is { } transportTimeout)
            accept.KeepAliveTimeout = transportTimeout;

        var ws = await http.WebSockets.AcceptWebSocketAsync(accept).ConfigureAwait(false);
        return new WebSocketStreamTransport(ws);
    }

    /// <summary>Accepts the session, then waits for the client's one bidirectional stream.</summary>
    private static async Task<IonStreamTransport?> AcceptWebTransportAsync(
        IHttpWebTransportFeature feature, IonStreamOptions options, CancellationToken requestAborted, ILogger log, string endpoint)
    {
        var session = await feature.AcceptAsync(requestAborted).ConfigureAwait(false);

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        if (options.HandshakeTimeout > TimeSpan.Zero)
            bound.CancelAfter(options.HandshakeTimeout);

        try
        {
            while (true)
            {
                var stream = await session.AcceptStreamAsync(bound.Token).ConfigureAwait(false);
                if (stream is null)
                {
                    log.LogInformation("WebTransport session for {Endpoint} ended before it opened a stream", endpoint);
                    return null;
                }

                var direction = stream.Features.Get<IStreamDirectionFeature>();
                if (direction is { CanRead: true, CanWrite: true })
                    return new WebTransportStreamTransport(session, stream);

                // A unidirectional stream is not how an Ion call starts.
                stream.Abort(new ConnectionAbortedException("Ion streams run on one bidirectional stream."));
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            log.LogWarning("WebTransport handshake of {Endpoint} timed out after {Timeout}", endpoint, options.HandshakeTimeout);
            session.Abort(0x100);
            return null;
        }
    }

    private static IReadOnlyList<IIonStreamLifecycle> ResolveHooks(IServiceProvider services, IonTransportOptions transport, Type @interface)
    {
        var hooks = new List<IIonStreamLifecycle>(transport.StreamLifecycles.Count + 1);

        foreach (var type in transport.StreamLifecycles)
            if (services.GetService(type) is IIonStreamLifecycle hook)
                hooks.Add(hook);

        // The same scoped instance the generated executor will resolve and call the stream method on.
        if (services.GetService(@interface) is IIonStreamLifecycle serviceHook)
            hooks.Add(serviceHook);

        return hooks;
    }

    /// <summary>Reads the argument frame, or ends the transport and returns null when the client does not send one.</summary>
    private static async Task<byte[]?> ReceiveArgumentsAsync(
        IonStreamTransport transport, IonStreamOptions options, CancellationToken requestAborted, ILogger log, string endpoint)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        if (options.HandshakeTimeout > TimeSpan.Zero)
            bound.CancelAfter(options.HandshakeTimeout);

        try
        {
            var frame = await transport.ReceiveAsync(options.MaxReceiveMessageSize, bound.Token).ConfigureAwait(false);

            if (frame.IsClose)
            {
                await CloseQuietlyAsync(transport, WebSocketCloseStatus.NormalClosure, "ack", options).ConfigureAwait(false);
                return null;
            }

            if (!frame.IsBinary || frame.Payload.IsEmpty)
            {
                log.LogWarning("Handshake of {Endpoint} failed: expected a binary argument message", endpoint);
                await CloseQuietlyAsync(transport, WebSocketCloseStatus.PolicyViolation, "expected the binary argument message", options)
                    .ConfigureAwait(false);
                return null;
            }

            // The one copy per connection: the arguments outlive the transport's receive buffer.
            return frame.Payload.ToArray();
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            // Cancelling the receive aborted the transport; there is no close handshake left to run.
            log.LogWarning("Handshake of {Endpoint} timed out after {Timeout}", endpoint, options.HandshakeTimeout);
            transport.Abort();
            return null;
        }
        catch (IonStreamMessageTooLargeException)
        {
            log.LogWarning("Handshake of {Endpoint} failed: the argument message exceeded {Limit} bytes",
                endpoint, options.MaxReceiveMessageSize);
            await CloseQuietlyAsync(transport, WebSocketCloseStatus.MessageTooBig, "argument message too large", options)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            log.LogInformation(ex, "Handshake of {Endpoint} failed: the client went away", endpoint);
            transport.Abort();
            return null;
        }
    }

    /// <summary>Ends our side and gives the client a moment to end its own; aborts if it does not.</summary>
    private static async Task CloseQuietlyAsync(IonStreamTransport transport, WebSocketCloseStatus status, string description, IonStreamOptions options)
    {
        using var bound = options.CloseTimeout > TimeSpan.Zero
            ? new CancellationTokenSource(options.CloseTimeout)
            : new CancellationTokenSource();
        try
        {
            await transport.CloseOutputAsync(status, description, bound.Token).ConfigureAwait(false);
            while (!transport.IsClosed)
            {
                var frame = await transport.ReceiveAsync(options.MaxReceiveMessageSize, bound.Token).ConfigureAwait(false);
                if (frame.IsClose)
                    break;
            }
        }
        catch (Exception)
        {
        }

        if (!transport.IsClosed)
            transport.Abort();
    }

    private static async Task DisposeAfterAsync(Task lingering, AsyncServiceScope scope)
    {
        try { await lingering.ConfigureAwait(false); }
        catch (Exception) { }
        await scope.DisposeAsync().ConfigureAwait(false);
    }

    private static int StatusFor(IonDisconnectInfo info) => info.Reason switch
    {
        IonDisconnectReason.Completed or IonDisconnectReason.ServerClosed or IonDisconnectReason.ServerShutdown
            => StatusCodes.Status200OK,
        IonDisconnectReason.ClientClosed or IonDisconnectReason.TransportLost
            => StatusCodes.Status499ClientClosedRequest,
        IonDisconnectReason.Rejected => StatusCodes.Status403Forbidden,
        IonDisconnectReason.ProtocolViolation => StatusCodes.Status400BadRequest,
        IonDisconnectReason.Timeout => StatusCodes.Status408RequestTimeout,
        _ => StatusCodes.Status500InternalServerError
    };

    private static async Task RefuseAsync(HttpContext http, ILogger log, string metric, string endpoint, Stopwatch sw, int status, string code, string message)
    {
        log.LogWarning("Stream {Endpoint} refused: {Code} {Message}", endpoint, code, message);
        http.Response.StatusCode = status;
        await WriteErrorAsync(http.Response, new IonProtocolError(code, message));
        Record(metric, endpoint, sw, status, code);
    }

    private static async Task WriteErrorAsync(HttpResponse resp, IonProtocolError error)
    {
        resp.ContentType = RpcEndpoints.IonContentType;
        resp.Headers.Append(RpcEndpoints.IonStatusCode, error.code);
        await IonBinarySerializer.SerializeAsync(error, async memory => { await resp.BodyWriter.WriteAsync(memory); });
    }

    private static void Record(string metric, string endpoint, Stopwatch sw, int status, string? errorCode)
    {
        sw.Stop();
        IonInstruments.RecordRequest(metric, endpoint, status);
        IonInstruments.RecordRequestDuration(metric, endpoint, sw.Elapsed.TotalMilliseconds);
        if (errorCode is not null)
            IonInstruments.RecordError(metric, endpoint, errorCode);
    }
}
