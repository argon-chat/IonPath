namespace ion.runtime.client;

using Microsoft.Extensions.DependencyInjection;
using network;
using System.Buffers;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Formats.Cbor;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

public delegate Task<WebSocket> IonWebSocketFactory(Uri uri, CancellationToken ct, string[]? protocols = null);
public class IonClient
{
    private readonly IonClientContext _context;

    private IonClient(IonClientContext context) => _context = context;

    /// <param name="webSocketClient">
    /// Opens the WebSockets of stream calls. Null opens a <see cref="ClientWebSocket"/> configured from
    /// <see cref="IonStreamClientOptions"/>; pass one to reach an in-memory test server, say.
    /// </param>
    public static IonClient Create(string endpoint, IServiceProvider provider, HttpClientHandler? httpHandle = null, IonWebSocketFactory? webSocketClient = null)
        => new(new IonClientContext(new HttpClient(httpHandle ?? new HttpClientHandler())
        {
            BaseAddress = new Uri(endpoint)
        }, webSocketClient, provider));

    public static IonClient Create(string endpoint, HttpClientHandler? httpHandle = null, IonWebSocketFactory? webSocketClient = null)
        => new(new IonClientContext(new HttpClient(httpHandle ?? new HttpClientHandler())
        {
            BaseAddress = new Uri(endpoint)
        }, webSocketClient));

    public static IonClient Create(HttpClient client, IonWebSocketFactory? wsFactory = null)
        => new(new IonClientContext(client, wsFactory));

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

    /// <summary>Configures stream calls: transport order, heartbeat, timeouts.</summary>
    public IonClient WithStreamOptions(Action<IonStreamClientOptions> configure)
    {
        configure(_context.StreamOptions);
        return this;
    }

    public T ForService<T>(AsyncServiceScope scope) where T : IIonService =>
        IonExecutorMetadataStorage.TakeClient<T>(scope, _context);

    public T ForService<T>(IServiceProvider provider) where T : IIonService =>
        IonExecutorMetadataStorage.TakeClient<T>(provider, _context);
}

public class IonClientContext(HttpClient client, IonWebSocketFactory? wsFactory, IServiceProvider? serviceProvider = null)
{
    private readonly List<IIonInterceptor> interceptors = [];
    internal IServiceProvider serviceProvider = serviceProvider ?? new ServiceContainer();

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

    /// <summary>The WebSocket factory stream calls use; null for the built-in <see cref="ClientWebSocket"/>.</summary>
    public IonWebSocketFactory? WebSocketFactory => wsFactory;

    /// <summary>Settings for stream calls made through this context.</summary>
    public IonStreamClientOptions StreamOptions { get; } = new();

    public IReadOnlyList<IIonInterceptor> Interceptors => interceptors;
}

public class IonRequest(IonClientContext context, Type interfaceName, MethodInfo methodName)
{
    public static string IonContentType = "application/ion";

    public async Task CallAsync(ReadOnlyMemory<byte> payload,
        CancellationToken ct = default)
    {
        var httpClient = context.HttpClient;

        var ctx = new IonCallContext(context.serviceProvider, httpClient, interfaceName, methodName, typeof(void), payload);

        var next = TerminalAsync;
        for (var i = context.Interceptors.Count - 1; i >= 0; i--)
        {
            var interceptor = context.Interceptors[i];
            var currentNext = next;
            next = (c, token) => interceptor.InvokeAsync(c, currentNext, token);
        }

        await next(ctx, ct).ConfigureAwait(false);

        return;

        async Task TerminalAsync(IIonCallContext callCtx, CancellationToken token)
        {
            if (callCtx is not IonCallContext c)
                throw new InvalidOperationException($"Invalid configuration, call context broken");

            c.HttpRequest ??=
                new HttpRequestMessage(HttpMethod.Post, $"/ion/{c.InterfaceName.Name}/{c.MethodName.Name}.unary")
                {
                    Content = new ReadOnlyMemoryContent(c.RequestPayload)
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue(IonContentType) }
                    }
                };

            foreach (var (hKey, hValue) in c.RequestItems) 
                c.HttpRequest.Headers.Add(hKey, hValue);

