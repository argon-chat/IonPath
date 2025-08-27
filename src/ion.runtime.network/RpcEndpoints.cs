namespace ion.runtime.network;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Formats.Cbor;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;

public class ServerSideCallContext(AsyncServiceScope scope, Type @interface, MethodInfo @method) : IIonCallContext
{
    public Type InterfaceName { get; } = @interface;
    public MethodInfo MethodName { get; } = @method;
    public IDictionary<string, string> RequestItems { get; } = new Dictionary<string, string>();
    public IDictionary<string, string> ResponseItems { get; } = new Dictionary<string, string>();
    public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
    public AsyncServiceScope AsyncServiceScope { get; } = scope;
    public void Dispose() => Stopwatch.Stop();
}


public static class RpcEndpoints
{
    public static IServiceCollection AddIonProtocol(this IServiceCollection services, Action<IIonTransportRegistration> onRegistration)
    {
        services.Configure<IonTransportOptions>(_ => { });
        services.AddSingleton<IonDescriptorStorage>();
        var reg = new IonDescriptorRegistration(services);
        onRegistration(reg);
        return services;
    }

    public static IServiceCollection AddIonService<TInterface, TImplementation>(this IServiceCollection services)
        where TInterface : class, IIonService
        where TImplementation : class, TInterface
    {
        services.AddScoped<TInterface, TImplementation>();
        services.Configure<IonTransportOptions>(options => 
            options.Services.Add(typeof(TInterface), typeof(TImplementation)));
        return services;
    }

    public static IServiceCollection AddIonInterceptor<TImplementation>(this IServiceCollection services)
        where TImplementation : class, IIonInterceptor
    {
        services.AddScoped<IIonInterceptor, TImplementation>();
        services.Configure<IonTransportOptions>(options =>
            options.Interceptors.Add(typeof(TImplementation)));
        return services;
    }


    public const string HeaderDeadlineMs = "X-Deadline";
    public static string IonContentType = "application/ion";
    public static string IonContentTypeOutput = "application/ion; charset=binary; ver=1";

    public static string IdempotencyHeader = "X-Idempotency-Key";
    public static string RequestSignatureHeader = "X-Sig-Key";
    public static string IonStatusCode = "X-Ion-Status";

    static class IonWs
    {
        public const byte OPCODE_DATA = 0x00;
        public const byte OPCODE_END = 0x01;
        public const byte OPCODE_ERROR = 0x02;
    }


