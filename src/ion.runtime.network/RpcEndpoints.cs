namespace ion.runtime.network;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Buffers;
using System.Formats.Cbor;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;

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

        public IServiceCollection AddIonService<TInterface, TImplementation>()
            where TInterface : class, IIonService
            where TImplementation : class, TInterface
        {
            services.AddScoped<TInterface, TImplementation>();
            services.Configure<IonTransportOptions>(options =>
                options.Services.Add(typeof(TInterface), typeof(TImplementation)));
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

    public const string IdempotencyHeader = "X-Idempotency-Key";
    public const string RequestSignatureHeader = "X-Sig-Key";
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
                var log = lf.CreateLogger("RPC");
                var req = http.Request;
                var resp = http.Response;

                if (req.ContentType is null ||
                    !req.ContentType.StartsWith(IonContentType, StringComparison.OrdinalIgnoreCase))
                {
                    resp.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                    await WriteError(log, resp, "UNSUPPORTED_MEDIA", $"Content-Type must be {IonContentType}");
                    return;
                }

                await using var scope = provider.CreateAsyncScope();
                using var callCtx =
                    new ServerSideCallContext(scope, typeof(__internal_ion), __internal_ion.__exchange_ref);

                foreach (var header in req.Headers)
                    callCtx.RequestItems.Add(header.Key, header.Value.ToString());

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
                }
                catch (IonRequestException ionException)
                {
                    resp.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteError(log, resp, ionException.Error.code, ionException.Error.msg);
                }
                catch (OperationCanceledException)
                {
                    resp.StatusCode = StatusCodes.Status504GatewayTimeout;
                    await WriteError(resp, IonProtocolError.DEADLINE_EXCEEDED());
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "handler failed");
                    resp.StatusCode = StatusCodes.Status500InternalServerError;
                    await WriteError(log, resp, "INTERNAL_ERROR", ex.ToString());
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
            [FromServices] ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("RPC.WS");

            await using var scope = provider.CreateAsyncScope();
            var router = store.GetStreamRouter(interfaceName, scope);
            var ticketExchange = provider.GetService<IIonTicketExchange>();

            if (!http.WebSockets.IsWebSocketRequest)
            {
                log.LogWarning("UNSUPPORTED_TRANSPORT");
                http.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                await WriteError(log, http.Response, "UNSUPPORTED_TRANSPORT", $"Transport must be WebSocket");
                return;
            }

            if (router is null)
            {
                log.LogWarning("ENTRYPOINT_NOT_FOUND");
                http.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                await WriteError(log, http.Response, "ENTRYPOINT_NOT_FOUND",
                    $"Method {methodName} is not server-streaming");
                return;
            }

            var subProtocol = http.WebSockets.WebSocketRequestedProtocols.FirstOrDefault(x => x.StartsWith("ion"));

            if (string.IsNullOrEmpty(subProtocol) && ticketExchange is not null)
            {
                log.LogWarning("UNSUPPORTED_SUB_PROTOCOL");
                http.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                await WriteError(log, http.Response, "UNSUPPORTED_SUB_PROTOCOL", $"Transport sub-protocol must be ion");
                return;
            }

            var ticket = string.IsNullOrEmpty(subProtocol) ? null : IonTicketExtractor.ExtractTicketBytes(subProtocol);

            if (ticket is null && ticketExchange is not null)
            {
                log.LogWarning("TICKET_BROKEN");
                http.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                await WriteError(log, http.Response, "TICKET_BROKEN", $"Transport ticket has been broken");
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
                    return;
                }
            }

            

            using var ws = await http.WebSockets.AcceptWebSocketAsync(subProtocol).ConfigureAwait(true);

            var invokeMsg = await ReceiveSetupMessageAsync(ws, ct).ConfigureAwait(true);

            if (invokeMsg.messageType == WebSocketMessageType.Close)
            {
                await CloseGracefullyAsync(ws, "ack", ct);
                return;
            }

            if (invokeMsg.messageType != WebSocketMessageType.Binary || invokeMsg.payload.Length == 0)
            {
                await CloseGracefullyAsync(ws, "Expected binary INVOKE frame", ct);
                return;
            }

            var reader = new CborReader(invokeMsg.payload);

            try
            {
                if (ticketExchange is not null)
                    ticketExchange.OnTicketApply(ticketData!);

                var inputStream = router.IsAllowInputStream(methodName) ? 
                    ReadIncomingStreamAsync(ws, ct) : 
                    null;

                await foreach (var encodedItem in router
                                   .StreamRouteExecuteAsync(methodName, reader, inputStream, ct)
                                   .ConfigureAwait(true)) 
                    await SendOpFrameAsync(ws, IonWs.OPCODE_DATA, encodedItem, ct);

                await SendOpFrameAsync(ws, IonWs.OPCODE_END, ReadOnlyMemory<byte>.Empty, ct);
                await CloseGracefullyAsync(ws, "done", ct);
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
            }
            catch (Exception ex)
            {
                log.LogError(ex, "WS handler failed");
                try
                {
                    var err = IonProtocolError.INTERNAL_ERROR(ex.ToString());
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
                    await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "exception", CancellationToken.None);
                }
                catch
                {
                }
            }
        });


        app.MapPost("/ion/{interfaceName}/{methodName}.unary", async (string interfaceName, string methodName,
                HttpRequest req, HttpResponse resp,
                [FromServices] IonDescriptorStorage store,
                [FromServices] IServiceProvider provider,
                [FromServices] IEnumerable<IIonInterceptor> interceptors,
                [FromServices] ILoggerFactory lf,
                [FromServices] IonRequestTerminatorStorage terminatorStorage,
                CancellationToken ct) =>
            {
                var log = lf.CreateLogger("RPC");
                if (req.ContentType is null ||
                    !req.ContentType.StartsWith(IonContentType, StringComparison.OrdinalIgnoreCase))
                {
                    resp.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                    await WriteError(log, resp, "UNSUPPORTED_MEDIA", $"Content-Type must be {IonContentType}");
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
                    return;
                }

                using var callCtx = new ServerSideCallContext(scope, @interface, method);


                foreach (var header in req.Headers)
                    callCtx.RequestItems.Add(header.Key, header.Value.ToString());

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
                }
                catch (IonRequestException ionException)
                {
                    resp.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteError(log, resp, ionException.Error.code, ionException.Error.msg);
                }
                catch (OperationCanceledException)
                {
                    resp.StatusCode = StatusCodes.Status504GatewayTimeout;
                    await WriteError(resp, IonProtocolError.DEADLINE_EXCEEDED());
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "handler failed");
                    resp.StatusCode = StatusCodes.Status500InternalServerError;
                    await WriteError(log, resp, "INTERNAL_ERROR", ex.ToString());
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

}