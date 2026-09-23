namespace IonTestClientServer.Streaming;

using System.Formats.Cbor;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using ion.runtime;

/// <summary>A WebSocket speaking the Ion wire by hand.</summary>
internal sealed class RawStream : IAsyncDisposable
{
    public ClientWebSocket Ws { get; } = new();
    private readonly byte[] buffer = new byte[1 << 20];

    public static async Task<string> TicketAsync(LabHost host, string user)
    {
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
        {
            BaseAddress = host.BaseAddress
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ion.att")
        {
            Content = new ByteArrayContent([])
        };
        request.Content.Headers.ContentType = new("application/ion");
        request.Headers.Add(LabTickets.UserHeader, user);
        using var response = await http.SendAsync(request);
        var reader = new CborReader(await response.Content.ReadAsByteArrayAsync());
        reader.ReadStartArray();
        var bytes = reader.ReadByteString();
        return Base56(bytes);
    }

    public static async Task<RawStream> ConnectAsync(LabHost host, string method, string? subProtocol, string query = "")
    {
        var raw = new RawStream();
        raw.Ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        raw.Ws.Options.CollectHttpResponseDetails = true;
        if (subProtocol is not null)
            raw.Ws.Options.AddSubProtocol(subProtocol);
        await raw.Ws.ConnectAsync(new Uri($"wss://localhost:{host.Port}/ion/IStreamLab/{method}.ws{query}"), CancellationToken.None);
        return raw;
    }

    public static async Task<RawStream> OpenAsync(LabHost host, string method, string user = "alice", string query = "")
        => await ConnectAsync(host, method, IonStreamProtocol.SubProtocol(await TicketAsync(host, user), 2), query);

    public Task SendAsync(byte[] message) => Ws.SendAsync(message, WebSocketMessageType.Binary, true, CancellationToken.None);

    public Task SendArgsAsync(Action<CborWriter> args, int count)
    {
        var w = new CborWriter();
        w.WriteStartArray(count);
        args(w);
        w.WriteEndArray();
        return SendAsync(w.Encode());
    }

    /// <summary>The next frame that is not a PING, or a close.</summary>
    public async Task<(WebSocketMessageType Type, byte[] Frame)> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var total = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                r = await Ws.ReceiveAsync(buffer.AsMemory(total), cts.Token);
                total += r.Count;
            } while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, []);
            if (total == 1 && buffer[0] == IonStreamProtocol.OpPing)
                continue;
            return (r.MessageType, buffer.AsSpan(0, total).ToArray());
        }
    }

    public async Task<byte[]> ExpectAsync(byte opcode)
    {
        var (type, frame) = await NextAsync();
        Assert.That(type, Is.EqualTo(WebSocketMessageType.Binary), "expected a frame, got a close");
        Assert.That(frame[0], Is.EqualTo(opcode), $"expected opcode 0x{opcode:x2}, got 0x{frame[0]:x2}");
        return frame[1..];
    }

    public async Task ExpectCloseAsync(WebSocketCloseStatus? status = null)
    {
        var (type, frame) = await NextAsync();
        Assert.That(type, Is.EqualTo(WebSocketMessageType.Close), $"expected a close, got opcode 0x{(frame.Length > 0 ? frame[0] : 0):x2}");
        if (status is not null)
            Assert.That(Ws.CloseStatus, Is.EqualTo(status));
    }

    public async ValueTask DisposeAsync()
    {
        try { Ws.Abort(); } catch (Exception) { }
        Ws.Dispose();
        await Task.CompletedTask;
    }

    private static string Base56(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";
        var zeros = 0;
        while (zeros < bytes.Length && bytes[zeros] == 0) zeros++;
        var value = BigInteger.Zero;
        foreach (var b in bytes) value = (value << 8) + b;
        var sb = new StringBuilder();
        while (value > 0) { sb.Insert(0, alphabet[(int)(value % 56)]); value /= 56; }
        return new string(alphabet[0], zeros) + sb;
    }
}