            c.HttpResponse?.Dispose();
            c.HttpResponse = await c.Client.SendAsync(c.HttpRequest, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            var respBytes = await c.HttpResponse.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            c.ResponsePayload = respBytes;

            if (!c.HttpResponse.IsSuccessStatusCode)
                throw IonResponseError.From(c.HttpResponse, respBytes);
        }
    }

    public Task<TResponse> CallAsync<TResponse>(
        ReadOnlyMemory<byte> payload,
        CancellationToken ct = default)
        => CallCoreAsync<TResponse, TResponse>(
            payload,
            IonFormatterStorage<TResponse>.Read,
            ct);

    public Task<IonArray<TResponse>> CallAsyncWithArray<TResponse>(
        ReadOnlyMemory<byte> payload,
        CancellationToken ct = default)
        => CallCoreAsync<TResponse, IonArray<TResponse>>(
            payload,
            IonFormatterStorage<TResponse>.ReadArray,
            ct);

    /// <summary>
    /// Reads a <c>T[]?</c> response — <c>Maybe&lt;Array&lt;T&gt;&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The <typeparamref name="TResponse"/> is the array <em>element</em>, matching
    /// <see cref="CallAsyncWithArray{TResponse}"/>. Neither neighbour can serve this shape:
    /// <see cref="CallAsyncWithArray{TResponse}"/> cannot produce <c>null</c>, and
    /// <c>CallAsyncNullable&lt;IonArray&lt;T&gt;&gt;</c> would need a formatter registered for
    /// <c>IonArray&lt;T&gt;</c> itself, which nothing registers. Mirrors
    /// <c>IonUnaryRequest.callAsyncNullableArrayT</c> in ion.webcore.js.
    /// </remarks>
    public Task<IonArray<TResponse>?> CallAsyncNullableArray<TResponse>(
        ReadOnlyMemory<byte> payload,
        CancellationToken ct = default)
        => CallCoreAsync<TResponse, IonArray<TResponse>?>(
            payload,
            IonFormatterStorage<TResponse>.ReadNullableArray,
            ct);

    public Task<TResponse?> CallAsyncNullable<TResponse>(
        ReadOnlyMemory<byte> payload,
        _StructTag<TResponse> _ = default,
        CancellationToken ct = default) where TResponse : struct
        => CallCoreAsync<TResponse, TResponse?>(
            payload,
            reader => reader.ReadNullable<TResponse>(),
            ct);

    public Task<TResponse?> CallAsyncNullable<TResponse>(
        ReadOnlyMemory<byte> payload,
        _ClassTag<TResponse> _ = default,
        CancellationToken ct = default) where TResponse : class
        => CallCoreAsync<TResponse, TResponse?>(
            payload,
            reader => reader.ReadNullable<TResponse>(),
            ct);

    public readonly struct _StructTag<T> where T : struct { }
    public readonly struct _ClassTag<T> where T : class { }

    private async Task<TResult> CallCoreAsync<TResponse, TResult>(
        ReadOnlyMemory<byte> payload,
        Func<CborReader, TResult> projector,
        CancellationToken ct = default)
    {
        var httpClient = context.HttpClient;

        using var ctx = new IonCallContext(context.serviceProvider, httpClient, interfaceName, methodName,
            typeof(TResponse), payload);

        var next = TerminalAsync;
        for (var i = context.Interceptors.Count - 1; i >= 0; i--)
        {
            var interceptor = context.Interceptors[i];
            var currentNext = next;
            next = (c, token) => interceptor.InvokeAsync(c, currentNext, token);
        }

        await next(ctx, ct).ConfigureAwait(false);

        var reader = new CborReader(ctx.ResponsePayload!);
        return projector(reader);

        async Task TerminalAsync(IIonCallContext callCtx, CancellationToken token)
        {
            if (callCtx is not IonCallContext c)
                throw new InvalidOperationException("Invalid configuration, call context broken");

            c.HttpRequest ??=
                new HttpRequestMessage(HttpMethod.Post, $"/ion/{c.InterfaceName.Name}/{c.MethodName.Name}.unary")
                {
                    Content = new ReadOnlyMemoryContent(c.RequestPayload)
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue(IonContentType) }
                    }
                };

            foreach (var (hKey, hValue) in c.RequestItems)
                c.HttpRequest.Headers.Add(hKey, hValue);

            c.HttpResponse?.Dispose();
            c.HttpResponse = await c.Client.SendAsync(
                c.HttpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                token
            ).ConfigureAwait(false);

            var respBytes = await c.HttpResponse.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            c.ResponsePayload = respBytes;

            if (!c.HttpResponse.IsSuccessStatusCode)
                throw IonResponseError.From(c.HttpResponse, respBytes);
        }
    }
}

