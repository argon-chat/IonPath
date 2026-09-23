namespace IonTestClientServer.Streaming;

using System.Diagnostics;
using ion.runtime;
using ion.runtime.client;
using ion.runtime.network;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using TestContracts;

/// <summary>Which transport a call ends up on, and how it gets there when the first choice fails.</summary>
[Parallelizable(ParallelScope.None)]
public class StreamTransportTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [SetUp]
    public void SetUp() => IonWsClient.ResetTransportHealth();

    private static async Task<string> TransportOfOneCallAsync(LabHost host, string user, Action<IonStreamClientOptions>? configure, params IonStreamTransportKind[] order)
    {
        var items = new List<int>();
        await foreach (var i in host.Lab(user, configure, order).Count(0, 3, 0))
            items.Add(i);
        Assert.That(items, Is.EqualTo(new[] { 0, 1, 2 }));
        return (await host.Probe.ByUserAsync(user)).Transport!;
    }

    [Test]
    public async Task The_default_order_prefers_WebTransport()
    {
        await using var host = await LabHost.StartAsync();
        var handler = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
        {
            BaseAddress = host.BaseAddress
        };
        var client = IonClient.Create(handler)
            .WithInterceptor(new UserHeader("dflt"))
            .WithStreamOptions(o => o.ServerCertificateValidation = (_, _, _, _) => true);

        Assert.That(new IonStreamClientOptions().Transports,
            Is.EqualTo(new[] { IonStreamTransportKind.WebTransport, IonStreamTransportKind.WebSocket }));

        await foreach (var _ in client.ForService<IStreamLab>(host.Services).Count(0, 1, 0))
        {
        }

        Assert.That((await host.Probe.ByUserAsync("dflt")).Transport, Is.EqualTo("wt"));
    }

    [Test]
    public async Task The_order_is_the_callers_to_choose()
    {
        await using var host = await LabHost.StartAsync();

        Assert.That(await TransportOfOneCallAsync(host, "a", null, IonStreamTransportKind.WebSocket, IonStreamTransportKind.WebTransport), Is.EqualTo("ws"));
        Assert.That(await TransportOfOneCallAsync(host, "b", null, IonStreamTransportKind.WebTransport, IonStreamTransportKind.WebSocket), Is.EqualTo("wt"));
        Assert.That(await TransportOfOneCallAsync(host, "c", null, IonStreamTransportKind.WebTransport), Is.EqualTo("wt"));
    }

    [Test]
    public async Task A_server_without_HTTP3_falls_back_to_WebSocket_and_the_failure_is_remembered()
    {
        await using var host = await LabHost.StartAsync(http3: false);

        var sw = Stopwatch.StartNew();
        Assert.That(await TransportOfOneCallAsync(host, "first", o => o.WebTransportConnectTimeout = TimeSpan.FromSeconds(1),
            IonStreamTransportKind.WebTransport, IonStreamTransportKind.WebSocket), Is.EqualTo("ws"));
        var firstCall = sw.Elapsed;

        sw.Restart();
        Assert.That(await TransportOfOneCallAsync(host, "second", o => o.WebTransportConnectTimeout = TimeSpan.FromSeconds(1),
            IonStreamTransportKind.WebTransport, IonStreamTransportKind.WebSocket), Is.EqualTo("ws"));
        var secondCall = sw.Elapsed;

        TestContext.Out.WriteLine($"first call {firstCall.TotalMilliseconds:F0} ms, second {secondCall.TotalMilliseconds:F0} ms");
        Assert.That(host.Probe.All.Select(c => c.Transport), Is.All.EqualTo("ws"));
        Assert.That(secondCall, Is.LessThan(firstCall), "the second call skipped the WebTransport attempt");
    }

    [Test]
    public async Task WebTransport_is_skipped_over_plain_http()
    {
        await using var factory = new IonTestFactoryAsp();
        var http = factory.CreateClient();
        var client = IonClient.Create(http, WsFactory(factory))
            .WithInterceptor<AuthToken>()
            .WithStreamOptions(o => o.Transports = [IonStreamTransportKind.WebTransport, IonStreamTransportKind.WebSocket]);

        var items = new List<int>();
        await foreach (var i in client.ForService<IRandomStreamInteraction>(factory.Services).Integer(0, 1))
            items.Add(i);
        Assert.That(items, Has.Count.EqualTo(10), "the in-memory test server is reached over WebSocket");

        var wtOnly = IonClient.Create(factory.CreateClient(), WsFactory(factory))
            .WithStreamOptions(o => o.Transports = [IonStreamTransportKind.WebTransport]);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in wtOnly.ForService<IRandomStreamInteraction>(factory.Services).Integer(0, 1))
            {
            }
        });
    }

    [Test]
    public async Task A_refused_ticket_is_final_on_any_transport()
    {
        await using var host = await LabHost.StartAsync();

        var ex = Assert.CatchAsync<IonRequestException>(async () =>
        {
            await foreach (var _ in host.Lab("forged", null, IonStreamTransportKind.WebTransport, IonStreamTransportKind.WebSocket).Count(0, 1, 0))
            {
            }
        });

        Assert.That(ex, Is.Not.InstanceOf<IonStreamDisconnectedException>(), ex!.ToString());
        Assert.That(ex.Error.code, Is.EqualTo("TICKET_REFUSED"));
        Assert.That(host.Probe.All, Is.Empty);
    }

    [Test]
    public async Task Both_transports_serve_concurrent_calls_side_by_side()
    {
        await using var host = await LabHost.StartAsync();

        var calls = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            var kind = i % 2 == 0 ? IonStreamTransportKind.WebTransport : IonStreamTransportKind.WebSocket;
            var sum = 0L;
            await foreach (var v in host.Lab($"u{i}", null, kind).Count(0, 500, 0))
                sum += v;
            return sum;
        })).ToArray();

        var sums = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.That(sums, Is.All.EqualTo(Enumerable.Range(0, 500).Sum()));
        await Eventually.TrueAsync(() => host.Connections.Count == 0, Wait, "all closed");
        Assert.That(host.Probe.All.Count(c => c.Transport == "wt"), Is.EqualTo(20));
        Assert.That(host.Probe.All.Count(c => c.Transport == "ws"), Is.EqualTo(20));
    }

    private static IonWebSocketFactory WsFactory(IonTestFactoryAsp factory) => (uri, ct, protocols) =>
    {
        var socket = factory.Server.CreateWebSocketClient();
        foreach (var protocol in protocols ?? []) socket.SubProtocols.Add(protocol);
        return socket.ConnectAsync(uri, ct);
    };

    private sealed class UserHeader(string user) : IIonInterceptor
    {
        public Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
        {
            context.RequestItems[LabTickets.UserHeader] = user;
            return next(context, ct);
        }
    }

    private sealed class AuthToken : IIonInterceptor
    {
        public Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
        {
            context.RequestItems["authToken"] = "123";
            return next(context, ct);
        }
    }
}
