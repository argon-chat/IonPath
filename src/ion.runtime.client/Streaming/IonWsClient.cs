namespace ion.runtime.client;

using System.Collections.Concurrent;
using System.Formats.Cbor;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using network;

/// <summary>
/// The client side of one <c>stream</c> method. Generated clients create one per call.
/// </summary>
/// <remarks>
/// <para>A call tries the transports of <see cref="IonStreamClientOptions.Transports"/> in order
/// until one completes the handshake — connected, arguments sent, READY received — and runs on it.
/// Each attempt exchanges its own ticket, because a ticket may be good for one connection only.
/// A WebTransport attempt that fails is remembered per server for
/// <see cref="IonStreamClientOptions.WebTransportRetryAfter"/>, so a network that drops UDP costs
/// one timeout, not one per call.</para>
///
/// <para>The server refusing the call — a rejected ticket, a connect hook that threw — is not a
/// transport failure and does not fall through: the next transport would be refused the same way.</para>
///
/// <para>A connection that drops is reconnected per <see cref="IonStreamClientOptions.Reconnect"/>:
/// resumed when the server kept the session, started afresh when it did not (see
/// <see cref="IonStreamClientOptions.Resume"/>). The consumer sees none of it but the pause.</para>
///
/// <para>How the enumeration ends tells the caller what happened: it completes when the server's
/// stream method completed; it throws <see cref="IonRequestException"/> with the server's error,
/// <see cref="IonStreamClosedException"/> when the server closed the stream on purpose, and
/// <see cref="IonStreamDisconnectedException"/> when the transport died or went silent and was not
/// (or could no longer be) reconnected. Breaking out of the loop or cancelling leaves gracefully —
/// the server's disconnect hook sees a client that closed, not a dropped connection.</para>
/// </remarks>
public class IonWsClient(IonClientContext context, Type interfaceName, MethodInfo methodName) : IIonStreamConnector
{
    private static readonly ConcurrentDictionary<string, long> WebTransportFailedUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets every remembered WebTransport failure.</summary>
    public static void ResetTransportHealth() => WebTransportFailedUntil.Clear();

    Type IIonStreamConnector.Interface => interfaceName;

    MethodInfo IIonStreamConnector.Method => methodName;

    public IAsyncEnumerable<TResponse> CallServerStreamingAsync<TResponse>(
        ReadOnlyMemory<byte> requestPayload,
        CancellationToken ct = default)
        => RunAsync<TResponse, object>(requestPayload, null, ct);

    public IAsyncEnumerable<TResponse> CallServerStreamingAsync<TResponse, TRequest>(
        ReadOnlyMemory<byte> requestPayload,
        IAsyncEnumerable<TRequest>? inputStream,
        CancellationToken ct = default)
        => RunAsync<TResponse, TRequest>(requestPayload, inputStream, ct);

    private async IAsyncEnumerable<TResponse> RunAsync<TResponse, TRequest>(
        ReadOnlyMemory<byte> requestPayload,
        IAsyncEnumerable<TRequest>? inputStream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var call = IonStreamCall.Start(this, context.StreamOptions, requestPayload, inputStream);

        CborReader? reader = null;
        while (await call.ReadAsync(ct).ConfigureAwait(false) is { } payload)
        {
            // One reader for the whole call: Reset re-points it at the next payload.
            if (reader is null) reader = new CborReader(payload);
            else reader.Reset(payload);

            yield return IonFormatterStorage<TResponse>.Read(reader);
        }
    }

    /// <summary>Runs the transport order until one transport completes <paramref name="handshake"/>.</summary>
    async Task<T> IIonStreamConnector.ConnectAsync<T>(Func<IonClientTransport, CancellationToken, Task<T>> handshake, CancellationToken ct)
    {
        var options = context.StreamOptions;
        var baseUri = context.HttpClient.BaseAddress
                      ?? throw new InvalidOperationException("The client's HttpClient has no BaseAddress.");

        List<Exception>? failures = null;

        // A WebTransport that failed recently is skipped only while something else is left to try:
        // a WebTransport-only client keeps trying it — a reconnecting one would otherwise give up.
        var kinds = options.Transports.Distinct().Where(k => CanUse(k, baseUri)).ToList();
        if (kinds.Count > 1 && kinds.Contains(IonStreamTransportKind.WebTransport) && !IsHealthy(baseUri))
            kinds.Remove(IonStreamTransportKind.WebTransport);

        foreach (var kind in kinds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var transport = kind switch
                {
                    IonStreamTransportKind.WebTransport => await OpenWebTransportAsync(baseUri, options, ct).ConfigureAwait(false),
                    _ => await OpenWebSocketAsync(baseUri, options, ct).ConfigureAwait(false)
                };

                return await handshake(transport, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Whatever the transport threw while being cancelled — a DNS lookup cancelled
                // mid-flight surfaces as a SocketException — the caller asked to stop.
                throw new OperationCanceledException(ct);
            }
            catch (Exception ex) when (IsTransportFailure(ex, ct))
            {
                if (kind == IonStreamTransportKind.WebTransport && options.WebTransportRetryAfter > TimeSpan.Zero)
                    WebTransportFailedUntil[baseUri.Authority] =
                        Environment.TickCount64 + (long)options.WebTransportRetryAfter.TotalMilliseconds;

                (failures ??= []).Add(ex);
            }
        }

        if (failures is null)
            throw new InvalidOperationException(
                $"None of the configured stream transports ({string.Join(", ", options.Transports)}) can reach {baseUri}.");

        if (failures.Count == 1 && failures[0] is IonStreamDisconnectedException single)
            throw single;

        throw new IonStreamDisconnectedException(IonDisconnectReason.TransportLost,
            $"Every stream transport failed: {string.Join("; ", failures.Select(f => f.Message))}",
            failures.Count == 1 ? failures[0] : new AggregateException(failures));
    }