public sealed class IonCallContext(
    IServiceProvider provider,
    HttpClient client,
    Type iface,
    MethodInfo method,
    Type resp,
    ReadOnlyMemory<byte> requestPayload) : IIonCallContext
{
    public HttpClient Client { get; } = client;
    public Type InterfaceName { get; } = iface;
    public MethodInfo MethodName { get; } = method;
    public IDictionary<string, string> RequestItems { get; } = new Dictionary<string, string>();
    public IDictionary<string, string> ResponseItems { get; } = new Dictionary<string, string>();

    public Type ResponseType { get; } = resp;

    public ReadOnlyMemory<byte> RequestPayload { get; set; } = requestPayload;

    public byte[]? ResponsePayload { get; set; }
    public HttpRequestMessage? HttpRequest { get; set; }
    public HttpResponseMessage? HttpResponse { get; set; }
    public int Attempt { get; set; } = 1;
    public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
    public IServiceProvider ServiceProvider => provider;

    public void Dispose()
    {
        HttpRequest?.Dispose();
        HttpResponse?.Dispose();
        Stopwatch.Stop();
    }
}

/// <summary>
/// Builds the exception for a non-2xx Ion response.
/// </summary>
/// <remarks>
/// Both call sites used to inline this, and they had drifted. One wrapped the decode in
/// <c>try { … throw new IonRequestException(error); } catch (Exception) { … }</c> — so the
/// correctly decoded server error was caught by its own catch and replaced with the HTTP reason
/// phrase. Every server-side failure on that path surfaced as a bare
/// <c>UPSTREAM_ERROR: Bad Request</c> with the real code and message discarded.
/// <para>
/// The other site rethrew correctly but still threw the body away when it was not CBOR, which is
/// exactly the case where the body is the only evidence there is.
/// </para>
/// </remarks>
internal static class IonResponseError
{
    private const int PreviewLimit = 2048;

    public static IonRequestException From(HttpResponseMessage response, ReadOnlyMemory<byte> body)
    {
        var status = (int)response.StatusCode;

        try
        {
            // The happy path for a failure: the server wrote a real IonProtocolError.
            var decoded = IonFormatterStorage<IonProtocolError>.Read(new CborReader(body));
            return new IonRequestException(decoded, status, null);
        }
        catch (Exception decodeFailure)
        {
            // Not an Ion error at all — a proxy, a load balancer, an ASP.NET error page, or a
            // failure that happened before the Ion handler ran. Carry everything we have.
            var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
                ? response.StatusCode.ToString()
                : response.ReasonPhrase;

            return new IonRequestException(
                IonProtocolError.UPSTREAM_ERROR(
                    $"HTTP {status} {reason}; the response body is not an Ion error ({decodeFailure.Message})"),
                status,
                Preview(body));
        }
    }

    /// <summary>Renders a body for a human: text when it is text, hex when it is not.</summary>
    private static string Preview(ReadOnlyMemory<byte> body)
    {
        if (body.Length == 0)
            return "<empty>";

        var span = body.Span;
        var probe = span[..Math.Min(span.Length, 512)];
        var printable = 0;

        foreach (var b in probe)
            if (b is 0x09 or 0x0A or 0x0D or (>= 0x20 and < 0x7F) or >= 0x80)
                printable++;

        // A CBOR payload the decoder rejected is still worth showing, just not as mojibake.
        if (probe.Length > 0 && printable * 10 < probe.Length * 9)
            return $"<{body.Length} bytes, not text> " +
                   Convert.ToHexString(span[..Math.Min(span.Length, 128)]).ToLowerInvariant();

        var text = Encoding.UTF8.GetString(span[..Math.Min(span.Length, PreviewLimit)]);
        return body.Length > PreviewLimit ? text + $"… (+{body.Length - PreviewLimit} bytes)" : text;
    }
}
