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

    private static async Task<WebSocket> Default(Uri uri, CancellationToken ct, string[]? protocols = null)
    {
        var cws = new ClientWebSocket();
        protocols ??= [];

        foreach (var protocol in protocols) 
            cws.Options.AddSubProtocol(protocol);

        await cws.ConnectAsync(uri, ct);
        return cws;
    }

    public static IonClient Create(string endpoint, IServiceProvider provider, HttpClientHandler? httpHandle = null, IonWebSocketFactory? webSocketClient = null)
        => new(new IonClientContext(new HttpClient(httpHandle ?? new HttpClientHandler())
        {
            BaseAddress = new Uri(endpoint)
        }, webSocketClient ?? Default, provider));

    public static IonClient Create(string endpoint, HttpClientHandler? httpHandle = null, IonWebSocketFactory? webSocketClient = null)
        => new(new IonClientContext(new HttpClient(httpHandle ?? new HttpClientHandler())
        {
            BaseAddress = new Uri(endpoint)
        }, webSocketClient ?? Default));

    public static IonClient Create(HttpClient client, IonWebSocketFactory wsFactory)
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

    public T ForService<T>(AsyncServiceScope scope) where T : IIonService =>
        IonExecutorMetadataStorage.TakeClient<T>(scope, _context);

    public T ForService<T>(IServiceProvider provider) where T : IIonService =>
        IonExecutorMetadataStorage.TakeClient<T>(provider, _context);
}

public class IonClientContext(HttpClient client, IonWebSocketFactory wsFactory, IServiceProvider? serviceProvider = null)
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
    public IonWebSocketFactory WebSocketClient => wsFactory;

    public IReadOnlyList<IIonInterceptor> Interceptors => interceptors;
}

public class IonWsClient(IonClientContext context, Type interfaceName, MethodInfo methodName)
{
    private static Uri ToWebSocketUri(Uri uri)
    {
        var targetScheme = uri.Scheme switch
        {
            "http" => "ws",
            "https" => "wss",
            "ws" => "ws",
            "wss" => "wss",
            _ => throw new ArgumentException("Invalid Scheme", nameof(uri))
        };

        var b = new UriBuilder(uri) { Scheme = targetScheme };

        if (uri.IsDefaultPort) b.Port = -1;

        return b.Uri;
    }

    private static async Task TerminalExchangeAsync(
        IIonCallContext callContext,
        HttpClient http,
        CancellationToken ct)
    {
        if (callContext is not IonCallContext c)
            throw new InvalidOperationException($"Invalid configuration, call context broken");

        c.HttpRequest ??=
            new HttpRequestMessage(HttpMethod.Post, "/ion.att")
            {
                Content = new ReadOnlyMemoryContent(c.RequestPayload)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/ion") }
                }
            };

        foreach (var (hKey, hValue) in c.RequestItems)
            c.HttpRequest.Headers.Add(hKey, hValue);

        c.HttpResponse?.Dispose();
        c.HttpResponse = await c.Client.SendAsync(c.HttpRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        var buf = await c.HttpResponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        c.ResponsePayload = buf;

        foreach (var header in c.HttpResponse.Headers) 
            callContext.ResponseItems.Add(header.Key, header.Value.ToString() ?? "");

        if (!c.HttpResponse.IsSuccessStatusCode)
            throw IonResponseError.From(c.HttpResponse, buf);

        if (buf.Length == 0)
            throw new IonRequestException(IonProtocolError.UPSTREAM_ERROR("Empty response from ion.att"));
    }


    private static async Task<string> CreateExchangeTokenAsync(
        IIonCallContext callContext,
        IonClientContext context,
        CancellationToken ct)
    {

        if (callContext is not IonCallContext c)
            throw new InvalidOperationException($"Invalid configuration, call context broken");
        
        Func<IIonCallContext, CancellationToken, Task> next =
            (cr, token) => TerminalExchangeAsync(cr, context.HttpClient, token);

        for (var i = context.Interceptors.Count - 1; i >= 0; i--)
        {
            var interceptor = context.Interceptors[i];
            var currentNext = next;
            next = (cr, token) => interceptor.InvokeAsync(cr, currentNext, token);
        }

        await next(callContext, ct).ConfigureAwait(false);

        var reader = new CborReader(c.ResponsePayload.ToArray());
        reader.ReadStartArray();
        var tokenBytes = reader.ReadByteString();
        reader.ReadEndArray();

        return ToBase56(tokenBytes);
    }