    /// <summary>Whether <paramref name="kind"/> can work here at all: WebTransport needs HTTPS and QUIC.</summary>
    private static bool CanUse(IonStreamTransportKind kind, Uri baseUri)
    {
        if (kind != IonStreamTransportKind.WebTransport)
            return true;

        if (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        return (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) &&
               WebTransportClientTransport.IsSupported;
    }

    /// <summary>False while a recent WebTransport failure to this server is remembered.</summary>
    private static bool IsHealthy(Uri baseUri)
        => !WebTransportFailedUntil.TryGetValue(baseUri.Authority, out var until) || Environment.TickCount64 >= until;

    /// <summary>
    /// Whether the next transport deserves a try. The server refusing the call does not: a rejected
    /// ticket or a connect hook that threw would be refused again on any transport.
    /// </summary>
    private static bool IsTransportFailure(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;

        return ex switch
        {
            IonStreamClosedException => false,
            IonStreamDisconnectedException => true,
            IonRequestException => false,
            _ => true
        };
    }

    /// <summary>The query a call's upgrade carries: <c>resume=1</c> when it asks for a resumable session.</summary>
    private static string Query(IonStreamClientOptions options)
        => options.Resumable ? $"{IonStreamProtocol.ResumeQueryParameter}=1" : "";

    private async Task<IonClientTransport> OpenWebSocketAsync(Uri baseUri, IonStreamClientOptions options, CancellationToken ct)
    {
        var ticket = await CreateExchangeTokenAsync(ct).ConfigureAwait(false);
        var query = Query(options);
        var uri = new Uri(ToWebSocketUri(baseUri),
            $"/ion/{interfaceName.Name}/{methodName.Name}.ws" + (query.Length == 0 ? "" : "?" + query));
        var protocols = new[] { IonStreamProtocol.SubProtocol(ticket, IonStreamProtocol.Version) };

        using var connectBound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.HandshakeTimeout > TimeSpan.Zero)
            connectBound.CancelAfter(options.HandshakeTimeout);

        WebSocket ws;
        try
        {
            ws = context.WebSocketFactory is { } factory
                ? await factory(uri, connectBound.Token, protocols).ConfigureAwait(false)
                : await ConnectDefaultWebSocketAsync(uri, protocols, options, connectBound.Token).ConfigureAwait(false);
        }
        catch (IonRequestException)
        {
            // The server answered the upgrade with a refusal; see ConnectDefaultWebSocketAsync.
            throw;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IonStreamDisconnectedException(IonDisconnectReason.Timeout,
                $"The WebSocket to {uri} did not open within {options.HandshakeTimeout}.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IonStreamDisconnectedException(IonDisconnectReason.TransportLost,
                $"The WebSocket to {uri} did not open: {ex.Message}", ex);
        }

        if (!string.IsNullOrEmpty(ws.SubProtocol) && IonStreamProtocol.ParseVersion(ws.SubProtocol) != IonStreamProtocol.Version)
        {
            ws.Abort();
            ws.Dispose();
            throw new IonStreamDisconnectedException(IonDisconnectReason.ProtocolViolation,
                $"The server negotiated '{ws.SubProtocol}', not Ion stream protocol v{IonStreamProtocol.Version}.");
        }

        return new WebSocketClientTransport(ws);
    }

    private async Task<IonClientTransport> OpenWebTransportAsync(Uri baseUri, IonStreamClientOptions options, CancellationToken ct)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
            throw new PlatformNotSupportedException("WebTransport needs QUIC.");

        var ticket = await CreateExchangeTokenAsync(ct).ConfigureAwait(false);
        var query = Query(options);
        var uri = new Uri(baseUri,
            $"/ion/{interfaceName.Name}/{methodName.Name}.wt?ticket={Uri.EscapeDataString(ticket)}&ver={IonStreamProtocol.Version}" +
            (query.Length == 0 ? "" : "&" + query));

