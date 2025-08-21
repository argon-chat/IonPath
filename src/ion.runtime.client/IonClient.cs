namespace ion.runtime.client;

using Microsoft.Extensions.DependencyInjection;
using network;
using System.Buffers;
using System.Diagnostics;
using System.Formats.Cbor;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
public delegate Task<WebSocket> IonWebSocketFactory(Uri uri, CancellationToken ct);
public class IonClient
{
    private readonly IonClientContext _context;

    private IonClient(IonClientContext context) => _context = context;

    private static async Task<WebSocket> Default(Uri uri, CancellationToken ct)
    {
        var cws = new ClientWebSocket();
        await cws.ConnectAsync(uri, ct);
        return cws;
    }

    public static IonClient Create(string endpoint, HttpClientHandler? httpHandle, IonWebSocketFactory? webSocketClient)
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
}

public class IonClientContext(HttpClient client, IonWebSocketFactory wsFactory)
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

    public async IAsyncEnumerable<TResponse> CallServerStreamingAsync<TResponse>(
        ReadOnlyMemory<byte> requestPayload,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var wsUri = new Uri(ToWebSocketUri(context.HttpClient.BaseAddress!), $"/ion/{interfaceName.Name}/{methodName.Name}.ws");

        var ws = await context.WebSocketClient(wsUri, ct);

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

        var ctx = new IonCallContext(httpClient, interfaceName, methodName, typeof(void), payload);

        var next = TerminalAsync;
        for (var i = context.Interceptors.Count - 1; i >= 0; i--)
        {
            var interceptor = context.Interceptors[i];
            var currentNext = next;
            next = (c, token) => interceptor.InvokeAsync(c, currentNext, token);
        }

        await next(ctx, ct).ConfigureAwait(false);

        return;

        async Task TerminalAsync(IonCallContext c, CancellationToken token)
        {
            c.HttpRequest ??=
                new HttpRequestMessage(HttpMethod.Post, $"/ion/{c.InterfaceName.Name}/{c.MethodName.Name}.unary")
                {
                    Content = new ReadOnlyMemoryContent(c.RequestPayload)
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue(IonContentType) }
                    }
                };

            c.HttpResponse?.Dispose();
            c.HttpResponse = await c.Client.SendAsync(c.HttpRequest, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

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
                    throw new IonRequestException(IonProtocolError.UPSTREAM_ERROR(c.HttpResponse.ReasonPhrase ??
                        c.HttpResponse.StatusCode.ToString()));
                }
            }
        }
    }

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
            c.HttpRequest ??=
                new HttpRequestMessage(HttpMethod.Post, $"/ion/{c.InterfaceName.Name}/{c.MethodName.Name}.unary")
                {
                    Content = new ReadOnlyMemoryContent(c.RequestPayload)
                    {
                        Headers = { ContentType = new MediaTypeHeaderValue(IonContentType) }
                    }
                };

            c.HttpResponse?.Dispose();
            c.HttpResponse = await c.Client.SendAsync(c.HttpRequest, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

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
                    throw new IonRequestException(IonProtocolError.UPSTREAM_ERROR(c.HttpResponse.ReasonPhrase ??
                        c.HttpResponse.StatusCode.ToString()));
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