    private static string ToBase56(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";
        const int @base = 56;

        var value = BigInteger.Zero;
        foreach (var b in bytes)
        {
            value = (value << 8) + b;
        }

        var leadingZeroes = 0;
        foreach (var b in bytes)
        {
            if (b == 0) leadingZeroes++;
            else break;
        }

        var result = new StringBuilder();
        while (value > 0)
        {
            var rem = (int)(value % @base);
            value /= @base;
            result.Insert(0, alphabet[rem]);
        }

        if (result.Length == 0)
            result.Append(alphabet[0]);

        if (leadingZeroes > 0)
            result.Insert(0, new string(alphabet[0], leadingZeroes));

        return result.ToString();
    }

    public async IAsyncEnumerable<TResponse> CallServerStreamingAsync<TResponse>(
        ReadOnlyMemory<byte> requestPayload,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await CreateExchangeTokenAsync(new IonCallContext(context.serviceProvider, context.HttpClient, null, null, null, ReadOnlyMemory<byte>.Empty), context, ct)
            .ConfigureAwait(false);

        var wsUri = new Uri(ToWebSocketUri(context.HttpClient.BaseAddress!), $"/ion/{interfaceName.Name}/{methodName.Name}.ws");

        var ws = await context.WebSocketClient(wsUri, ct, [$"ion!ticket#{token}!ver#1"]); ;

        await ws.SendAsync(requestPayload, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            var ms = new MemoryStream(capacity: 64 * 1024);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    ms.Position = 0;
                    ms.SetLength(0);

                    WebSocketReceiveResult result;
                    do
                    {
                        var segment = new ArraySegment<byte>(buffer);
                        result = await ws.ReceiveAsync(segment, ct).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await CloseGracefully(ws, ct).ConfigureAwait(false);
                            yield break;
                        }

                        if (result.Count > 0)
                            ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var msg = ms.GetBuffer();
                    var msgLen = (int)ms.Length;
                    if (msgLen == 0)
                        continue;

                    var opcode = msg[0];

                    switch (opcode)
                    {
                        case 0x00:
                        {
                            var span = new ReadOnlySpan<byte>(msg, 1, msgLen - 1);
                            var reader = new CborReader(span.ToArray());
                            var item = IonFormatterStorage<TResponse>.Read(reader);
                            yield return item;
                            break;
                        }

                        case 0x01:
                        {
                            await CloseGracefully(ws, ct).ConfigureAwait(false);
                            yield break;
                        }
                        case 0x02:
                        {
                            var span = new ReadOnlySpan<byte>(msg, 1, msgLen - 1);
                            var reader = new CborReader(span.ToArray());
                            var error = IonFormatterStorage<IonProtocolError>.Read(reader);
                            try
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "error", ct)
                                    .ConfigureAwait(false);
                            }
                            catch
                            {
                                /* ignore */
                            }

                            throw new IonRequestException(error);
                        }

                        default:
                            var lastItem = default(TResponse?);
                            try
                            {
                                var reader = new CborReader(new ReadOnlySpan<byte>(msg, 0, msgLen).ToArray());
                                lastItem = IonFormatterStorage<TResponse>.Read(reader);
                            }
                            catch (Exception ex)
                            {
                                try
                                {
                                    await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "invalid frame", ct)
                                        .ConfigureAwait(false);
                                }
                                catch { }
                                throw new IonRequestException(
                                    IonProtocolError.UPSTREAM_ERROR($"Invalid WS frame: {ex.Message}"));
                            }

                            if (lastItem is not null)
                                yield return lastItem;

