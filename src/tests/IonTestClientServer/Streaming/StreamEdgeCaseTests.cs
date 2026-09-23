namespace IonTestClientServer.Streaming;

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using ion.runtime;
using ion.runtime.client;
using ion.runtime.network;
using TestContracts;

/// <summary>
/// The corners: what connect hooks may do, a client leaving while they run, a disconnect hook that
/// throws, streams that end before their input does, HTTP/2 WebSockets, and whether a finished
/// connection can be collected at all.
/// </summary>
[TestFixture(IonStreamTransportKind.WebSocket)]
[TestFixture(IonStreamTransportKind.WebTransport)]
[Parallelizable(ParallelScope.None)]
public class StreamEdgeCaseTests(IonStreamTransportKind transport)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private LabHost host = null!;

    [SetUp]
    public async Task SetUp()
    {
        IonWsClient.ResetTransportHealth();
        host = await LabHost.StartAsync();
    }

    [TearDown]
    public async Task TearDown() => await host.DisposeAsync();

    private IStreamLab Lab(string user = "alice") => host.Lab(user, null, transport);

    private IonStreamTransportKind Other => transport == IonStreamTransportKind.WebSocket
        ? IonStreamTransportKind.WebTransport
        : IonStreamTransportKind.WebSocket;

    [Test]
    public async Task A_push_from_the_connect_hook_is_the_first_item_the_client_sees()
    {
        host.Probe.PushOnConnect = true;

        LabEvent? first = null;
        await foreach (var e in Lab().Listen("t"))
        {
            first = e;
            break;
        }

        Assert.That(first, Is.EqualTo(new LabEvent(0, "welcome", "sent from OnConnected")),
            "queued before READY, delivered after it, before anything else");
    }

    [Test]
    public async Task Closing_from_the_connect_hook_is_a_refusal_the_client_can_read_and_is_not_retried()
    {
        var ex = Assert.ThrowsAsync<IonStreamClosedException>(async () =>
        {
            await foreach (var _ in host.Lab("close-on-connect", null, transport, Other).Count(0, 5, 0))
            {
            }
        });

        Assert.That(ex!.Reason, Is.EqualTo("not now"));
        Assert.That(ex.AllowReconnect, Is.True);

        var c = await host.Probe.SingleAsync();
        var info = await c.Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ServerClosed));
        Assert.That(c.StreamStarted.Task.IsCompleted, Is.False, "the stream method never ran");
        await Task.Delay(200);
        Assert.That(host.Probe.All, Has.Count.EqualTo(1), "not retried on the other transport");
    }

    [Test]
    public async Task A_client_that_leaves_during_the_connect_hook_is_noticed_by_the_hook()
    {
        host.Probe.ConnectDelay = TimeSpan.FromSeconds(1.5);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        var sw = Stopwatch.StartNew();
        Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Lab().Count(0, 5, 0, cts.Token))
            {
            }
        });
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1.4)), "the caller did not wait for the hook");

        var c = await host.Probe.SingleAsync();
        var info = await c.Disconnected.Task.WaitAsync(Wait);
        Assert.Multiple(() =>
        {
            Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ClientClosed));
            Assert.That(c.AbortedDuringConnect, Is.True, "ConnectionAborted fired while the hook was still running");
            Assert.That(c.StreamStarted.Task.IsCompleted, Is.False, "the stream method never ran");
            Assert.That(c.DisconnectCalls, Is.EqualTo(1), "a hook that connected is disconnected, even if the client is long gone");
        });
    }

    [Test]
    public async Task A_disconnect_hook_that_throws_does_not_stop_the_others()
    {
        host.Probe.ThrowOnDisconnect.Add("thrower");

        await foreach (var _ in Lab("thrower").Count(0, 3, 0))
        {
        }

        var c = await host.Probe.SingleAsync();
        await Eventually.TrueAsync(() => c.GlobalDisconnectCalls == 1, Wait, "the global hook still ran");
        Assert.That(c.DisconnectCalls, Is.EqualTo(1));
        Assert.That(host.Log.Lines(Microsoft.Extensions.Logging.LogLevel.Error)
            .Any(l => l.Contains("OnDisconnectedAsync of StreamLabImpl failed")), Is.True, "and the failure was logged");
        await Eventually.TrueAsync(() => host.Connections.Count == 0, Wait, "the connection is gone all the same");
    }

    [Test]
    public async Task A_stream_with_no_items_completes_empty()
    {
        var items = new List<int>();
        await foreach (var i in Lab().Count(0, 0, 0))
            items.Add(i);

        Assert.That(items, Is.Empty);
        var c = await host.Probe.SingleAsync();
        Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.Completed));
    }

    [Test]
    public async Task A_duplex_stream_that_ends_before_its_input_ends_the_call_and_stops_the_input()
    {
        host.Probe.EchoLimit = 3;
        var inputStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var output = new List<string>();
        await foreach (var s in Lab().Echo(Endless(inputStopped)))
            output.Add(s);

        Assert.That(output, Is.EqualTo(new[] { "I0", "I1", "I2" }));
        await inputStopped.Task.WaitAsync(Wait);
        var c = await host.Probe.SingleAsync();
        Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.Completed));

        static async IAsyncEnumerable<string> Endless(TaskCompletionSource stopped, [EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                for (var i = 0; ; i++)
                {
                    yield return $"i{i}";
                    await Task.Delay(10, ct);
                }
            }
            finally
            {
                stopped.TrySetResult();
            }
        }
    }

    [Test]
    public async Task WebSockets_over_HTTP2_work_like_any_other()
    {
        if (transport != IonStreamTransportKind.WebSocket)
            Assert.Ignore("A WebSocket-only property.");

        IonWebSocketFactory http2 = async (uri, ct, protocols) =>
        {
            var ws = new ClientWebSocket();
            ws.Options.HttpVersion = HttpVersion.Version20;
            ws.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            foreach (var p in protocols ?? [])
                ws.Options.AddSubProtocol(p);
            var invoker = new HttpMessageInvoker(new SocketsHttpHandler
            {
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
            });
            await ws.ConnectAsync(uri, invoker, ct);
            return ws;
        };

        var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
        {
            BaseAddress = host.BaseAddress
        };
        var client = IonClient.Create(http, http2)
            .WithInterceptor(new UserHeader("h2"))
            .WithStreamOptions(o => o.Transports = [IonStreamTransportKind.WebSocket]);

        var items = new List<int>();
        await foreach (var i in client.ForService<IStreamLab>(host.Services).Count(0, 100, 0))
            items.Add(i);

        Assert.That(items, Is.EqualTo(Enumerable.Range(0, 100)));
        Assert.That((await host.Probe.ByUserAsync("h2")).HttpProtocol, Is.EqualTo("HTTP/2"));
    }

    [Test]
    public async Task A_finished_connection_leaves_nothing_behind_to_collect()
    {
        for (var i = 0; i < 25; i++)
            await foreach (var _ in Lab($"gc{i}").Count(0, 3, 0))
            {
            }

        await Eventually.TrueAsync(() => host.Connections.Count == 0, Wait, "all closed");
        await Eventually.TrueAsync(() => host.Probe.All.All(c => c.GlobalDisconnectCalls == 1), Wait, "all hooks ran");

        var weak = TakeWeakReferences(host.Probe);

        for (var round = 0; round < 10 && weak.Any(w => w.IsAlive); round++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            await Task.Delay(100);
        }

        Assert.That(weak.Count(w => w.IsAlive), Is.Zero, $"{weak.Count(w => w.IsAlive)} of {weak.Count} connections are still reachable");

        // A separate method, so no local of this frame keeps a context alive for the JIT's sake.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static List<WeakReference> TakeWeakReferences(LabProbe probe)
        {
            var refs = new List<WeakReference>();
            foreach (var c in probe.All)
            {
                refs.Add(new WeakReference(c.Context));
                c.Context = null;
            }

            return refs;
        }
    }

    [Test]
    public void Base56_tickets_keep_their_leading_zero_bytes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IonTicketExtractor.DecodeTicket("27")!.Value.ToArray(), Is.EqualTo(new byte[] { 0, 5 }));
            Assert.That(IonTicketExtractor.DecodeTicket("222")!.Value.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0 }));
            Assert.That(IonTicketExtractor.DecodeTicket("0OIl"), Is.Null, "characters outside the alphabet");
            Assert.That(IonTicketExtractor.DecodeTicket(""), Is.Null);
        });

        var random = new Random(56);
        for (var n = 0; n < 500; n++)
        {
            var bytes = new byte[random.Next(0, 40)];
            random.NextBytes(bytes);
            for (var z = random.Next(0, 4); z > 0 && z <= bytes.Length; z--)
                bytes[z - 1] = 0;

            var decoded = IonTicketExtractor.DecodeTicket(Encode(bytes));
            Assert.That(decoded?.ToArray() ?? [], Is.EqualTo(bytes), Convert.ToHexString(bytes));
        }

        // The reference encoding every client implements: a '2' per leading zero byte, then the number.
        static string Encode(byte[] bytes)
        {
            const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";
            var zeros = bytes.TakeWhile(b => b == 0).Count();
            var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            var sb = new StringBuilder();
            while (value > 0)
            {
                sb.Insert(0, alphabet[(int)(value % 56)]);
                value /= 56;
            }

            return new string(alphabet[0], zeros) + sb;
        }
    }

    private sealed class UserHeader(string user) : IIonInterceptor
    {
        public Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
        {
            context.RequestItems[LabTickets.UserHeader] = user;
            return next(context, ct);
        }
    }
}
