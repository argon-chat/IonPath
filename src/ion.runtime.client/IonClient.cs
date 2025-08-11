namespace ion.runtime.client;

using network;
using System.Diagnostics;
using System.Formats.Cbor;
using System.Net.Http.Headers;

public static class IonClient
{
}

public class IonRequest(HttpClient client, string endpoint, string interfaceName, string methodName)
{
    public static string IonContentType = "application/ion; charset=binary; ver=1";
    private readonly List<IIonInterceptor> interceptors = new();

    public IonRequest Use(IIonInterceptor interceptor)
    {
        interceptors.Add(interceptor);
        return this;
    }
    public IonRequest Use(params IIonInterceptor[] args)
    {
        interceptors.AddRange(args);
        return this;
    }

    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        TRequest request,
        CancellationToken ct = default)
    {
        var writer = new CborWriter();
        IonFormatterStorage<TRequest>.Write(writer, request);
        var payload = writer.Encode();

        var ctx = new IonCallContext(client, endpoint, interfaceName, methodName, typeof(TRequest), typeof(TResponse), payload);

        var next = TerminalAsync;
        for (var i = interceptors.Count - 1; i >= 0; i--)
        {
            var interceptor = interceptors[i];
            var currentNext = next;
            next = (c, token) => interceptor.InvokeAsync(c, currentNext, token);
        }

        await next(ctx, ct).ConfigureAwait(false);

        var reader = new CborReader(ctx.ResponsePayload!);
        return IonFormatterStorage<TResponse>.Read(reader);

        async Task TerminalAsync(IonCallContext c, CancellationToken token)
        {
            c.HttpRequest ??= new HttpRequestMessage(HttpMethod.Post, $"{c.Endpoint.TrimEnd('/')}/ion/{c.InterfaceName}/{c.MethodName}")
            {
                Content = new ByteArrayContent(c.RequestPayload)
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
                var error = IonFormatterStorage<IonProtocolError>.Read(new CborReader(respBytes));
                throw new IonRequestException(error);
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
    string endpoint,
    string iface,
    string method,
    Type req,
    Type resp,
    byte[] requestPayload)
{
    public HttpClient Client { get; } = client;
    public string Endpoint { get; } = endpoint;
    public string InterfaceName { get; } = iface;
    public string MethodName { get; } = method;

    public Type RequestType { get; } = req;
    public Type ResponseType { get; } = resp;

    /// <summary>Сериализованное тело запроса (CBOR). Интерцептор может заменить/перезаписать.</summary>
    public byte[] RequestPayload { get; set; } = requestPayload;

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