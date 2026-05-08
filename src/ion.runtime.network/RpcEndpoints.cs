namespace ion.runtime.network;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Buffers;
using System.Formats.Cbor;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using ion.runtime;

public static class RpcEndpoints
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddIonRequestTerminator<T>()
            where T : class, IIonRequestTerminator
        {
            services.AddSingleton<IIonRequestTerminator, T>();
            return services;
        }

        public IServiceCollection AddIonProtocol(Action<IIonTransportRegistration> onRegistration)
        {
            services.Configure<IonTransportOptions>(_ => { });
            services.AddSingleton<IonDescriptorStorage>();
            services.AddSingleton<IonRequestTerminatorStorage>();
            var reg = new IonDescriptorRegistration(services);
            onRegistration(reg);

            if (reg.BoundPorts.Count > 0)
            {
                var registry = new IonPortBindingRegistry();
                foreach (var port in reg.BoundPorts)
                    registry.Add(port);
                services.AddSingleton(registry); // registered as instance for UseIonPorts() access
            }

            return services;
        }

        internal IServiceCollection IonWithSubProtocolTicketExchange<T>()
            where T : class, IIonTicketExchange
        {
            services.AddScoped<IIonTicketExchange, T>();
            services.Configure<IonTransportOptions>(x =>
            {
                x.WebSocketOptions.Flow = IonWebSocketAuthFlow.SubProtocol;
                x.WebSocketOptions.TicketExchangeHandle = typeof(T);
            });
            return services;
        }

        public IServiceCollection AddIonService<TInterface, TImplementation>(int? port = null)
            where TInterface : class, IIonService
            where TImplementation : class, TInterface
        {
            services.AddScoped<TInterface, TImplementation>();
            services.Configure<IonTransportOptions>(options =>
            {
                options.Services.Add(typeof(TInterface), typeof(TImplementation));
                if (port.HasValue)
                    options.PortBindings[typeof(TInterface)] = port.Value;
            });
            return services;
        }

        public IServiceCollection AddIonInterceptor<TImplementation>()
            where TImplementation : class, IIonInterceptor
        {
            services.AddScoped<IIonInterceptor, TImplementation>();
            services.Configure<IonTransportOptions>(options =>
                options.Interceptors.Add(typeof(TImplementation)));
            return services;
        }
    }


    public const string HeaderDeadlineMs = "X-Deadline";
    public const string IonContentType = "application/ion";
    public const string IonContentTypeOutput = "application/ion; charset=binary; ver=1";

    public const string IonStatusCode = "X-Ion-Status";
    public const string SubProtocolTemplate = "ion; ticket={ticket}; ver=1";

    static class IonWs
    {
        public const byte OPCODE_DATA = 0x00;
        public const byte OPCODE_END = 0x01;
        public const byte OPCODE_ERROR = 0x02;
    }

    // Cached opcode frames to avoid allocations
    private static readonly byte[] OpcodeDataFrame = [IonWs.OPCODE_DATA];
    private static readonly byte[] OpcodeEndFrame = [IonWs.OPCODE_END];
    private static readonly byte[] OpcodeErrorFrame = [IonWs.OPCODE_ERROR];

    private static void ExtractCorrelation(HttpRequest req, HttpResponse resp, ServerSideCallContext callCtx, IonTransportOptions options)
    {
        var sessionId = req.Headers[IonCorrelationHeaders.SessionId].FirstOrDefault();
        if (!string.IsNullOrEmpty(sessionId))
            callCtx.SessionId = sessionId;

        var correlationId = req.Headers[IonCorrelationHeaders.CorrelationId].FirstOrDefault();
        if (string.IsNullOrEmpty(correlationId) && options.GenerateCorrelationIdIfMissing)
            correlationId = Guid.NewGuid().ToString("N");

        if (!string.IsNullOrEmpty(correlationId))
        {
            callCtx.CorrelationId = correlationId;
            resp.Headers.Append(IonCorrelationHeaders.CorrelationId, correlationId);
        }

        if (!string.IsNullOrEmpty(sessionId))
            resp.Headers.Append(IonCorrelationHeaders.SessionId, sessionId);
    }

    private static IDisposable? BeginCorrelationScope(ILogger logger, ServerSideCallContext callCtx)
    {
        return logger.BeginScope(new Dictionary<string, object?>
        {
            ["SessionId"] = callCtx.SessionId,
            ["CorrelationId"] = callCtx.CorrelationId
        });
    }

    public static IEndpointRouteBuilder MapRpcEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map("/ion.att", async (HttpContext http,
                [FromServices] IOptions<IonTransportOptions> transportOptions,
                [FromServices] IEnumerable<IIonInterceptor> interceptors,
                [FromServices] IServiceProvider provider,
                [FromServices] ILoggerFactory lf,
                CancellationToken ct
            ) =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var log = lf.CreateLogger("RPC");
                var req = http.Request;
                var resp = http.Response;

                if (req.ContentType is null ||
                    !req.ContentType.StartsWith(IonContentType, StringComparison.OrdinalIgnoreCase))
                {
                    resp.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                    await WriteError(log, resp, "UNSUPPORTED_MEDIA", $"Content-Type must be {IonContentType}");
                    sw.Stop();
                    IonInstruments.RecordRequest("att", "exchange", resp.StatusCode);
                    IonInstruments.RecordRequestDuration("att", "exchange", sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("att", "exchange", "UNSUPPORTED_MEDIA");
                    return;
                }

                await using var scope = provider.CreateAsyncScope();
                using var callCtx =
                    new ServerSideCallContext(scope, typeof(__internal_ion), __internal_ion.__exchange_ref);

                foreach (var header in req.Headers)
                    callCtx.RequestItems.Add(header.Key, header.Value.ToString());

                ExtractCorrelation(req, resp, callCtx, transportOptions.Value);
                using var logScope = BeginCorrelationScope(log, callCtx);

                var writer = new CborWriter();

                async Task TerminalAsync(IIonCallContext c, CancellationToken cancellationToken)
                {
                    var exchanger = c.ServiceProvider.GetService<IIonTicketExchange>();

                    if (exchanger is null)
                    {
                        writer.WriteStartArray(1);
                        writer.WriteByteString([0]);
                        writer.WriteEndArray();
                    }
                    else
                    {
                        var token = await exchanger.OnExchangeCreateAsync(c);

                        writer.WriteStartArray(1);
                        writer.WriteByteString(token.Span);
                        writer.WriteEndArray();
                    }

                    resp.StatusCode = StatusCodes.Status200OK;
                    resp.ContentType = IonContentType;

                    foreach (var (k, v) in c.ResponseItems)
                        resp.Headers.Append(k, v);


                    await resp.BodyWriter.WriteAsync(writer.Encode(), cancellationToken);
                    await resp.BodyWriter.FlushAsync(cancellationToken);
                }

                try
                {
                    var next = TerminalAsync;

                    var array = interceptors.ToArray();
                    for (var i = array.Length - 1; i >= 0; i--)
                    {
                        var interceptor = array[i];
                        var currentNext = next;
                        next = (c, token) => interceptor.InvokeAsync(c, currentNext, token);
                    }

                    await next(callCtx, ct).ConfigureAwait(true);

                    sw.Stop();
                    IonInstruments.RecordRequest("att", "exchange", resp.StatusCode);
                    IonInstruments.RecordRequestDuration("att", "exchange", sw.Elapsed.TotalMilliseconds);
                }
                catch (IonRequestException ionException)
                {
                    resp.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteError(log, resp, ionException.Error.code, ionException.Error.msg);
                    sw.Stop();
                    IonInstruments.RecordRequest("att", "exchange", resp.StatusCode);
                    IonInstruments.RecordRequestDuration("att", "exchange", sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("att", "exchange", ionException.Error.code);
                }
                catch (OperationCanceledException)
                {
                    resp.StatusCode = StatusCodes.Status504GatewayTimeout;
                    await WriteError(resp, IonProtocolError.DEADLINE_EXCEEDED());
                    sw.Stop();
                    IonInstruments.RecordRequest("att", "exchange", resp.StatusCode);
                    IonInstruments.RecordRequestDuration("att", "exchange", sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("att", "exchange", "DEADLINE_EXCEEDED");
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "handler failed");
                    resp.StatusCode = StatusCodes.Status500InternalServerError;
                    var sanitized = IonErrorSanitizer.Sanitize(ex, transportOptions.Value.DetailedErrors);
                    await WriteError(log, resp, sanitized.code, sanitized.msg);
                    sw.Stop();
                    IonInstruments.RecordRequest("att", "exchange", resp.StatusCode);
                    IonInstruments.RecordRequestDuration("att", "exchange", sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("att", "exchange", "INTERNAL_ERROR");
                }
            })
            .WithMetadata(new ConsumesAttribute(IonContentType))
            .Produces(StatusCodes.Status200OK, contentType: IonContentType)
            .Produces(StatusCodes.Status400BadRequest, contentType: IonContentType)
            .Produces(StatusCodes.Status409Conflict, contentType: IonContentType)
            .Produces(StatusCodes.Status415UnsupportedMediaType, contentType: IonContentType)
            .Produces(StatusCodes.Status500InternalServerError, contentType: IonContentType);
        ;

        app.Map("/ion/{interfaceName}/{methodName}.ws", async (HttpContext http,
            string interfaceName,
            string methodName,
            [FromServices] IonDescriptorStorage store,
            [FromServices] IServiceProvider provider,
            [FromServices] IOptions<IonTransportOptions> transportOptions,
            [FromServices] ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var log = lf.CreateLogger("RPC.WS");
            var endpoint = $"{interfaceName}/{methodName}";

            IonInstruments.IncrementActiveConnections("ws");

            try
            {
                // Extract correlation from HTTP upgrade request headers (with query param fallback for WebSocket)
                var sessionId = http.Request.Headers[IonCorrelationHeaders.SessionId].FirstOrDefault()
                    ?? http.Request.Query["sid"].FirstOrDefault();
                var correlationId = http.Request.Headers[IonCorrelationHeaders.CorrelationId].FirstOrDefault()
                    ?? http.Request.Query["cid"].FirstOrDefault();
                if (string.IsNullOrEmpty(correlationId) && transportOptions.Value.GenerateCorrelationIdIfMissing)
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

                await using var scope = provider.CreateAsyncScope();
                var router = store.GetStreamRouter(interfaceName, scope);
                var ticketExchange = provider.GetService<IIonTicketExchange>();

                if (!http.WebSockets.IsWebSocketRequest)
                {
                    log.LogWarning("UNSUPPORTED_TRANSPORT");
                    http.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                    await WriteError(log, http.Response, "UNSUPPORTED_TRANSPORT", $"Transport must be WebSocket");
                    sw.Stop();
                    IonInstruments.RecordRequest("ws", endpoint, http.Response.StatusCode);
                    IonInstruments.RecordRequestDuration("ws", endpoint, sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("ws", endpoint, "UNSUPPORTED_TRANSPORT");
                    return;
                }

                if (!store.IsServiceAllowedOnPort(interfaceName, http.Connection.LocalPort))
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    sw.Stop();
                    IonInstruments.RecordRequest("ws", endpoint, http.Response.StatusCode);
                    IonInstruments.RecordRequestDuration("ws", endpoint, sw.Elapsed.TotalMilliseconds);
                    return;
                }

                if (router is null)
                {
                    log.LogWarning("ENTRYPOINT_NOT_FOUND");
                    http.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                    await WriteError(log, http.Response, "ENTRYPOINT_NOT_FOUND",
                        $"Method {methodName} is not server-streaming");
                    sw.Stop();
                    IonInstruments.RecordRequest("ws", endpoint, http.Response.StatusCode);
                    IonInstruments.RecordRequestDuration("ws", endpoint, sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("ws", endpoint, "ENTRYPOINT_NOT_FOUND");
                    return;
                }

                var subProtocol = http.WebSockets.WebSocketRequestedProtocols.FirstOrDefault(x => x.StartsWith("ion"));

                if (string.IsNullOrEmpty(subProtocol) && ticketExchange is not null)
                {
                    log.LogWarning("UNSUPPORTED_SUB_PROTOCOL");
                    http.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                    await WriteError(log, http.Response, "UNSUPPORTED_SUB_PROTOCOL",
                        $"Transport sub-protocol must be ion");
                    sw.Stop();
                    IonInstruments.RecordRequest("ws", endpoint, http.Response.StatusCode);
                    IonInstruments.RecordRequestDuration("ws", endpoint, sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("ws", endpoint, "UNSUPPORTED_SUB_PROTOCOL");
                    return;
                }

                var ticket = string.IsNullOrEmpty(subProtocol)
                    ? null
                    : IonTicketExtractor.ExtractTicketBytes(subProtocol);

                if (ticket is null && ticketExchange is not null)
                {
                    log.LogWarning("TICKET_BROKEN");
                    http.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                    await WriteError(log, http.Response, "TICKET_BROKEN", $"Transport ticket has been broken");
                    sw.Stop();
                    IonInstruments.RecordRequest("ws", endpoint, http.Response.StatusCode);
                    IonInstruments.RecordRequestDuration("ws", endpoint, sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("ws", endpoint, "TICKET_BROKEN");
                    return;
                }

                object? ticketData = null;

                if (ticketExchange is not null)
                {
                    var (error, t) =
                        await ticketExchange.OnExchangeTransactionAsync(ticket.Value).ConfigureAwait(true);
                    ticketData = t;
                    if (error is not null)
                    {
                        log.LogWarning(error.ToString());
                        await WriteError(http.Response, error.Value);
                        sw.Stop();
                        IonInstruments.RecordRequest("ws", endpoint, http.Response.StatusCode);
                        IonInstruments.RecordRequestDuration("ws", endpoint, sw.Elapsed.TotalMilliseconds);
                        IonInstruments.RecordError("ws", endpoint, error.Value.code);
                        return;
                    }
                }


                using var ws = await http.WebSockets.AcceptWebSocketAsync(subProtocol).ConfigureAwait(true);

                var invokeMsg = await ReceiveSetupMessageAsync(ws, ct).ConfigureAwait(true);

                if (invokeMsg.messageType == WebSocketMessageType.Close)
                {
                    await CloseGracefullyAsync(ws, "ack", ct);
                    sw.Stop();
                    IonInstruments.RecordRequest("ws", endpoint, StatusCodes.Status200OK);
                    IonInstruments.RecordRequestDuration("ws", endpoint, sw.Elapsed.TotalMilliseconds);
                    return;
                }

                if (invokeMsg.messageType != WebSocketMessageType.Binary || invokeMsg.payload.Length == 0)
                {
                    await CloseGracefullyAsync(ws, "Expected binary INVOKE frame", ct);
                    sw.Stop();
                    IonInstruments.RecordRequest("ws", endpoint, StatusCodes.Status400BadRequest);
                    IonInstruments.RecordRequestDuration("ws", endpoint, sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("ws", endpoint, "INVALID_FRAME");
                    return;
                }

                var reader = new CborReader(invokeMsg.payload);

                try
                {
                    if (ticketExchange is not null)
                        ticketExchange.OnTicketApply(ticketData!);

                    var inputStream = router.IsAllowInputStream(methodName) ? ReadIncomingStreamAsync(ws, ct) : null;

                    await foreach (var encodedItem in router
                                       .StreamRouteExecuteAsync(methodName, reader, inputStream, ct)
                                       .ConfigureAwait(true))
                        await SendOpFrameAsync(ws, IonWs.OPCODE_DATA, encodedItem, ct);

                    await SendOpFrameAsync(ws, IonWs.OPCODE_END, ReadOnlyMemory<byte>.Empty, ct);
                    await CloseGracefullyAsync(ws, "done", ct);

                    sw.Stop();
                    IonInstruments.RecordRequest("ws", endpoint, StatusCodes.Status200OK);
                    IonInstruments.RecordRequestDuration("ws", endpoint, sw.Elapsed.TotalMilliseconds);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "cancel", CancellationToken.None);
                    }
                    catch
                    {
                    }

                    sw.Stop();
                    IonInstruments.RecordRequest("ws", endpoint, StatusCodes.Status499ClientClosedRequest);
                    IonInstruments.RecordRequestDuration("ws", endpoint, sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("ws", endpoint, "OPERATION_CANCELLED");
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "WS handler failed for {Endpoint}", endpoint);
                    try
                    {
                        var err = IonErrorSanitizer.Sanitize(ex, transportOptions.Value.DetailedErrors);
                        var writer = new CborWriter();
                        IonFormatterStorage<IonProtocolError>.Write(writer, err);
                        var bytes = writer.Encode();
                        await SendOpFrameAsync(ws, IonWs.OPCODE_ERROR, bytes, ct);
                    }
                    catch
                    {
                    }

                    try
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "exception",
                            CancellationToken.None);
                    }
                    catch (Exception closeEx)
                    {
                        log.LogWarning(closeEx, "Failed to close WebSocket gracefully for {Endpoint}", endpoint);
                    }

                    sw.Stop();
                    IonInstruments.RecordRequest("ws", endpoint, StatusCodes.Status500InternalServerError);
                    IonInstruments.RecordRequestDuration("ws", endpoint, sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("ws", endpoint, "INTERNAL_ERROR");
                }
            }
            finally
            {
                IonInstruments.DecrementActiveConnections("ws");
            }
        });


        app.MapPost("/ion/{interfaceName}/{methodName}.unary", async (
                string interfaceName, string methodName,
                HttpRequest req, HttpResponse resp,
                [FromServices] IonDescriptorStorage store,
                [FromServices] IServiceProvider provider,
                [FromServices] IEnumerable<IIonInterceptor> interceptors,
                [FromServices] IOptions<IonTransportOptions> transportOptions,
                [FromServices] ILoggerFactory lf,
                [FromServices] IonRequestTerminatorStorage terminatorStorage,
                CancellationToken ct) =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var log = lf.CreateLogger("RPC");
                var endpoint = $"{interfaceName}/{methodName}";

                if (req.ContentType is null ||
                    !req.ContentType.StartsWith(IonContentType, StringComparison.OrdinalIgnoreCase))
                {
                    resp.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                    await WriteError(log, resp, "UNSUPPORTED_MEDIA", $"Content-Type must be {IonContentType}");
                    sw.Stop();
                    IonInstruments.RecordRequest("unary", endpoint, resp.StatusCode);
                    IonInstruments.RecordRequestDuration("unary", endpoint, sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("unary", endpoint, "UNSUPPORTED_MEDIA");
                    return;
                }

                if (!store.IsServiceAllowedOnPort(interfaceName, req.HttpContext.Connection.LocalPort))
                {
                    resp.StatusCode = StatusCodes.Status404NotFound;
                    sw.Stop();
                    IonInstruments.RecordRequest("unary", endpoint, resp.StatusCode);
                    IonInstruments.RecordRequestDuration("unary", endpoint, sw.Elapsed.TotalMilliseconds);
                    return;
                }

                await using var scope = provider.CreateAsyncScope();

                using var msStream = new MemoryStream();
                await req.Body.CopyToAsync(msStream, ct);

                var memory = new Memory<byte>(msStream.GetBuffer(), 0, (int)msStream.Length);

                var @interface = store.GetTransportInterface(interfaceName);
                var method = store.GetTransportMethod(interfaceName, methodName);
                var router = store.GetRouter(interfaceName, scope);

                if (router is null || @interface is null || method is null)
                {
                    resp.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    await WriteError(log, resp, "INTERFACE_NOT_FOUND", $"Interface {interfaceName} is not found");
                    sw.Stop();
                    IonInstruments.RecordRequest("unary", endpoint, resp.StatusCode);
                    IonInstruments.RecordRequestDuration("unary", endpoint, sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("unary", endpoint, "INTERFACE_NOT_FOUND");
                    return;
                }

                using var callCtx = new ServerSideCallContext(scope, @interface, method);


                foreach (var header in req.Headers)
                    callCtx.RequestItems.Add(header.Key, header.Value.ToString());

                ExtractCorrelation(req, resp, callCtx, transportOptions.Value);
                using var logScope = BeginCorrelationScope(log, callCtx);

                var reader = new CborReader(memory);
                var writer = new CborWriter();

                async Task TerminalAsync(IIonCallContext ctxIn, CancellationToken token)
                {
                    await router.RouteExecuteAsync(methodName, reader, writer, token);

                    var terminator = terminatorStorage.TakeTerminator(ctxIn.InterfaceName, ctxIn.MethodName);

                    if (terminator is not null)
                    {
                        await terminator.OnTerminateAsync(resp, token);
                        return;
                    }

                    resp.StatusCode = StatusCodes.Status200OK;
                    resp.ContentType = IonContentType;

                    foreach (var (k, v) in ctxIn.ResponseItems)
                        resp.Headers.Append(k, v);

                    if (writer.BytesWritten != 0)
                        await resp.BodyWriter.WriteAsync(writer.Encode(), token);
                    await resp.BodyWriter.FlushAsync(token);
                }

                try
                {
                    var next = TerminalAsync;

                    var array = interceptors.ToArray();
                    for (var i = array.Length - 1; i >= 0; i--)
                    {
                        var interceptor = array[i];
                        var currentNext = next;
                        next = (c, token) => interceptor.InvokeAsync(c, currentNext, token);
                    }

                    await next(callCtx, ct).ConfigureAwait(false);

                    sw.Stop();
                    IonInstruments.RecordRequest("unary", endpoint, resp.StatusCode);
                    IonInstruments.RecordRequestDuration("unary", endpoint, sw.Elapsed.TotalMilliseconds);
                }
                catch (IonRequestException ionException)
                {
                    resp.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteError(log, resp, ionException.Error.code, ionException.Error.msg);
                    sw.Stop();
                    IonInstruments.RecordRequest("unary", endpoint, resp.StatusCode);
                    IonInstruments.RecordRequestDuration("unary", endpoint, sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("unary", endpoint, ionException.Error.code);
                }
                catch (OperationCanceledException)
                {
                    resp.StatusCode = StatusCodes.Status504GatewayTimeout;
                    await WriteError(resp, IonProtocolError.DEADLINE_EXCEEDED());
                    sw.Stop();
                    IonInstruments.RecordRequest("unary", endpoint, resp.StatusCode);
                    IonInstruments.RecordRequestDuration("unary", endpoint, sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("unary", endpoint, "DEADLINE_EXCEEDED");
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "handler failed for {Endpoint}", endpoint);
                    resp.StatusCode = StatusCodes.Status500InternalServerError;
                    var sanitized = IonErrorSanitizer.Sanitize(ex, transportOptions.Value.DetailedErrors);
                    await WriteError(log, resp, sanitized.code, sanitized.msg);
                    sw.Stop();
                    IonInstruments.RecordRequest("unary", endpoint, resp.StatusCode);
                    IonInstruments.RecordRequestDuration("unary", endpoint, sw.Elapsed.TotalMilliseconds);
                    IonInstruments.RecordError("unary", endpoint, "INTERNAL_ERROR");
                }
            })
            .WithMetadata(new ConsumesAttribute(IonContentType))
            .Produces(StatusCodes.Status200OK, contentType: IonContentType)
            .Produces(StatusCodes.Status400BadRequest, contentType: IonContentType)
            .Produces(StatusCodes.Status409Conflict, contentType: IonContentType)
            .Produces(StatusCodes.Status415UnsupportedMediaType, contentType: IonContentType)
            .Produces(StatusCodes.Status500InternalServerError, contentType: IonContentType);

        return app;
    }

    private static async Task WriteError(ILogger logger, HttpResponse resp, string code, string message)
    {
        resp.ContentType = IonContentType;
        resp.Headers.Append(IonStatusCode, code);
        logger.LogError("{Message}, {Code}", message, code);
        await IonBinarySerializer.SerializeAsync(new IonProtocolError(code, message),
            async memory => { await resp.BodyWriter.WriteAsync(memory); });
    }

    private static async Task WriteError(HttpResponse resp, IonProtocolError error)
    {
        resp.ContentType = IonContentType;
        resp.Headers.Append(IonStatusCode, error.code);
        await IonBinarySerializer.SerializeAsync(error, async memory => { await resp.BodyWriter.WriteAsync(memory); });
    }

    private static async Task SendOpFrameAsync(
        WebSocket ws,
        byte opcode,
        ReadOnlyMemory<byte> cborPayload,
        CancellationToken ct)
    {
        if (cborPayload.IsEmpty)
        {
            var frame = opcode switch
            {
                IonWs.OPCODE_DATA => OpcodeDataFrame,
                IonWs.OPCODE_END => OpcodeEndFrame,
                IonWs.OPCODE_ERROR => OpcodeErrorFrame,
                _ => [opcode]
            };
            await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(cborPayload.Length + 1);
        try
        {
            rented[0] = opcode;
            cborPayload.Span.CopyTo(rented.AsSpan(1));
            await ws.SendAsync(new ArraySegment<byte>(rented, 0, cborPayload.Length + 1), WebSocketMessageType.Binary,
                true, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }


    private static async Task<(WebSocketMessageType messageType, ReadOnlyMemory<byte> payload)>
        ReceiveSetupMessageAsync(
            WebSocket ws,
            CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var ms = new MemoryStream(64 * 1024);
            WebSocketReceiveResult result;
            do
            {
                var seg = new ArraySegment<byte>(buffer);
                result = await ws.ReceiveAsync(seg, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return (result.MessageType, ReadOnlyMemory<byte>.Empty);
                if (result.Count > 0)
                    ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            // Copy data before returning buffer to pool to avoid use-after-return
            return (WebSocketMessageType.Binary, ms.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CloseGracefullyAsync(WebSocket ws, string message, CancellationToken ct)
    {
        try
        {
            if (ws.State == WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, message, ct).ConfigureAwait(false);
            else if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, message, ct).ConfigureAwait(false);
        }
        catch
        {
            // Ignore close errors - connection may already be terminated
        }
    }

    private static async Task<(WebSocketMessageType messageType, byte opcode, ReadOnlyMemory<byte> payload)>
        ReceiveOpFrameAsync(WebSocket ws, CancellationToken ct)
    {
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var segment = new ArraySegment<byte>(rented);
            var result = await ws.ReceiveAsync(segment, ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
                return (result.MessageType, 0, ReadOnlyMemory<byte>.Empty);

            if (result.MessageType != WebSocketMessageType.Binary || result.Count == 0)
                throw new InvalidOperationException("Expected non-empty binary frame");

            var opcode = rented[0];
            // Copy payload before returning buffer to pool
            var payload = result.Count > 1
                ? rented.AsSpan(1, result.Count - 1).ToArray()
                : [];

            return (result.MessageType, opcode, payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    sealed class WebSocketScope(WebSocket ws) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Stream disposed",
                        CancellationToken.None
                    ).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadIncomingStreamAsync(
        WebSocket ws,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var _ = new WebSocketScope(ws);

        while (!ct.IsCancellationRequested)
        {
            var (msgType, opcode, payload) = await ReceiveOpFrameAsync(ws, ct).ConfigureAwait(false);

            if (msgType == WebSocketMessageType.Close)
                yield break;

            switch (opcode)
            {
                case IonWs.OPCODE_DATA:
                    if (payload.IsEmpty)
                        yield break;
                    yield return payload;
                    break;

                case IonWs.OPCODE_END:
                    yield break;

                case IonWs.OPCODE_ERROR:
                    throw new InvalidOperationException("Received OPCODE_ERROR from client");

                default:
                    throw new InvalidOperationException($"Unknown opcode {opcode}");
            }
        }
    }

    /// <summary>
    /// Configures Kestrel to listen on all ports registered via <c>AddService&lt;T,I&gt;(port: ...)</c>.
    /// Call this on the <see cref="WebApplicationBuilder"/> before <c>Build()</c>.
    /// <example>
    /// <code>
    /// builder.UseIonPorts();
    /// </code>
    /// </example>
    /// </summary>
    public static WebApplicationBuilder UseIonPorts(this WebApplicationBuilder builder)
    {
        // Find the IonPortBindingRegistry that was registered as a singleton instance
        var descriptor = builder.Services.FirstOrDefault(d =>
            d.ServiceType == typeof(IonPortBindingRegistry));

        if (descriptor?.ImplementationInstance is not IonPortBindingRegistry registry || registry.Ports.Count == 0)
            return builder;

        builder.WebHost.ConfigureKestrel((_, kestrel) =>
        {
            foreach (var port in registry.Ports)
                kestrel.ListenAnyIP(port);
        });

        return builder;
    }
}