        using var connectBound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.WebTransportConnectTimeout > TimeSpan.Zero)
            connectBound.CancelAfter(options.WebTransportConnectTimeout);

        try
        {
            return await WebTransportClientTransport.ConnectAsync(uri, options, connectBound.Token).ConfigureAwait(false);
        }
        catch (IonWebTransportRefusedException refused) when (refused.Status is 401 or 403 && refused.Error is { } error)
        {
            // The server looked at the ticket and said no. So would every other transport.
            throw new IonRequestException(error, refused.Status, null);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IonStreamDisconnectedException(IonDisconnectReason.Timeout,
                $"WebTransport to {baseUri.Authority} did not connect within {options.WebTransportConnectTimeout}.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IonStreamDisconnectedException(IonDisconnectReason.TransportLost,
                $"WebTransport to {baseUri.Authority} failed: {ex.Message}", ex);
        }
    }

    /// <remarks>
    /// The upgrade's status is collected, so that a server that refused it — a ticket it rejected
    /// (401), a method it does not have (412), a protocol version it does not speak (426) — is told
    /// apart from a network that failed: the first is final, reconnecting only hammers it.
    /// </remarks>
    private static async Task<WebSocket> ConnectDefaultWebSocketAsync(
        Uri uri, string[] protocols, IonStreamClientOptions options, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        foreach (var protocol in protocols)
            ws.Options.AddSubProtocol(protocol);
        if (options.ServerCertificateValidation is { } validate)
            ws.Options.RemoteCertificateValidationCallback = validate;
        ws.Options.CollectHttpResponseDetails = true;

        try
        {
            await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            return ws;
        }
        catch (WebSocketException ex) when ((int)ws.HttpStatusCode is >= 400 and < 500 and not 408 and not 429)
        {
            var status = (int)ws.HttpStatusCode;
            var code = ws.HttpResponseHeaders?.TryGetValue("X-Ion-Status", out var values) == true
                ? values.FirstOrDefault() ?? "UPGRADE_REFUSED"
                : "UPGRADE_REFUSED";
            ws.Dispose();
            throw new IonRequestException(new IonProtocolError(code, $"The server refused the stream with HTTP {status}: {ex.Message}"), status, null);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

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

    // ── ticket exchange ────────────────────────────────────────────────────────────────────────

    private async Task<string> CreateExchangeTokenAsync(CancellationToken ct)
    {
        using var callContext = new IonCallContext(context.serviceProvider, context.HttpClient,
            interfaceName, methodName, typeof(void), ReadOnlyMemory<byte>.Empty);

        Func<IIonCallContext, CancellationToken, Task> next = TerminalExchangeAsync;
        for (var i = context.Interceptors.Count - 1; i >= 0; i--)
        {
            var interceptor = context.Interceptors[i];
            var currentNext = next;
            next = (cr, token) => interceptor.InvokeAsync(cr, currentNext, token);
        }

        await next(callContext, ct).ConfigureAwait(false);

        var reader = new CborReader(callContext.ResponsePayload ?? []);
        reader.ReadStartArray();
        var tokenBytes = reader.ReadByteString();
        reader.ReadEndArray();

        return ToBase56(tokenBytes);
    }

    private static async Task TerminalExchangeAsync(IIonCallContext callContext, CancellationToken ct)
    {
        if (callContext is not IonCallContext c)
            throw new InvalidOperationException("Invalid configuration, call context broken");

        c.HttpRequest ??= new HttpRequestMessage(HttpMethod.Post, "/ion.att")
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
            callContext.ResponseItems[header.Key] = string.Join(",", header.Value);

        if (!c.HttpResponse.IsSuccessStatusCode)
            throw IonResponseError.From(c.HttpResponse, buf);

        if (buf.Length == 0)
            throw new IonRequestException(IonProtocolError.UPSTREAM_ERROR("Empty response from ion.att"));
    }

    internal static string ToBase56(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";
        const int @base = 56;

        var value = BigInteger.Zero;
        foreach (var b in bytes)
            value = (value << 8) + b;

        var leadingZeroes = 0;
        foreach (var b in bytes)
        {
            if (b == 0) leadingZeroes++;
            else break;
        }

        // One '2' per leading zero byte, then the number — the same encoding as the TypeScript and
        // Rust clients. A lone extra '2' for an all-zero value (as this used to add) would decode
        // as one more zero byte than was sent.
        var result = new StringBuilder();
        while (value > 0)
        {
            var rem = (int)(value % @base);
            value /= @base;
            result.Insert(0, alphabet[rem]);
        }

        if (leadingZeroes > 0)
            result.Insert(0, new string(alphabet[0], leadingZeroes));

        return result.ToString();
    }
}
