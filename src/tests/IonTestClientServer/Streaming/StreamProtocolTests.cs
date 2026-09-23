namespace IonTestClientServer.Streaming;

using System.Formats.Cbor;
using System.Net;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using ion.runtime;
using ion.runtime.network;

/// <summary>
/// The wire, byte by byte, from a hand-driven WebSocket: what the server does with clients that
/// misbehave in every way the protocol can be misused.
/// </summary>
[Parallelizable(ParallelScope.None)]
public class StreamProtocolTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private LabHost host = null!;

    [SetUp]
    public async Task SetUp() => host = await LabHost.StartAsync(o => o.MaxReceiveMessageSize = 64 * 1024);

    [TearDown]
    public async Task TearDown() => await host.DisposeAsync();

    private static async Task<int> UpgradeStatusAsync(LabHost host, string? subProtocol)
    {
        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        ws.Options.CollectHttpResponseDetails = true;
        if (subProtocol is not null)
            ws.Options.AddSubProtocol(subProtocol);
        try
        {
            await ws.ConnectAsync(new Uri($"wss://localhost:{host.Port}/ion/IStreamLab/Count.ws"), CancellationToken.None);
            return (int)ws.HttpStatusCode;
        }
        catch (WebSocketException)
        {
            return (int)ws.HttpStatusCode;
        }
    }

    [Test]
    public async Task Protocol_v1_is_refused_at_the_upgrade()
    {
        var ticket = await RawStream.TicketAsync(host, "alice");
        Assert.That(await UpgradeStatusAsync(host, $"ion!ticket#{ticket}!ver#1"), Is.EqualTo(426));
        Assert.That(await UpgradeStatusAsync(host, $"ion!ticket#{ticket}"), Is.EqualTo(426), "no version is v1");
        Assert.That(await UpgradeStatusAsync(host, null), Is.EqualTo(426), "no sub-protocol at all");
    }

    [Test]
    public async Task A_missing_or_refused_ticket_is_refused_at_the_upgrade()
    {
        Assert.That(await UpgradeStatusAsync(host, "ion!ver#2"), Is.EqualTo(412), "a ticket exchange is configured, so a ticket is required");

        var forged = await RawStream.TicketAsync(host, "forged");
        Assert.That(await UpgradeStatusAsync(host, $"ion!ticket#{forged}!ver#2"), Is.EqualTo(401));
        Assert.That(host.Connections.Count, Is.Zero);
    }

    [Test]
    public async Task The_happy_path_on_the_wire()
    {
        await using var raw = await RawStream.OpenAsync(host, "Count");
        Assert.That(raw.Ws.SubProtocol, Does.EndWith("!ver#2"));

        await raw.SendArgsAsync(w => { w.WriteInt32(5); w.WriteInt32(3); w.WriteInt32(0); }, 3);

        var ready = IonStreamProtocol.ReadReady(await raw.ExpectAsync(IonStreamProtocol.OpReady));
        Assert.That(ready.ConnectionId, Is.Not.Empty);
        Assert.That(ready.KeepAliveInterval, Is.EqualTo(TimeSpan.FromMilliseconds(150)));
        Assert.That(ready.ClientTimeout, Is.EqualTo(TimeSpan.FromMilliseconds(1500)));

        for (var i = 5; i < 8; i++)
            Assert.That(new CborReader(await raw.ExpectAsync(IonStreamProtocol.OpData)).ReadInt32(), Is.EqualTo(i));

        Assert.That(await raw.ExpectAsync(IonStreamProtocol.OpEnd), Is.Empty);
        await raw.ExpectCloseAsync(WebSocketCloseStatus.NormalClosure);
        await raw.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None);

        var c = await host.Probe.SingleAsync();
        Assert.That((await c.Disconnected.Task.WaitAsync(Wait)).Reason, Is.EqualTo(IonDisconnectReason.Completed));
    }

    [Test]
    public async Task A_client_goodbye_carries_its_reason_to_the_disconnect_hook()
    {
        await using var raw = await RawStream.OpenAsync(host, "Listen");
        await raw.SendArgsAsync(w => w.WriteTextString("t"), 1);
        await raw.ExpectAsync(IonStreamProtocol.OpReady);

        await raw.SendAsync(IonStreamProtocol.ClientCloseFrame("moving to another tab"));
        await raw.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

        var info = await (await host.Probe.SingleAsync()).Disconnected.Task.WaitAsync(Wait);
        Assert.Multiple(() =>
        {
            Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ClientClosed));
            Assert.That(info.Message, Is.EqualTo("moving to another tab"));
            Assert.That(info.Exception, Is.Null);
        });
    }

    [Test]
    public async Task A_plain_websocket_close_without_a_goodbye_is_still_a_graceful_client_close()
    {
        await using var raw = await RawStream.OpenAsync(host, "Listen");
        await raw.SendArgsAsync(w => w.WriteTextString("t"), 1);
        await raw.ExpectAsync(IonStreamProtocol.OpReady);

        await raw.Ws.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "tab closed", CancellationToken.None);

        var info = await (await host.Probe.SingleAsync()).Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ClientClosed));
        Assert.That(info.ClientCloseStatus, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
        Assert.That(info.Message, Is.EqualTo("tab closed"));
    }

    [Test]
    public async Task A_killed_client_is_a_lost_transport_not_a_close()
    {
        var raw = await RawStream.OpenAsync(host, "Listen");
        await raw.SendArgsAsync(w => w.WriteTextString("t"), 1);
        await raw.ExpectAsync(IonStreamProtocol.OpReady);

        raw.Ws.Abort(); // a TCP reset: no close frame, no goodbye

        var info = await (await host.Probe.SingleAsync()).Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.TransportLost));
        Assert.That(info.Exception, Is.Not.Null);
        Assert.That(info.IsGraceful, Is.False);
        await raw.DisposeAsync();
    }

    [Test]
    public async Task A_client_that_stops_pinging_is_timed_out()
    {
        await using var raw = await RawStream.OpenAsync(host, "Listen");
        await raw.SendArgsAsync(w => w.WriteTextString("t"), 1);
        await raw.ExpectAsync(IonStreamProtocol.OpReady);

        // Keep reading (so it is not a stalled send) but never send anything.
        var reading = Task.Run(async () =>
        {
            try { while (true) await raw.NextAsync(TimeSpan.FromSeconds(20)); }
            catch (Exception) { }
        });

        var started = DateTime.UtcNow;
        var info = await (await host.Probe.SingleAsync()).Disconnected.Task.WaitAsync(Wait);

        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.Timeout));
        Assert.That(info.Exception, Is.TypeOf<TimeoutException>());
        Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(4)), "ClientTimeout is 1.5 s");
    }

    [Test]
    public async Task A_client_that_pings_is_never_timed_out()
    {
        await using var raw = await RawStream.OpenAsync(host, "Listen");
        await raw.SendArgsAsync(w => w.WriteTextString("t"), 1);
        await raw.ExpectAsync(IonStreamProtocol.OpReady);

        for (var i = 0; i < 30; i++)
        {
            await raw.SendAsync([IonStreamProtocol.OpPing]);
            await Task.Delay(150);
        }

        var c = await host.Probe.SingleAsync();
        Assert.That(c.Disconnected.Task.IsCompleted, Is.False, "4.5 s of pings, 3× the client timeout");
    }

    [Test]
    public async Task An_argument_message_that_never_comes_times_the_handshake_out()
    {
        await using var slow = await LabHost.StartAsync(o => o.HandshakeTimeout = TimeSpan.FromMilliseconds(400));
        await using var raw = await RawStream.OpenAsync(slow, "Count");

        var (type, _) = await raw.NextAsync(TimeSpan.FromSeconds(5)).ContinueWith(t =>
            t.IsFaulted ? (WebSocketMessageType.Close, Array.Empty<byte>()) : t.Result);

        Assert.That(type, Is.EqualTo(WebSocketMessageType.Close), "the server gave up on the handshake");
        Assert.That(slow.Probe.All, Is.Empty, "no connect hook ran for a connection that never started");
    }

    [Test]
    [TestCase(new byte[] { 0x42 }, TestName = "Unknown opcode")]
    [TestCase(new byte[] { IonStreamProtocol.OpData, 0x01 }, TestName = "DATA to a stream that takes no input")]
    [TestCase(new byte[] { IonStreamProtocol.OpReady }, TestName = "READY from a client")]
    public async Task A_protocol_violation_gets_an_error_frame_then_a_protocol_error_close(byte[] violation)
    {
        await using var raw = await RawStream.OpenAsync(host, "Listen");
        await raw.SendArgsAsync(w => w.WriteTextString("t"), 1);
        await raw.ExpectAsync(IonStreamProtocol.OpReady);

        await raw.SendAsync(violation);

        var error = IonStreamProtocol.ReadError(await raw.ExpectAsync(IonStreamProtocol.OpError));
        Assert.That(error.code, Is.EqualTo("PROTOCOL_VIOLATION"));
        await raw.ExpectCloseAsync(WebSocketCloseStatus.ProtocolError);

        var info = await (await host.Probe.SingleAsync()).Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ProtocolViolation));
        Assert.That(info.Exception, Is.TypeOf<IonStreamProtocolException>());
    }

    [Test]
    public async Task Arguments_that_cannot_be_decoded_are_the_clients_protocol_violation()
    {
        await using var raw = await RawStream.OpenAsync(host, "Count");
        var notAnArray = new CborWriter();
        notAnArray.WriteTextString("not an argument array");
        await raw.SendAsync(notAnArray.Encode());

        await raw.ExpectAsync(IonStreamProtocol.OpReady);
        var error = IonStreamProtocol.ReadError(await raw.ExpectAsync(IonStreamProtocol.OpError));
        Assert.That(error.code, Is.EqualTo("PROTOCOL_VIOLATION"), "a malformed call is the client's fault, not an internal error");
        Assert.That(error.msg, Does.Contain("could not be decoded"));
        await raw.ExpectCloseAsync(WebSocketCloseStatus.ProtocolError);

        var info = await (await host.Probe.SingleAsync()).Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ProtocolViolation));
    }

    [Test]
    public async Task An_empty_data_frame_is_no_longer_an_end_of_input()
    {
        await using var raw = await RawStream.OpenAsync(host, "Echo");
        await raw.SendArgsAsync(_ => { }, 0);
        await raw.ExpectAsync(IonStreamProtocol.OpReady);

        await raw.SendAsync([IonStreamProtocol.OpData]);

        Assert.That(IonStreamProtocol.ReadError(await raw.ExpectAsync(IonStreamProtocol.OpError)).code, Is.EqualTo("PROTOCOL_VIOLATION"));
    }

    [Test]
    public async Task An_oversized_frame_closes_with_message_too_big()
    {
        await using var raw = await RawStream.OpenAsync(host, "Listen");
        await raw.SendArgsAsync(w => w.WriteTextString("t"), 1);
        await raw.ExpectAsync(IonStreamProtocol.OpReady);

        var huge = new byte[64 * 1024 + 1];
        huge[0] = IonStreamProtocol.OpPing;
        await raw.SendAsync(huge);

        await raw.ExpectAsync(IonStreamProtocol.OpError);
        await raw.ExpectCloseAsync(WebSocketCloseStatus.MessageTooBig);
        var info = await (await host.Probe.SingleAsync()).Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.ProtocolViolation));
    }

    [Test]
    public async Task Fragmented_arguments_and_input_are_reassembled()
    {
        await using var raw = await RawStream.OpenAsync(host, "Echo");

        // An empty argument array; the fragmentation is in the input item below.
        await raw.Ws.SendAsync(new byte[] { 0x80 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        await raw.ExpectAsync(IonStreamProtocol.OpReady);

        var w = new CborWriter();
        w.WriteStartArray(1);
        w.WriteTextString(new string('q', 40_000));
        w.WriteEndArray();
        var item = new byte[] { IonStreamProtocol.OpData }.Concat(w.Encode()).ToArray();

        for (var offset = 0; offset < item.Length; offset += 1000)
        {
            var size = Math.Min(1000, item.Length - offset);
            await raw.Ws.SendAsync(item.AsMemory(offset, size), WebSocketMessageType.Binary, offset + size == item.Length, CancellationToken.None);
        }

        await raw.SendAsync([IonStreamProtocol.OpEnd]);

        var echoed = new CborReader(await raw.ExpectAsync(IonStreamProtocol.OpData)).ReadTextString();
        Assert.That(echoed, Is.EqualTo(new string('Q', 40_000)));
        await raw.ExpectAsync(IonStreamProtocol.OpEnd);
    }

    [Test]
    public async Task An_input_error_frame_faults_the_stream_methods_input()
    {
        await using var raw = await RawStream.OpenAsync(host, "Echo");
        await raw.SendArgsAsync(_ => { }, 0);
        await raw.ExpectAsync(IonStreamProtocol.OpReady);

        await raw.SendAsync(IonStreamProtocol.ErrorFrame(new IonProtocolError("INPUT_FAULTED", "upstream broke")));

        var error = IonStreamProtocol.ReadError(await raw.ExpectAsync(IonStreamProtocol.OpError));
        Assert.That(error.code, Is.EqualTo("INPUT_FAULTED"), "the input's error surfaces, not a generic one");

        var info = await (await host.Probe.SingleAsync()).Disconnected.Task.WaitAsync(Wait);
        Assert.That(info.Reason, Is.EqualTo(IonDisconnectReason.Faulted));
        Assert.That(info.Exception, Is.TypeOf<IonStreamInputException>());
    }

    [Test]
    public async Task Session_and_correlation_ids_from_the_query_reach_the_context_and_session_targeting_works()
    {
        await using var raw = await RawStream.OpenAsync(host, "Listen", query: "?sid=tab-7&cid=op-42");
        await raw.SendArgsAsync(w => w.WriteTextString("t"), 1);
        await raw.ExpectAsync(IonStreamProtocol.OpReady);

        var c = await host.Probe.SingleAsync();
        Assert.That(c.Context!.SessionId, Is.EqualTo("tab-7"));
        Assert.That(c.Context.CorrelationId, Is.EqualTo("op-42"));

        Assert.That(await host.Connections.Session("tab-7").SendAsync(new TestContracts.LabEvent(1, "s", "for the tab")), Is.EqualTo(1));
        var item = await raw.ExpectAsync(IonStreamProtocol.OpData);
        Assert.That(IonFormatterStorage<TestContracts.LabEvent>.Read(new CborReader(item)).body, Is.EqualTo("for the tab"));
    }
}
