namespace ion.runtime.client;

using System.Runtime.CompilerServices;
using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Text;
using network;

/// <summary>The server answered the WebTransport CONNECT with something other than 200.</summary>
internal sealed class IonWebTransportRefusedException(int status, IonProtocolError? error)
    : Exception($"The server refused the WebTransport session with HTTP {status}" + (error is { } e ? $": {e.code} {e.msg}" : "."))
{
    public int Status { get; } = status;

    /// <summary>The Ion error the server put in the response body, when it put one there.</summary>
    public IonProtocolError? Error { get; } = error;
}

/// <summary>
/// A WebTransport session over HTTP/3, from nothing but <see cref="QuicConnection"/>: .NET has no
/// WebTransport client, and <c>HttpClient</c> cannot send the extended CONNECT one starts with.
/// </summary>
/// <remarks>
/// <para>The subset of HTTP/3 needed is small. The client opens its control stream and sends
/// SETTINGS enabling extended CONNECT, HTTP datagrams and WebTransport; waits for the server's
/// SETTINGS (which also confirms the server enables WebTransport before a request is wasted on it);
/// sends one CONNECT with <c>:protocol = webtransport</c>; and, on a 200, opens one bidirectional
/// stream tagged with the WebTransport stream signal and the session id. That stream carries the
/// Ion frames, each prefixed with its length as a QUIC varint.</para>
///
/// <para>This speaks draft-ietf-webtrans-http3-02 — the draft Kestrel implements, and the one
/// browsers negotiate with it — hence the <c>sec-webtransport-http3-draft02</c> header and the
/// <c>0x2b603742</c> setting.</para>
///
/// <para>Headers are QPACK-encoded against the static table only. The client advertises no dynamic
/// table, so a conforming server cannot reference one in its response either.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class WebTransportClientTransport : IonClientTransport
{
    private const ulong FrameData = 0x00;
    private const ulong FrameHeaders = 0x01;
    private const ulong FrameSettings = 0x04;
    private const ulong StreamTypeControl = 0x00;
    private const ulong SettingEnableConnectProtocol = 0x08;
    private const ulong SettingH3Datagram = 0x33;
    private const ulong SettingH3DatagramDraft = 0xffd277;
    private const ulong SettingEnableWebTransport = 0x2b603742;
    private const ulong WebTransportBidirectionalSignal = 0x41;
    private const long H3NoError = 0x100;
    private const long H3SettingsError = 0x109;
    private const long H3RequestCancelled = 0x10c;

    /// <summary>How often a CONNECT refused for SETTINGS the server had not read yet is sent again.</summary>
    private const int SettingsRaceRetries = 3;
    private const int MaxHandshakeFrame = 64 * 1024;

    private static readonly byte[] ControlPreamble = BuildControlPreamble();

    private readonly QuicConnection connection;
    private readonly QuicStream control;
    private readonly QuicStream session;
    private readonly QuicStream stream;
    private readonly Task inbound;
    private readonly CancellationTokenSource inboundCts;
    private readonly Task sessionMonitor;

    private byte[]? buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
    private int start;
    private int end;
    private int consumeOnNextReceive;
    private volatile bool peerFinished;
    private volatile bool outputCompleted;
    private int aborted;

    private WebTransportClientTransport(QuicConnection connection, QuicStream control, QuicStream session,
        QuicStream stream, Task inbound, CancellationTokenSource inboundCts)
    {
        this.connection = connection;
        this.control = control;
        this.session = session;
        this.stream = stream;
        this.inbound = inbound;
        this.inboundCts = inboundCts;
        sessionMonitor = MonitorSessionAsync();
    }

    public static bool IsSupported => QuicConnection.IsSupported;

    public override IonStreamTransportKind Kind => IonStreamTransportKind.WebTransport;

    public override bool IsClosed => peerFinished && outputCompleted;

    /// <summary>
    /// Opens the QUIC connection, trying every address the host resolves to in resolver order.
    /// </summary>
    /// <remarks>
    /// Given a name, MsQuic connects to the first address only. TCP falls through the list, QUIC did
    /// not: on Linux <c>localhost</c> resolves to <c>::1</c> first, so a server listening on
    /// <c>127.0.0.1</c> alone was unreachable over WebTransport (reported as an ALPN failure) while
    /// WebSockets to the same URL worked. The name stays the TLS target either way.
    /// </remarks>
    private static async Task<QuicConnection> ConnectQuicAsync(Uri uri, IonStreamClientOptions options, CancellationToken ct)
    {
        EndPoint[] endpoints = IPAddress.TryParse(uri.DnsSafeHost, out _)
            ? [new DnsEndPoint(uri.IdnHost, uri.Port)]
            : [.. (await Dns.GetHostAddressesAsync(uri.IdnHost, ct).ConfigureAwait(false)).Select(a => new IPEndPoint(a, uri.Port))];

        if (endpoints.Length == 0)
            endpoints = [new DnsEndPoint(uri.IdnHost, uri.Port)];

        for (var i = 0; ; i++)
        {
            try
            {
                return await QuicConnection.ConnectAsync(Options(endpoints[i]), ct).ConfigureAwait(false);
            }
            catch (Exception) when (i < endpoints.Length - 1 && !ct.IsCancellationRequested)
            {
                // The next address may be the one the server listens on; the last one's failure is the one reported.
            }
        }

        QuicClientConnectionOptions Options(EndPoint endpoint)
        {
            var quic = new QuicClientConnectionOptions
            {
                RemoteEndPoint = endpoint,
                DefaultStreamErrorCode = H3RequestCancelled,
                DefaultCloseErrorCode = H3NoError,
                MaxInboundUnidirectionalStreams = 8,
                MaxInboundBidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = [SslApplicationProtocol.Http3],
                    TargetHost = uri.IdnHost,
                    RemoteCertificateValidationCallback = options.ServerCertificateValidation
                }
            };

            // QUIC's own idle timer must outlast the Ion heartbeat, or the connection dies of silence the
            // heartbeat was about to break.
            if (options.ServerTimeout > TimeSpan.Zero)
                quic.IdleTimeout = options.ServerTimeout + options.ServerTimeout;
            if (options.KeepAliveInterval > TimeSpan.Zero)
                quic.KeepAliveInterval = options.KeepAliveInterval;

            return quic;
        }
    }

    /// <summary>Establishes the session and the Ion stream on it.</summary>
    /// <exception cref="IonWebTransportRefusedException">The server answered the CONNECT with a non-200 status.</exception>
    public static async Task<WebTransportClientTransport> ConnectAsync(Uri uri, IonStreamClientOptions options, CancellationToken ct)
    {
        var connection = await ConnectQuicAsync(uri, options, ct).ConfigureAwait(false);

        QuicStream? control = null, session = null, data = null;
        var inboundCts = new CancellationTokenSource();
        Task? inbound = null;

        try
        {
            control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct).ConfigureAwait(false);
            await control.WriteAsync(ControlPreamble, ct).ConfigureAwait(false);

            var serverSettings = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            inbound = AcceptInboundAsync(connection, serverSettings, inboundCts.Token);

            // A server that does not enable WebTransport says so in its SETTINGS, before a CONNECT is
            // spent on it.
            await serverSettings.Task.WaitAsync(ct).ConfigureAwait(false);

            // The server sends its SETTINGS when the connection opens, not in answer to ours — so
            // having them says nothing about whether it has read ours. QUIC does not order streams
            // among themselves, and a busy server can read the CONNECT before our control stream;
            // Kestrel then refuses the CONNECT with H3_SETTINGS_ERROR. Ours went out first, so by the
            // time a second CONNECT arrives they are there.
            for (var attempt = 1; ; attempt++)
            {
                session = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
                try
                {
                    await session.WriteAsync(BuildConnectRequest(uri), ct).ConfigureAwait(false);
                    var (status, error) = await ReadResponseAsync(session, ct).ConfigureAwait(false);
                    if (status != 200)
                        throw new IonWebTransportRefusedException(status, error);
                    break;
                }
                catch (QuicException ex) when (ex.ApplicationErrorCode == H3SettingsError && attempt < SettingsRaceRetries)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    session = null;
                    await Task.Delay(10 * attempt, ct).ConfigureAwait(false);
                }
            }

            data = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
            var header = new byte[16];
            var length = IonVarint.Write(header, WebTransportBidirectionalSignal);
            length += IonVarint.Write(header.AsSpan(length), (ulong)session.Id);
            await data.WriteAsync(header.AsMemory(0, length), ct).ConfigureAwait(false);

            return new WebTransportClientTransport(connection, control, session, data, inbound, inboundCts);
        }
        catch
        {
            inboundCts.Cancel();
            if (data is not null) await data.DisposeAsync().ConfigureAwait(false);
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            if (control is not null) await control.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            if (inbound is not null)
                try { await inbound.ConfigureAwait(false); } catch (Exception) { }
            inboundCts.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Watches the CONNECT stream, whose end is the session's end in draft-02. A server that aborts
    /// the session resets it, and the Ion stream on top may never hear about it directly — so a reset
    /// here kills the Ion stream at once. A clean end gets a moment's grace first: it can overtake the
    /// last bytes of the Ion stream, which travel on a different QUIC stream.
    /// </summary>
    private async Task MonitorSessionAsync()
    {
        var sink = new byte[256];
        var reset = false;
        try
        {
            while (await session.ReadAsync(sink, inboundCts.Token).ConfigureAwait(false) > 0)
            {
                // Capsules (a CLOSE_WEBTRANSPORT_SESSION, say) mean the session is ending; the stream's
                // end or reset that follows is what we act on.
            }
        }
        catch (OperationCanceledException) when (inboundCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            reset = true;
        }

        if (!reset)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), inboundCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        if (!peerFinished)
            Abort();
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public override async ValueTask<IonClientFrame> ReceiveAsync(int maxSize, CancellationToken ct)
    {
        var buf = buffer ?? throw new ObjectDisposedException(nameof(WebTransportClientTransport));

        start += consumeOnNextReceive;
        consumeOnNextReceive = 0;

        while (true)
        {
            var available = end - start;
            var frameSize = available + 1;

            if (IonVarint.TryRead(buf.AsSpan(start, available), out var length, out var prefix))
            {
                if (length > (ulong)maxSize)
                    throw new IonClientFrameTooLargeException(maxSize);

                frameSize = prefix + (int)length;
                if (available >= frameSize)
                {
                    consumeOnNextReceive = frameSize;
                    return new IonClientFrame(false, true, buf.AsMemory(start + prefix, (int)length));
                }
            }

            buf = MakeRoom(frameSize);

            var read = await stream.ReadAsync(buf.AsMemory(end), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (end - start > 0)
                    throw new IOException($"The WebTransport stream ended inside a frame ({end - start} bytes left over).");

                peerFinished = true;
                return IonClientFrame.Close;
            }

            end += read;
        }
    }

    /// <summary>Makes the buffer hold a <paramref name="frameSize"/>-byte frame from <see cref="start"/> with room to read more.</summary>
    private byte[] MakeRoom(int frameSize)
    {
        var buf = buffer!;
        var available = end - start;
        var needed = Math.Max(frameSize, available + 1);

        if (start + needed <= buf.Length && end < buf.Length)
            return buf;

        if (needed <= buf.Length)
        {
            buf.AsSpan(start, available).CopyTo(buf);
        }
        else
        {
            var grown = ArrayPool<byte>.Shared.Rent(Math.Max(needed, buf.Length * 2));
            buf.AsSpan(start, available).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(buf);
            buffer = buf = grown;
        }

        start = 0;
        end = available;
        return buf;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public override async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var prefix = IonVarint.Size((ulong)frame.Length);
        var rented = ArrayPool<byte>.Shared.Rent(prefix + frame.Length);
        try
        {
            // One write per frame: a separate write for the prefix could cost a packet of its own.
            IonVarint.Write(rented, (ulong)frame.Length);
            frame.Span.CopyTo(rented.AsSpan(prefix));
            await stream.WriteAsync(rented.AsMemory(0, prefix + frame.Length), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override ValueTask CloseOutputAsync(string? description, CancellationToken ct)
    {
        if (!outputCompleted)
        {
            outputCompleted = true;
            stream.CompleteWrites();
        }

        return ValueTask.CompletedTask;
    }

    public override void Abort()
    {
        if (Interlocked.Exchange(ref aborted, 1) == 1)
            return;

        try { stream.Abort(QuicAbortDirection.Both, H3RequestCancelled); }
        catch (Exception) { }

        try { session.Abort(QuicAbortDirection.Both, H3RequestCancelled); }
        catch (Exception) { }
    }

    public override async ValueTask DisposeAsync()
    {
        await inboundCts.CancelAsync().ConfigureAwait(false);

        // A FIN on the CONNECT stream is the session's graceful end in draft-02.
        if (Volatile.Read(ref aborted) == 0)
            try { session.CompleteWrites(); } catch (Exception) { }

        try
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await connection.CloseAsync(H3NoError, bound.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        await stream.DisposeAsync().ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);
        await control.DisposeAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);

        try { await inbound.ConfigureAwait(false); }
        catch (Exception) { }
        try { await sessionMonitor.ConfigureAwait(false); }
        catch (Exception) { }
        inboundCts.Dispose();

        var b = Interlocked.Exchange(ref buffer, null);
        if (b is not null)
            ArrayPool<byte>.Shared.Return(b);
    }

    // ── HTTP/3 plumbing ────────────────────────────────────────────────────────────────────────

    private static byte[] BuildControlPreamble()
    {
        Span<byte> settings = stackalloc byte[64];
        var n = 0;
        n += IonVarint.Write(settings[n..], SettingEnableConnectProtocol);
        n += IonVarint.Write(settings[n..], 1);
        // RFC 9297's datagram setting and the draft value Kestrel still checks for; an unknown
        // setting is ignored by definition, so sending both costs nothing.
        n += IonVarint.Write(settings[n..], SettingH3Datagram);
        n += IonVarint.Write(settings[n..], 1);
        n += IonVarint.Write(settings[n..], SettingH3DatagramDraft);
        n += IonVarint.Write(settings[n..], 1);
        n += IonVarint.Write(settings[n..], SettingEnableWebTransport);
        n += IonVarint.Write(settings[n..], 1);

        var preamble = new byte[1 + 1 + IonVarint.Size((ulong)n) + n];
        var p = IonVarint.Write(preamble, StreamTypeControl);
        p += IonVarint.Write(preamble.AsSpan(p), FrameSettings);
        p += IonVarint.Write(preamble.AsSpan(p), (ulong)n);
        settings[..n].CopyTo(preamble.AsSpan(p));
        return preamble;
    }

    /// <summary>A HEADERS frame carrying the extended CONNECT, QPACK-encoded against the static table.</summary>
    private static byte[] BuildConnectRequest(Uri uri)
    {
        var block = new ArrayBufferWriter<byte>(256);

        // Required Insert Count 0, Delta Base 0: no dynamic table.
        block.Write<byte>([0x00, 0x00]);
        // Indexed field lines, static table: 15 = :method CONNECT, 23 = :scheme https.
        block.Write<byte>([0xC0 | 15, 0xC0 | 23]);
        // Literal with static name reference: 0 = :authority, 1 = :path.
        WriteLiteralWithNameReference(block, 0, uri.Authority);
        WriteLiteralWithNameReference(block, 1, uri.PathAndQuery);
        // Literal names: neither is in the static table.
        WriteLiteralWithLiteralName(block, ":protocol", "webtransport");
        WriteLiteralWithLiteralName(block, "sec-webtransport-http3-draft02", "1");

        var frame = new byte[IonVarint.Size(FrameHeaders) + IonVarint.Size((ulong)block.WrittenCount) + block.WrittenCount];
        var n = IonVarint.Write(frame, FrameHeaders);
        n += IonVarint.Write(frame.AsSpan(n), (ulong)block.WrittenCount);
        block.WrittenSpan.CopyTo(frame.AsSpan(n));
        return frame;
    }

    private static void WriteLiteralWithNameReference(ArrayBufferWriter<byte> block, int staticIndex, string value)
    {
        // 01 N T index(4+): N = 0, T = 1 (static).
        WritePrefixedInteger(block, 0x50, 4, staticIndex);
        WriteString(block, 0x00, 7, value);
    }

    private static void WriteLiteralWithLiteralName(ArrayBufferWriter<byte> block, string name, string value)
    {
        // 001 N H length(3+): N = 0, H = 0.
        WriteString(block, 0x20, 3, name);
        WriteString(block, 0x00, 7, value);
    }

    private static void WriteString(ArrayBufferWriter<byte> block, byte pattern, int prefixBits, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WritePrefixedInteger(block, pattern, prefixBits, bytes.Length);
        block.Write(bytes);
    }

    /// <summary>An HPACK/QPACK prefixed integer (RFC 7541 §5.1).</summary>
    private static void WritePrefixedInteger(ArrayBufferWriter<byte> block, byte pattern, int prefixBits, int value)
    {
        var max = (1 << prefixBits) - 1;
        if (value < max)
        {
            block.Write<byte>([(byte)(pattern | value)]);
            return;
        }

        block.Write<byte>([(byte)(pattern | max)]);
        value -= max;
        while (value >= 0x80)
        {
            block.Write<byte>([(byte)((value & 0x7F) | 0x80)]);
            value >>= 7;
        }

        block.Write<byte>([(byte)value]);
    }

    /// <summary>Reads the CONNECT response: its status and, for a refusal, the Ion error in its body.</summary>
    private static async Task<(int Status, IonProtocolError? Error)> ReadResponseAsync(QuicStream request, CancellationToken ct)
    {
        var scratch = new byte[8];
        var status = 0;
        ArrayBufferWriter<byte>? body = null;

        while (true)
        {
            var type = await ReadVarintAsync(request, scratch, ct).ConfigureAwait(false);
            if (type is null)
                break;

            var length = await ReadVarintAsync(request, scratch, ct).ConfigureAwait(false)
                         ?? throw new IOException("The CONNECT response ended inside a frame header.");
            if (length > MaxHandshakeFrame)
                throw new IOException($"An HTTP/3 frame of {length} bytes in the CONNECT response.");

            var payload = new byte[(int)length];
            await request.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

            if (type == FrameHeaders && status == 0)
            {
                status = ReadStatus(payload);
                if (status == 200)
                    return (200, null);
            }
            else if (type == FrameData)
            {
                body ??= new ArrayBufferWriter<byte>();
                body.Write(payload);
            }
            // Anything else — trailers, reserved "grease" frame types — carries nothing we need.
        }

        IonProtocolError? error = null;
        if (body is { WrittenCount: > 0 })
        {
            try { error = IonStreamProtocol.ReadError(body.WrittenMemory); }
            catch (Exception) { }
        }

        return (status, error);
    }

    private static async ValueTask<ulong?> ReadVarintAsync(QuicStream stream, byte[] scratch, CancellationToken ct)
    {
        if (await stream.ReadAsync(scratch.AsMemory(0, 1), ct).ConfigureAwait(false) == 0)
            return null;

        var size = 1 << (scratch[0] >> 6);
        if (size > 1)
            await stream.ReadExactlyAsync(scratch.AsMemory(1, size - 1), ct).ConfigureAwait(false);

        IonVarint.TryRead(scratch.AsSpan(0, size), out var value, out _);
        return value;
    }

    /// <summary>The <c>:status</c> of a QPACK field section encoded against the static table.</summary>
    private static int ReadStatus(ReadOnlySpan<byte> block)
    {
        var pos = 0;
        ReadPrefixedInteger(block, ref pos, 8); // Required Insert Count
        ReadPrefixedInteger(block, ref pos, 7); // Delta Base

        while (pos < block.Length)
        {
            var b = block[pos];

            if ((b & 0x80) != 0)
            {
                // Indexed field line.
                var isStatic = (b & 0x40) != 0;
                var index = ReadPrefixedInteger(block, ref pos, 6);
                if (isStatic && StaticStatus(index) is { } status)
                    return status;
            }
            else if ((b & 0xC0) == 0x40)
            {
                // Literal field line with name reference.
                var isStatic = (b & 0x10) != 0;
                var index = ReadPrefixedInteger(block, ref pos, 4);
                var value = ReadStringValue(block, ref pos, 7, 0x80);
                if (isStatic && StaticStatus(index) is not null && value is not null && int.TryParse(value, out var status))
                    return status;
            }
            else if ((b & 0xE0) == 0x20)
            {
                // Literal field line with literal name.
                var name = ReadStringValue(block, ref pos, 3, 0x08);
                var value = ReadStringValue(block, ref pos, 7, 0x80);
                if (name == ":status" && value is not null && int.TryParse(value, out var status))
                    return status;
            }
            else
            {
                throw new IOException("The CONNECT response referenced a QPACK dynamic table the client never offered.");
            }
        }

        throw new IOException("The CONNECT response carried no :status.");
    }

    /// <summary>The status a QPACK static-table entry names, or null when the entry is not <c>:status</c>.</summary>
    private static int? StaticStatus(int index) => index switch
    {
        24 => 103, 25 => 200, 26 => 304, 27 => 404, 28 => 503,
        63 => 100, 64 => 204, 65 => 206, 66 => 302, 67 => 400, 68 => 403, 69 => 421, 70 => 425, 71 => 500,
        _ => null
    };

    private static int ReadPrefixedInteger(ReadOnlySpan<byte> block, ref int pos, int prefixBits)
    {
        var max = (1 << prefixBits) - 1;
        var value = block[pos++] & max;
        if (value < max)
            return value;

        var shift = 0;
        byte b;
        do
        {
            b = block[pos++];
            value += (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        return value;
    }

    /// <summary>A string literal; null when it is Huffman-coded, which only the status could need and never does.</summary>
    private static string? ReadStringValue(ReadOnlySpan<byte> block, ref int pos, int prefixBits, byte huffmanFlag)
    {
        var huffman = (block[pos] & huffmanFlag) != 0;
        var length = ReadPrefixedInteger(block, ref pos, prefixBits);
        var bytes = block.Slice(pos, length);
        pos += length;
        return huffman ? null : Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// Serves the server's unidirectional streams: reads SETTINGS off its control stream, and drains
    /// everything else (QPACK encoder/decoder streams, reserved types) so none of them blocks.
    /// </summary>
    private static async Task AcceptInboundAsync(QuicConnection connection, TaskCompletionSource serverSettings, CancellationToken ct)
    {
        var served = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var incoming = await connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
                served.Add(ServeAsync(incoming, serverSettings, ct));
            }
        }
        catch (Exception ex)
        {
            serverSettings.TrySetException(new IOException("The QUIC connection ended before the server's SETTINGS arrived.", ex));
        }

        foreach (var task in served)
            try { await task.ConfigureAwait(false); } catch (Exception) { }
    }

    private static async Task ServeAsync(QuicStream incoming, TaskCompletionSource serverSettings, CancellationToken ct)
    {
        await using var _ = incoming;
        var scratch = new byte[8];

        try
        {
            var type = await ReadVarintAsync(incoming, scratch, ct).ConfigureAwait(false);
            if (type == StreamTypeControl)
            {
                var frameType = await ReadVarintAsync(incoming, scratch, ct).ConfigureAwait(false);
                var length = await ReadVarintAsync(incoming, scratch, ct).ConfigureAwait(false);
                if (frameType != FrameSettings || length is null or > MaxHandshakeFrame)
                {
                    serverSettings.TrySetException(new IOException("The server's control stream did not start with SETTINGS."));
                    return;
                }

                var payload = new byte[(int)length];
                await incoming.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

                if (!EnablesWebTransport(payload))
                {
                    serverSettings.TrySetException(new NotSupportedException("The server does not enable WebTransport."));
                    return;
                }

                serverSettings.TrySetResult();
            }

            // Drain: the rest of the control stream (GOAWAY and the like), QPACK streams, anything else.
            var sink = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                while (await incoming.ReadAsync(sink, ct).ConfigureAwait(false) > 0)
                {
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sink);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or QuicException or IOException)
        {
        }
    }

    private static bool EnablesWebTransport(ReadOnlySpan<byte> settings)
    {
        var pos = 0;
        while (pos < settings.Length)
        {
            if (!IonVarint.TryRead(settings[pos..], out var id, out var idSize))
                return false;
            pos += idSize;
            if (!IonVarint.TryRead(settings[pos..], out var value, out var valueSize))
                return false;
            pos += valueSize;

            if (id == SettingEnableWebTransport && value != 0)
                return true;
        }

        return false;
    }
}
