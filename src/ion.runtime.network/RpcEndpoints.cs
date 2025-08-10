namespace ion.runtime.network;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Formats.Cbor;

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


    public const string HeaderDeadlineMs = "X-Deadline";
    public static string IonContentType = "application/ion";
    public static string IonContentTypeOutput = "application/ion; charset=binary; ver=1";

    public static string IdempotencyHeader = "X-Idempotency-Key";
    public static string RequestSignatureHeader = "X-Sig-Key";
    public static string IonStatusCode = "X-Ion-Status";


    public static IEndpointRouteBuilder MapRpcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/ion/{interfaceName}/{methodName}", async (string interfaceName, string methodName,
                HttpRequest req, HttpResponse resp, 
                IonDescriptorStorage store,
                IServiceProvider provider,
                ILoggerFactory lf, CancellationToken ct) 
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

                
                using var msStream = new MemoryStream();
                await req.Body.CopyToAsync(msStream, ct);

                var memory = new Memory<byte>(msStream.GetBuffer(), 0, (int)msStream.Length);

                await using var scope = provider.CreateAsyncScope();

                var router = store.GetRouter(interfaceName, scope);

                if (router is null)
                {
                    resp.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    await WriteError(resp, "INTERFACE_NOT_FOUND", $"Interface {interfaceName} is not found");
                    return;
                }

                var reader = new CborReader(memory);
                var writer = new CborWriter();

                try
                {
                    await router.RouteExecuteAsync(methodName, reader, writer);
                    resp.StatusCode = StatusCodes.Status200OK;
                    resp.ContentType = IonContentType;
                    await resp.BodyWriter.WriteAsync(writer.Encode(), ct);
                    await resp.BodyWriter.FlushAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    resp.StatusCode = StatusCodes.Status504GatewayTimeout;
                    await WriteError(resp, "DEADLINE_EXCEEDED", "Deadline exceeded");
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "handler failed");
                    resp.StatusCode = StatusCodes.Status500InternalServerError;
                    await WriteError(resp, "INTERNAL_ERROR", ex.ToString());
                }
            })
            .Accepts<byte[]>(IonContentType)
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
}