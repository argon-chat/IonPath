namespace ion.runtime.client;

using Microsoft.Extensions.DependencyInjection;
using network;
using System.Diagnostics;
using System.Formats.Cbor;
using System.Net.Http.Headers;
using System.Reflection;
using static System.Runtime.InteropServices.JavaScript.JSType;

public class IonClient
{
    private readonly IonClientContext _context;

    private IonClient(IonClientContext context) => _context = context;

    public static IonClient Create(string endpoint, HttpClientHandler? httpHandle) 
        => new(new IonClientContext(new HttpClient(httpHandle ?? new HttpClientHandler())
        {
            BaseAddress = new Uri(endpoint)
        }));

    public static IonClient Create(HttpClient client)
        => new(new IonClientContext(client));

    public IonClient WithInterceptor<T>() where T : IIonInterceptor, new()
    {
        _context.Use(Activator.CreateInstance<T>());
        return this;
    }

    public IonClient WithInterceptor<T>(T interceptor) where T : IIonInterceptor
    {
        _context.Use(interceptor);
        return this;
    }

    public T ForService<T>(AsyncServiceScope scope) where T : IIonService => IonExecutorMetadataStorage.TakeClient<T>(scope, _context);
}

public interface IFooBarService : IIonService
{
    Task<int> GetIntFuck(string s);
}
public sealed class FooBarServiceImpl(IonClientContext context) : IFooBarService
{
    private static readonly Lazy<MethodInfo> GetIntFuck_Ref = new(() =>
        typeof(IFooBarService).GetMethod(nameof(GetIntFuck), BindingFlags.Public | BindingFlags.Instance)!);

    public async Task<int> GetIntFuck(string s)
    {
        var req = new IonRequest(context, typeof(IFooBarService), GetIntFuck_Ref.Value);

        var writer = new CborWriter();

        const int argsSize = 4;

        writer.WriteStartArray(argsSize);

        IonFormatterStorage<string>.Write(writer, s);

        writer.WriteEndArray();

        return await req.CallAsync<int>(writer.Encode());
    }
}



public class IonClientContext(HttpClient client)
{
    private readonly List<IIonInterceptor> interceptors = [];

    public IonClientContext Use(IIonInterceptor interceptor)
    {
        interceptors.Add(interceptor);
        return this;
    }
    public IonClientContext Use(params IIonInterceptor[] args)
    {
        interceptors.AddRange(args);
        return this;
    }

    public HttpClient HttpClient => client;

    public IReadOnlyList<IIonInterceptor> Interceptors => interceptors;
}



public class IonRequest(IonClientContext context, Type interfaceName, MethodInfo methodName)
{
    public static string IonContentType = "application/ion";
    
    public async Task<TResponse> CallAsync<TResponse>(
        ReadOnlyMemory<byte> payload,
        CancellationToken ct = default)
    {
        var httpClient = context.HttpClient;

        var ctx = new IonCallContext(httpClient, interfaceName, methodName, typeof(TResponse), payload);

        var next = TerminalAsync;
        for (var i = context.Interceptors.Count - 1; i >= 0; i--)
        {
            var interceptor = context.Interceptors[i];
            var currentNext = next;
            next = (c, token) => interceptor.InvokeAsync(c, currentNext, token);
        }

        await next(ctx, ct).ConfigureAwait(false);

        var reader = new CborReader(ctx.ResponsePayload!);
        return IonFormatterStorage<TResponse>.Read(reader);

        async Task TerminalAsync(IonCallContext c, CancellationToken token)
        {
            c.HttpRequest ??= new HttpRequestMessage(HttpMethod.Post, $"/ion/{c.InterfaceName.Name}/{c.MethodName.Name}")
            {
                Content = new ReadOnlyMemoryContent(c.RequestPayload)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue(IonContentType) }
                }
            };

            c.HttpResponse?.Dispose();
            c.HttpResponse = await c.Client.SendAsync(c.HttpRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

            var respBytes = await c.HttpResponse.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            c.ResponsePayload = respBytes;

            if (!c.HttpResponse.IsSuccessStatusCode)
            {
                try
                {
                    var error = IonFormatterStorage<IonProtocolError>.Read(new CborReader(respBytes));
                    throw new IonRequestException(error);
                }
                catch (Exception)
                {
                    throw new IonRequestException(IonProtocolError.UPSTREAM_ERROR(c.HttpResponse.ReasonPhrase ?? c.HttpResponse.StatusCode.ToString()));
                }
            }
        }
    }
}

public class IonRequestException(IonProtocolError error)
    : Exception($"Ion request throw exception, {error.code}: {error.msg}");

public interface IIonInterceptor
{
    Task InvokeAsync(IonCallContext context, Func<IonCallContext, CancellationToken, Task> next, CancellationToken ct);
}

public sealed class IonCallContext(
    HttpClient client,
    Type iface,
    MethodInfo method,
    Type resp,
    ReadOnlyMemory<byte> requestPayload)
{
    public HttpClient Client { get; } = client;
    public Type InterfaceName { get; } = iface;
    public MethodInfo MethodName { get; } = method;

    public Type ResponseType { get; } = resp;

    /// <summary>Сериализованное тело запроса (CBOR). Интерцептор может заменить/перезаписать.</summary>
    public ReadOnlyMemory<byte> RequestPayload { get; set; } = requestPayload;

    /// <summary>Сериализованное тело ответа (CBOR).</summary>
    public byte[]? ResponsePayload { get; set; }

    /// <summary>HttpRequest, можно модифицировать до отправки (заголовки и т.п.).</summary>
    public HttpRequestMessage? HttpRequest { get; set; }

    /// <summary>HttpResponse, доступен после ответа.</summary>
    public HttpResponseMessage? HttpResponse { get; set; }

    /// <summary>Произвольные данные для обмена между интерцепторами.</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    /// <summary>Количество попыток (для ретраев).</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>Общий Stopwatch для метрик/логов.</summary>
    public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
}