                            break;
                    }
                }
            }
            finally
            {
                await ms.DisposeAsync();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    public async IAsyncEnumerable<TResponse> CallServerStreamingAsync<TResponse, TRequest>(
        ReadOnlyMemory<byte> requestPayload,
        IAsyncEnumerable<TRequest>? inputStream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await CreateExchangeTokenAsync(new IonCallContext(context.serviceProvider, context.HttpClient, null, null, null, ReadOnlyMemory<byte>.Empty), context, ct)
            .ConfigureAwait(false);

        var wsUri = new Uri(ToWebSocketUri(context.HttpClient.BaseAddress!), $"/ion/{interfaceName.Name}/{methodName.Name}.ws");

        var ws = await context.WebSocketClient(wsUri, ct, [$"ion!ticket#{token}!ver#1"]); ;

        await ws.SendAsync(requestPayload, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);

        var writerTask = Task.CompletedTask;
        if (inputStream is not null)
        {
            writerTask = SendInputStreamAsync<TRequest>(ws, inputStream, ct);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var ms = new MemoryStream(capacity: 64 * 1024);

        try
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    ms.Position = 0;
                    ms.SetLength(0);

                    WebSocketReceiveResult result;
                    do
                    {
                        var segment = new ArraySegment<byte>(buffer);
                        result = await ws.ReceiveAsync(segment, ct).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await CloseGracefully(ws, ct).ConfigureAwait(false);
                            yield break;
                        }

                        if (result.Count > 0)
                            ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var msg = ms.GetBuffer();
                    var msgLen = (int)ms.Length;
                    if (msgLen == 0)
                        continue;

                    var opcode = msg[0];

                    switch (opcode)
                    {
                        case 0x00:
                        {
                            var span = new ReadOnlySpan<byte>(msg, 1, msgLen - 1);
                            var reader = new CborReader(span.ToArray());
                            var item = IonFormatterStorage<TResponse>.Read(reader);
                            yield return item;
                            break;
                        }

                        case 0x01:
                        {
                            await CloseGracefully(ws, ct).ConfigureAwait(false);
                            yield break;
                        }
                        case 0x02:
                        {
                            var span = new ReadOnlySpan<byte>(msg, 1, msgLen - 1);
                            var reader = new CborReader(span.ToArray());
                            var error = IonFormatterStorage<IonProtocolError>.Read(reader);
                            try
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "error", ct)
                                    .ConfigureAwait(false);
                            }
                            catch
                            {
                                /* ignore */
                            }

                            throw new IonRequestException(error);
                        }

                        default:
                        var lastItem = default(TResponse?);
                        try
                        {
                            var reader = new CborReader(new ReadOnlySpan<byte>(msg, 0, msgLen).ToArray());
                            lastItem = IonFormatterStorage<TResponse>.Read(reader);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "invalid frame", ct)
                                    .ConfigureAwait(false);
                            }
                            catch { }
                            throw new IonRequestException(
                                IonProtocolError.UPSTREAM_ERROR($"Invalid WS frame: {ex.Message}"));
                        }

                        if (lastItem is not null)
                            yield return lastItem;

                        break;
                    }
                }
            }
            finally
            {
                await ms.DisposeAsync();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await ms.DisposeAsync();
            try { await writerTask; } catch { /* ignore */ }
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private static async Task SendInputStreamAsync<TRequest>(
        WebSocket ws,
        IAsyncEnumerable<TRequest> inputStream,
        CancellationToken ct)
    {
        await foreach (var item in inputStream.WithCancellation(ct))
        {
            var writer = new CborWriter();
            writer.WriteStartArray(1);
            IonFormatterStorage<TRequest>.Write(writer, item);
            writer.WriteEndArray();
            var payload = writer.Encode();

            var rented = ArrayPool<byte>.Shared.Rent(payload.Length + 1);
            try
            {
                rented[0] = 0x00;
                payload.CopyTo(rented.AsSpan(1));
                await ws.SendAsync(
                    new ArraySegment<byte>(rented, 0, payload.Length + 1),
                    WebSocketMessageType.Binary,
                    true,
                    ct
                ).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        await ws.SendAsync(
            new ArraySegment<byte>([0x00]),
            WebSocketMessageType.Binary,
            true,
            ct
        ).ConfigureAwait(false);
    }

    private static async Task CloseGracefully(WebSocket ws, CancellationToken ct)
    {
        if (ws.State == WebSocketState.CloseReceived)
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", ct).ConfigureAwait(false); } catch { }
    }
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
            {
                try
                {
                    var error = IonFormatterStorage<IonProtocolError>.Read(new CborReader(respBytes));
                    throw new IonRequestException(error);
                }
                catch
                {
                    throw new IonRequestException(
                        IonProtocolError.UPSTREAM_ERROR(
                            c.HttpResponse.ReasonPhrase ?? c.HttpResponse.StatusCode.ToString()
                        )
                    );
                }
            }
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
