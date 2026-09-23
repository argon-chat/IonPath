namespace ion.runtime.network;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
            services.AddIonStreamHub();
            services.TryAddScoped<IIonStreamContextAccessor, IonStreamContextHolder>();
            services.TryAddSingleton<IonStreamResumeRegistry>();
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

        public IServiceCollection AddIonService<TInterface, TImplementation>(int? port = null, bool excludeGlobalInterceptors = false)
            where TInterface : class, IIonService
            where TImplementation : class, TInterface
        {
            services.AddScoped<TInterface, TImplementation>();
            services.Configure<IonTransportOptions>(options =>
            {
                options.Services.Add(typeof(TInterface), typeof(TImplementation));
                if (port.HasValue)
                {
                    options.PortBindings[typeof(TInterface)] = port.Value;
                    if (excludeGlobalInterceptors)
                        options.ExcludeGlobalInterceptorPorts.Add(port.Value);
                }
            });
            return services;
        }

        /// <summary>Registers a global stream connect/disconnect hook; see <see cref="IIonStreamLifecycle"/>.</summary>
        public IServiceCollection AddIonStreamLifecycle<TImplementation>()
            where TImplementation : class, IIonStreamLifecycle
        {
            services.TryAddScoped<TImplementation>();
            services.Configure<IonTransportOptions>(options =>
            {
                if (!options.StreamLifecycles.Contains(typeof(TImplementation)))
                    options.StreamLifecycles.Add(typeof(TImplementation));
            });
            return services;
        }

        public IServiceCollection AddIonInterceptor<TImplementation>(int? port = null)
            where TImplementation : class, IIonInterceptor
        {
            services.AddScoped<TImplementation>();
            if (port.HasValue)
            {
                services.Configure<IonTransportOptions>(options =>
                {
                    if (!options.PortInterceptors.TryGetValue(port.Value, out var list))
                    {
                        list = [];
                        options.PortInterceptors[port.Value] = list;
                    }
                    list.Add(typeof(TImplementation));
                });
            }
            else
            {
                services.AddScoped<IIonInterceptor, TImplementation>();
                services.Configure<IonTransportOptions>(options =>
                    options.Interceptors.Add(typeof(TImplementation)));
            }
            return services;
        }
    }


    public const string HeaderDeadlineMs = "X-Deadline";
    public const string IonContentType = "application/ion";
    public const string IonContentTypeOutput = "application/ion; charset=binary; ver=1";

    public const string IonStatusCode = "X-Ion-Status";
    public const string SubProtocolTemplate = "ion; ticket={ticket}; ver=1";

    private static IIonInterceptor[] ResolveInterceptors(
        IEnumerable<IIonInterceptor> globalInterceptors,
        IonTransportOptions options,
        IServiceProvider scopedProvider,
        int localPort)
    {
        var excludeGlobals = options.ExcludeGlobalInterceptorPorts.Contains(localPort);
        var hasPortInterceptors = options.PortInterceptors.TryGetValue(localPort, out var portTypes)
            && portTypes.Count > 0;

        if (!hasPortInterceptors && !excludeGlobals)
            return globalInterceptors.ToArray();

        var result = excludeGlobals ? new List<IIonInterceptor>() : new List<IIonInterceptor>(globalInterceptors);

        if (hasPortInterceptors)
        {
            foreach (var type in portTypes!)
            {
                if (scopedProvider.GetService(type) is IIonInterceptor interceptor)
                    result.Add(interceptor);
            }
        }

        return result.ToArray();
    }

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

                    var array = ResolveInterceptors(interceptors, transportOptions.Value, scope.ServiceProvider, req.HttpContext.Connection.LocalPort);
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

        // One handler for both transports; the request says which one it is. `.wt` is the path
        // WebTransport clients use, `.ws` the WebSocket one — either accepts either.
        app.Map("/ion/{interfaceName}/{methodName}.ws", IonStreamEndpoint.HandleAsync);
        app.Map("/ion/{interfaceName}/{methodName}.wt", IonStreamEndpoint.HandleAsync);

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
                await HandleUnaryAsync(interfaceName, methodName, req, resp, store, provider,
                    interceptors, transportOptions.Value, lf, terminatorStorage, ct);
            })
            .WithMetadata(new ConsumesAttribute(IonContentType))
            .Produces(StatusCodes.Status200OK, contentType: IonContentType)
            .Produces(StatusCodes.Status400BadRequest, contentType: IonContentType)
            .Produces(StatusCodes.Status409Conflict, contentType: IonContentType)
            .Produces(StatusCodes.Status415UnsupportedMediaType, contentType: IonContentType)
            .Produces(StatusCodes.Status500InternalServerError, contentType: IonContentType);

        return app;
    }

    private static async Task HandleUnaryAsync(
        string interfaceName, string methodName,
        HttpRequest req, HttpResponse resp,
        IonDescriptorStorage store,
        IServiceProvider provider,
        IEnumerable<IIonInterceptor> interceptors,
        IonTransportOptions transportOptions,
        ILoggerFactory lf,
        IonRequestTerminatorStorage terminatorStorage,
        CancellationToken ct)
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

        ExtractCorrelation(req, resp, callCtx, transportOptions);
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

            var array = ResolveInterceptors(interceptors, transportOptions, scope.ServiceProvider, req.HttpContext.Connection.LocalPort);
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
            var sanitized = IonErrorSanitizer.Sanitize(ex, transportOptions.DetailedErrors);
            await WriteError(log, resp, sanitized.code, sanitized.msg);
            sw.Stop();
            IonInstruments.RecordRequest("unary", endpoint, resp.StatusCode);
            IonInstruments.RecordRequestDuration("unary", endpoint, sw.Elapsed.TotalMilliseconds);
            IonInstruments.RecordError("unary", endpoint, "INTERNAL_ERROR");
        }
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

    /// <summary>
    /// Maps Ion RPC endpoints for services bound to a specific port, returning a <see cref="RouteGroupBuilder"/>
    /// that allows chaining ASP.NET conventions (e.g. RequireAuthorization, CORS, rate limiting).
    /// The routes are registered with a host filter so they only match on the specified port.
    /// <example>
    /// <code>
    /// app.MapRpcPortEndpoints(9090)
    ///    .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = "OperatorCert" });
    /// </code>
    /// </example>
    /// </summary>
    public static RouteGroupBuilder MapRpcPortEndpoints(this IEndpointRouteBuilder app, int port)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<IonTransportOptions>>().Value;

        var portServices = options.PortBindings
            .Where(kv => kv.Value == port)
            .Select(kv => kv.Key.Name)
            .ToList();

        if (portServices.Count == 0)
            throw new InvalidOperationException($"No Ion services are bound to port {port}.");

        var group = app.MapGroup("ion").RequireHost($"*:{port}");

        foreach (var serviceName in portServices)
        {
            group.MapPost($"{serviceName}/{{methodName}}.unary", async (
                    string methodName,
                    HttpRequest req, HttpResponse resp,
                    [FromServices] IonDescriptorStorage store,
                    [FromServices] IServiceProvider provider,
                    [FromServices] IEnumerable<IIonInterceptor> interceptors,
                    [FromServices] IOptions<IonTransportOptions> transportOptions,
                    [FromServices] ILoggerFactory lf,
                    [FromServices] IonRequestTerminatorStorage terminatorStorage,
                    CancellationToken ct) =>
                {
                    await HandleUnaryAsync(serviceName, methodName, req, resp, store, provider,
                        interceptors, transportOptions.Value, lf, terminatorStorage, ct);
                })
                .WithMetadata(new ConsumesAttribute(IonContentType))
                .Produces(StatusCodes.Status200OK, contentType: IonContentType)
                .Produces(StatusCodes.Status400BadRequest, contentType: IonContentType)
                .Produces(StatusCodes.Status500InternalServerError, contentType: IonContentType);
        }

        return group;
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