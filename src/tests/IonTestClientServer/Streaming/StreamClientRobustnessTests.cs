namespace IonTestClientServer.Streaming;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Cbor;
using System.Net;
using System.Net.WebSockets;
using ion.runtime;
using ion.runtime.client;
using ion.runtime.network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestContracts;

/// <summary>
/// The C# client against a server that breaks the protocol on purpose: a scripted WebSocket
/// endpoint that sends exactly the frames a test tells it to. Every one of these must end the call
/// promptly, with an exception that says what happened — never a hang, never a silent success.
/// </summary>
[Parallelizable(ParallelScope.None)]
public class StreamClientRobustnessTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private EvilServer server = null!;

    [SetUp]
    public async Task SetUp() => server = await EvilServer.StartAsync();

    [TearDown]
    public async Task TearDown() => await server.DisposeAsync();

    private IStreamLab Lab(Action<IonStreamClientOptions>? configure = null)
    {
        var client = IonClient.Create(new HttpClient { BaseAddress = server.BaseAddress })
            .WithStreamOptions(o =>
            {
                o.Transports = [IonStreamTransportKind.WebSocket];
                o.HandshakeTimeout = TimeSpan.FromSeconds(3);
                o.CloseTimeout = TimeSpan.FromSeconds(1);
                // How one connection's failure surfaces; the reconnecting tests below turn it back on.
                o.Reconnect = null;
                configure?.Invoke(o);
            });
        return client.ForService<IStreamLab>(new ServiceCollection().BuildServiceProvider());
    }

    private static async Task<List<int>> CollectAsync(IAsyncEnumerable<int> stream)
    {
        var items = new List<int>();
        await foreach (var i in stream)
            items.Add(i);
        return items;
    }

    [Test]
    public async Task An_unknown_opcode_is_a_protocol_violation()
    {
        server.Script = async ws =>
        {
            await EvilServer.SendAsync(ws, EvilServer.Ready());
            await EvilServer.SendAsync(ws, [0x42]);
        };

        var sw = Stopwatch.StartNew();
        var ex = Assert.ThrowsAsync<IonStreamDisconnectedException>(async () => await CollectAsync(Lab().Count(0, 1, 0)));
        Assert.That(ex!.Reason, Is.EqualTo(IonDisconnectReason.ProtocolViolation));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
    }

    [Test]
    public async Task A_close_without_END_is_a_lost_transport_that_carries_the_close_code()
    {
        server.Script = async ws =>
        {
            await EvilServer.SendAsync(ws, EvilServer.Ready());
            await EvilServer.SendAsync(ws, EvilServer.Data(1));
            await ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "rebooting", CancellationToken.None);
        };

        var items = new List<int>();
        var ex = Assert.ThrowsAsync<IonStreamDisconnectedException>(async () =>
        {
            await foreach (var i in Lab().Count(0, 1, 0))
                items.Add(i);
        });

        Assert.Multiple(() =>
        {
            Assert.That(items, Is.EqualTo(new[] { 1 }), "what arrived before the close is still delivered");
            Assert.That(ex!.Reason, Is.EqualTo(IonDisconnectReason.TransportLost));
            Assert.That(ex.CloseStatus, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
            Assert.That(ex.CloseDescription, Is.EqualTo("rebooting"));
        });
    }

    [Test]
    public async Task An_item_that_cannot_be_decoded_fails_the_call_and_the_client_still_leaves_gracefully()
    {
        server.Script = async ws =>
        {
            await EvilServer.SendAsync(ws, EvilServer.Ready());
            var text = new CborWriter();
            text.WriteTextString("not an i4");
            await EvilServer.SendAsync(ws, IonStreamProtocol.Frame(IonStreamProtocol.OpData, text));
        };

        Assert.CatchAsync<IonDecodeException>(async () => await CollectAsync(Lab().Count(0, 1, 0)));

        var close = await server.ClientClosed.Task.WaitAsync(Wait);
        Assert.That(close, Is.EqualTo(WebSocketCloseStatus.NormalClosure), "a WebSocket close, not an abort");
        Assert.That(server.ClientOpcodes, Does.Contain(IonStreamProtocol.OpClose), "and the goodbye before it");
    }

    [Test]
    public async Task A_CLOSE_before_READY_is_a_closed_stream_with_its_reason()
    {
        server.Script = ws => EvilServer.SendAsync(ws, IonStreamProtocol.ServerCloseFrame("maintenance", allowReconnect: true));

        var ex = Assert.ThrowsAsync<IonStreamClosedException>(async () => await CollectAsync(Lab().Count(0, 1, 0)));
        Assert.That(ex!.Reason, Is.EqualTo("maintenance"));
        Assert.That(ex.AllowReconnect, Is.True);
        Assert.That(server.Connections, Is.EqualTo(1), "a server's refusal is not retried");
    }

    [Test]
    public async Task When_reconnecting_a_CLOSE_that_invites_the_client_back_is_retried_with_backoff()
    {
        server.Script = ws => EvilServer.SendAsync(ws, IonStreamProtocol.ServerCloseFrame("maintenance", allowReconnect: true));
        var attempts = new ConcurrentQueue<IonStreamReconnecting>();

        using var cts = new CancellationTokenSource();
        var call = Task.Run(() => CollectAsync(Lab(o =>
        {
            o.Reconnect = new IonStreamReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(40), MaxDelay = TimeSpan.FromMilliseconds(80) };
            o.OnReconnecting = attempts.Enqueue;
        }).Count(0, 1, 0, cts.Token)));

        await Eventually.TrueAsync(() => server.Connections >= 3, Wait, "the invitation is taken up, again and again");
        Assert.That(attempts.All(a => a.Cause is IonStreamClosedException { Reason: "maintenance" }), Is.True);
        Assert.That(call.IsCompleted, Is.False, "the consumer waits, it is not failed");

        await cts.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await call.WaitAsync(Wait));
    }

    [Test]
    public async Task When_reconnecting_a_protocol_violation_is_still_final()
    {
        server.Script = async ws =>
        {
            await EvilServer.SendAsync(ws, EvilServer.Ready());
            await EvilServer.SendAsync(ws, [0x7f]);
        };

        var ex = Assert.ThrowsAsync<IonStreamDisconnectedException>(async () => await CollectAsync(Lab(o =>
            o.Reconnect = new IonStreamReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(20) }).Count(0, 1, 0)));
        Assert.That(ex!.Reason, Is.EqualTo(IonDisconnectReason.ProtocolViolation));
        await Task.Delay(200);
        Assert.That(server.Connections, Is.EqualTo(1), "a server that breaks the protocol is not asked again");
    }

    [Test]
    public async Task An_empty_CLOSE_is_a_close_with_no_reason()
    {
        server.Script = async ws =>
        {
            await EvilServer.SendAsync(ws, EvilServer.Ready());
            await EvilServer.SendAsync(ws, [IonStreamProtocol.OpClose]);
        };

        var ex = Assert.ThrowsAsync<IonStreamClosedException>(async () => await CollectAsync(Lab().Count(0, 1, 0)));
        Assert.That(ex!.Reason, Is.Null);
        Assert.That(ex.AllowReconnect, Is.False);
    }

    [Test]
    public async Task An_ERROR_before_READY_is_the_servers_error()
    {
        server.Script = ws => EvilServer.SendAsync(ws, IonStreamProtocol.ErrorFrame(new IonProtocolError("QUOTA", "too many streams")));

        var ex = Assert.ThrowsAsync<IonRequestException>(async () => await CollectAsync(Lab().Count(0, 1, 0)));
        Assert.That(ex!.Error, Is.EqualTo(new IonProtocolError("QUOTA", "too many streams")));
        Assert.That(ex, Is.Not.InstanceOf<IonStreamDisconnectedException>());
    }

    [Test]
    public async Task A_server_that_never_says_READY_times_the_handshake_out()
    {
        server.Script = _ => Task.Delay(TimeSpan.FromSeconds(30));

        var sw = Stopwatch.StartNew();
        var ex = Assert.ThrowsAsync<IonStreamDisconnectedException>(async () =>
            await CollectAsync(Lab(o => o.HandshakeTimeout = TimeSpan.FromMilliseconds(700)).Count(0, 1, 0)));

        Assert.That(ex!.Reason, Is.EqualTo(IonDisconnectReason.Timeout));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
    }

    [Test]
    public async Task DATA_before_READY_is_a_protocol_violation_at_once()
    {
        server.Script = ws => EvilServer.SendAsync(ws, EvilServer.Data(1));

        var sw = Stopwatch.StartNew();
        var ex = Assert.ThrowsAsync<IonStreamDisconnectedException>(async () => await CollectAsync(Lab().Count(0, 1, 0)));
        Assert.That(ex!.Reason, Is.EqualTo(IonDisconnectReason.ProtocolViolation));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "not after the 3 s handshake timeout");
    }

    [Test]
    public async Task A_frame_over_the_clients_limit_is_a_protocol_violation()
    {
        server.Script = async ws =>
        {
            await EvilServer.SendAsync(ws, EvilServer.Ready());
            var big = new CborWriter();
            big.WriteByteString(new byte[4096]);
            await EvilServer.SendAsync(ws, IonStreamProtocol.Frame(IonStreamProtocol.OpData, big));
        };

        var ex = Assert.ThrowsAsync<IonStreamDisconnectedException>(async () =>
            await CollectAsync(Lab(o => o.MaxReceiveMessageSize = 1024).Count(0, 1, 0)));
        Assert.That(ex!.Reason, Is.EqualTo(IonDisconnectReason.ProtocolViolation));
    }

    [Test]
    public async Task A_READY_from_a_newer_server_with_more_fields_is_understood()
    {
        server.Script = async ws =>
        {
            var ready = new CborWriter();
            ready.WriteStartArray(5);
            ready.WriteTextString("conn-1");
            ready.WriteUInt64(15_000);
            ready.WriteUInt64(30_000);
            ready.WriteTextString("a field from the future");
            ready.WriteStartMap(0);
            ready.WriteEndMap();
            ready.WriteEndArray();
            await EvilServer.SendAsync(ws, IonStreamProtocol.Frame(IonStreamProtocol.OpReady, ready));
            await EvilServer.SendAsync(ws, EvilServer.Data(7));
            await EvilServer.SendAsync(ws, [IonStreamProtocol.OpEnd]);
        };

        Assert.That(await CollectAsync(Lab().Count(0, 1, 0)), Is.EqualTo(new[] { 7 }));
    }

    [Test]
    public async Task Anything_after_END_is_ignored()
    {
        server.Script = async ws =>
        {
            await EvilServer.SendAsync(ws, EvilServer.Ready());
            await EvilServer.SendAsync(ws, EvilServer.Data(1));
            await EvilServer.SendAsync(ws, [IonStreamProtocol.OpEnd]);
            await EvilServer.SendAsync(ws, EvilServer.Data(2));
            await EvilServer.SendAsync(ws, [0x42]);
        };

        Assert.That(await CollectAsync(Lab().Count(0, 1, 0)), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public async Task The_client_adapts_its_heartbeat_to_what_the_server_announces()
    {
        // The server will drop a client silent for 400 ms; the client's own setting says 15 s.
        server.Script = async ws =>
        {
            await EvilServer.SendAsync(ws, EvilServer.Ready(keepAliveMs: 100, clientTimeoutMs: 400));
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            await EvilServer.SendAsync(ws, [IonStreamProtocol.OpEnd]);
        };

        Assert.That(await CollectAsync(Lab(o => o.KeepAliveInterval = TimeSpan.FromSeconds(15)).Count(0, 1, 0)), Is.Empty);

        var pings = server.ClientOpcodes.Count(o => o == IonStreamProtocol.OpPing);
        Assert.That(pings, Is.GreaterThanOrEqualTo(5), "about every 200 ms for 1.5 s — half the announced timeout");
    }

    /// <summary>A WebSocket endpoint that plays a script instead of the protocol.</summary>
    private sealed class EvilServer : IAsyncDisposable
    {
        private readonly WebApplication app;
        private int connections;

        private EvilServer(WebApplication app, int port)
        {
            this.app = app;
            BaseAddress = new Uri($"http://127.0.0.1:{port}");
        }

        public Uri BaseAddress { get; }

        public Func<WebSocket, Task> Script { get; set; } = _ => Task.CompletedTask;

        public int Connections => Volatile.Read(ref connections);

        public ConcurrentQueue<byte> ClientOpcodes { get; } = new();

        public TaskCompletionSource<WebSocketCloseStatus?> ClientClosed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static byte[] Ready(ulong keepAliveMs = 15_000, ulong clientTimeoutMs = 30_000)
        {
            var w = new CborWriter();
            w.WriteStartArray(3);
            w.WriteTextString("evil-connection");
            w.WriteUInt64(keepAliveMs);
            w.WriteUInt64(clientTimeoutMs);
            w.WriteEndArray();
            return IonStreamProtocol.Frame(IonStreamProtocol.OpReady, w);
        }

        public static byte[] Data(int value)
        {
            var w = new CborWriter();
            w.WriteInt32(value);
            return IonStreamProtocol.Frame(IonStreamProtocol.OpData, w);
        }

        public static Task SendAsync(WebSocket ws, byte[] frame)
            => ws.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);

        public static async Task<EvilServer> StartAsync()
        {
            var port = Random.Shared.Next(20_000, 40_000);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(2));
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
            var app = builder.Build();
            var self = new EvilServer(app, port);

            app.UseWebSockets();
            app.MapPost("/ion.att", async (HttpContext http) =>
            {
                var w = new CborWriter();
                w.WriteStartArray(1);
                w.WriteByteString([0, 0, 1]);
                w.WriteEndArray();
                http.Response.ContentType = "application/ion";
                await http.Response.Body.WriteAsync(w.Encode());
            });
            app.Map("/ion/IStreamLab/{method}.ws", async (HttpContext http) =>
            {
                Interlocked.Increment(ref self.connections);
                using var ws = await http.WebSockets.AcceptWebSocketAsync(http.WebSockets.WebSocketRequestedProtocols.FirstOrDefault());
                var buffer = new byte[1 << 16];
                await ws.ReceiveAsync(buffer, CancellationToken.None); // the arguments

                var script = self.Script(ws);
                try
                {
                    while (true)
                    {
                        var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close)
                        {
                            self.ClientClosed.TrySetResult(ws.CloseStatus);
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None);
                            break;
                        }

                        if (r.Count > 0)
                            self.ClientOpcodes.Enqueue(buffer[0]);
                    }
                }
                catch (Exception)
                {
                    self.ClientClosed.TrySetResult(null); // aborted
                }

                try { await script.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            });

            await app.StartAsync();
            return self;
        }

        public async ValueTask DisposeAsync()
        {
            try { await app.StopAsync(); } catch (Exception) { }
            await app.DisposeAsync();
        }
    }
}