    public static IEndpointRouteBuilder MapRpcEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map("/ion/{interfaceName}/{methodName}.ws", async (HttpContext http,
            string interfaceName,
            string methodName,
            IonDescriptorStorage store,
            IServiceProvider provider,
            ILoggerFactory lf,
            CancellationToken ct) =>
        {
            var log = lf.CreateLogger("RPC.WS");

            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsync("WebSocket expected", ct);
                return;
            }
            await using var scope = provider.CreateAsyncScope();
            var router = store.GetStreamRouter(interfaceName, scope);
            using var ws = await http.WebSockets.AcceptWebSocketAsync();

            if (router is null)
            {
                await CloseErrorGracefully(ws, $"Method {methodName} is not server-streaming", ct);
                return;
            }

            var invokeMsg = await ReceiveSetupMessageAsync(ws, ct);

            if (invokeMsg.messageType == WebSocketMessageType.Close)
            {
                await CloseGracefully(ws, ct);
                return;
            }

            if (invokeMsg.messageType != WebSocketMessageType.Binary || invokeMsg.payload.Length == 0)
            {
                await CloseErrorGracefully(ws, "Expected binary INVOKE frame", ct);
                return;
            }
            
            var reader = new CborReader(invokeMsg.payload);

            try
            {
                await foreach (var encodedItem in router.StreamRouteExecuteAsync(methodName, reader, ct))
                {
                    await SendOpFrameAsync(ws, IonWs.OPCODE_DATA, encodedItem, ct);
                }

                await SendOpFrameAsync(ws, IonWs.OPCODE_END, ReadOnlyMemory<byte>.Empty, ct);
                await CloseGracefully(ws, ct);
            }
            catch (OperationCanceledException)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "cancel", CancellationToken.None); } 
                catch { }
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
                catch { }
                try { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "exception", CancellationToken.None); } 
                catch { }
            }
        });

        
        app.MapPost("/ion/{interfaceName}/{methodName}.unary", async (string interfaceName, string methodName,
                HttpRequest req, HttpResponse resp,
                [FromServices] IonDescriptorStorage store,
                [FromServices] IServiceProvider provider,
                [FromServices] IEnumerable<IIonInterceptor> interceptors,
                [FromServices] ILoggerFactory lf, CancellationToken ct) 
                =>
            {
                var log = lf.CreateLogger("RPC");
                if (req.ContentType is null ||
                    !req.ContentType.StartsWith(IonContentType, StringComparison.OrdinalIgnoreCase))
                {
                    resp.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                    await WriteError(resp, "UNSUPPORTED_MEDIA", $"Content-Type must be {IonContentType}");
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
                    await WriteError(resp, "INTERFACE_NOT_FOUND", $"Interface {interfaceName} is not found");
                    return;
                }

                using var callCtx = new ServerSideCallContext(scope, @interface, method);


                foreach (var header in req.Headers) 
                    callCtx.RequestItems.Add(header.Key, header.Value.ToString());

                var reader = new CborReader(memory);
                var writer = new CborWriter();

                async Task TerminalAsync(IIonCallContext _, CancellationToken token)
                {
                    await router.RouteExecuteAsync(methodName, reader, writer);
                    resp.StatusCode = StatusCodes.Status200OK;
                    resp.ContentType = IonContentType;

                    foreach (var (k, v) in _.ResponseItems) 
                        resp.Headers.Append(k, v);

                    await resp.BodyWriter.WriteAsync(writer.Encode(), token);
                    await resp.BodyWriter.FlushAsync(token);
                }

                try
                {
                    var next = TerminalAsync;

                    var array = interceptors.ToArray();
                    for (var i = array.Count() - 1; i >= 0; i--)
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
                    await WriteError(resp, ionException.Error.code, ionException.Error.msg);
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
                    await WriteError(resp, "INTERNAL_ERROR", ex.ToString());
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

    private static async Task WriteError(HttpResponse resp, string code, string message)
    {
        resp.ContentType = IonContentType;
        resp.Headers.Append(IonStatusCode, code);
        await IonBinarySerializer.SerializeAsync(new IonProtocolError(code, message), async memory =>
        {
            await resp.BodyWriter.WriteAsync(memory);
        });
    }
    private static async Task WriteError(HttpResponse resp, IonProtocolError error)
    {
        resp.ContentType = IonContentType;
        resp.Headers.Append(IonStatusCode, error.code);
        await IonBinarySerializer.SerializeAsync(error, async memory => {
            await resp.BodyWriter.WriteAsync(memory);
        });
    }

    private static async Task SendOpFrameAsync(
        WebSocket ws,
        byte opcode,
        ReadOnlyMemory<byte> cborPayload,
        CancellationToken ct)
    {
        if (cborPayload.IsEmpty)
        {
            var one = new byte[] { opcode };
            await ws.SendAsync(one, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(cborPayload.Length + 1);
        try
        {
            rented[0] = opcode;
            cborPayload.Span.CopyTo(rented.AsSpan(1));
            await ws.SendAsync(new ArraySegment<byte>(rented, 0, cborPayload.Length + 1), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }


    private static async Task<(WebSocketMessageType messageType, ReadOnlyMemory<byte> payload)> ReceiveSetupMessageAsync(
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

            return (WebSocketMessageType.Binary, new ReadOnlyMemory<byte>(ms.GetBuffer(), 0, (int)ms.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CloseGracefully(WebSocket ws, CancellationToken ct)
    {
        if (ws.State == WebSocketState.CloseReceived)
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", ct).ConfigureAwait(false); }
            catch { }
        else if (ws.State == WebSocketState.Open)
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct).ConfigureAwait(false); } 
            catch { }
    }

    private static async Task CloseErrorGracefully(WebSocket ws, string error, CancellationToken ct)
    {
        if (ws.State == WebSocketState.CloseReceived)
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, error, ct).ConfigureAwait(false); }
            catch { }
        else if (ws.State == WebSocketState.Open)
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, error, ct).ConfigureAwait(false); }
            catch { }
    }
}