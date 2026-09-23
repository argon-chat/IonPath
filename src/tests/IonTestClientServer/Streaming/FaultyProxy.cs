namespace IonTestClientServer.Streaming;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// A loopback relay that can fail the way networks fail: TCP (WebSockets, the ticket exchange) and
/// UDP (QUIC, so WebTransport) on the same port, forwarded to a server.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><see cref="Freeze"/> is a partition: every socket stays open, nothing moves, no FIN, no
///   RST, UDP datagrams are dropped — a closed laptop lid, a dead Wi-Fi, a NAT that forgot the flow.
///   Only a heartbeat can tell.</item>
///   <item><see cref="Thaw"/> ends it; whatever was held back flows again.</item>
///   <item><see cref="Reset"/> kills every TCP pair with an RST.</item>
///   <item><see cref="CutClientSide"/> kills only the client's half of every TCP pair: the client
///   sees an RST at once, the server's socket stays open and silent — so the server believes the
///   old transport alive until its heartbeat says otherwise. What a client that notices a network
///   switch before the server does looks like.</item>
///   <item><see cref="FreezeUdp"/> is a partition of UDP only: WebTransport dies, WebSockets live —
///   a network that starts dropping QUIC mid-session.</item>
/// </list>
/// </remarks>
public sealed class FaultyProxy : IAsyncDisposable
{
    private readonly TcpListener tcp;
    private readonly UdpClient udp;
    private readonly IPEndPoint target;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Socket> sockets = [];
    private readonly ConcurrentBag<Pair> pairs = [];
    private readonly ConcurrentDictionary<IPEndPoint, UdpClient> upstreams = new();
    private readonly List<Task> loops = [];
    private volatile TaskCompletionSource open = Opened();
    private volatile TaskCompletionSource udpOpen = Opened();

    private FaultyProxy(TcpListener tcp, UdpClient udp, int port, int targetPort)
    {
        this.tcp = tcp;
        this.udp = udp;
        Port = port;
        target = new IPEndPoint(IPAddress.Loopback, targetPort);
        loops.Add(AcceptAsync());
        loops.Add(RelayFromClientsAsync());
    }

    public int Port { get; }

    public Uri BaseAddress => new($"https://localhost:{Port}");

    public static FaultyProxy Start(int targetPort)
    {
        for (var i = 0; i < 200; i++)
        {
            var port = Random.Shared.Next(20_000, 40_000);
            UdpClient? udp = null;
            try
            {
                udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                IgnoreIcmpResets(udp);
                var tcp = new TcpListener(IPAddress.Loopback, port);
                tcp.Start();
                return new FaultyProxy(tcp, udp, port, targetPort);
            }
            catch (SocketException)
            {
                udp?.Dispose();
            }
        }

        throw new InvalidOperationException("No loopback port is free for both TCP and UDP.");
    }

    public void Freeze()
    {
        if (open.Task.IsCompleted)
            open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Thaw() => open.TrySetResult();

    public void FreezeUdp()
    {
        if (udpOpen.Task.IsCompleted)
            udpOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void ThawUdp() => udpOpen.TrySetResult();

    /// <summary>TCP pairs accepted so far — each client connection, the ticket exchanges included.</summary>
    public int Connections => pairs.Count;

    public void CutClientSide()
    {
        foreach (var pair in pairs)
        {
            if (pair.Orphaned)
                continue;

            pair.Orphaned = true;
            try
            {
                pair.Client.LingerState = new LingerOption(true, 0);
                pair.Client.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    public void Reset()
    {
        foreach (var socket in sockets)
        {
            try
            {
                socket.LingerState = new LingerOption(true, 0);
                socket.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    private bool Frozen => !open.Task.IsCompleted;

    private bool UdpFrozen => Frozen || !udpOpen.Task.IsCompleted;

    private Task Gate() => open.Task.WaitAsync(stop.Token);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await tcp.AcceptSocketAsync(stop.Token);
                var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                client.NoDelay = true;
                try
                {
                    await server.ConnectAsync(target, stop.Token);
                }
                catch (Exception)
                {
                    client.Close();
                    server.Dispose();
                    continue;
                }

                sockets.Add(client);
                sockets.Add(server);
                var pair = new Pair(client, server);
                pairs.Add(pair);
                _ = PumpAsync(pair, client, server);
                _ = PumpAsync(pair, server, client);
            }
        }
        catch (Exception)
        {
            // Stopped.
        }
    }

    private async Task PumpAsync(Pair pair, Socket from, Socket to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                await Gate();
                var read = await from.ReceiveAsync(buffer, SocketFlags.None, stop.Token);
                if (read == 0)
                {
                    to.Shutdown(SocketShutdown.Send);
                    return;
                }

                await Gate();
                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, stop.Token);
            }
        }
        catch (Exception)
        {
            // An orphaned pair keeps its server half open, and quiet: the server must find out
            // for itself that nobody is there any more.
            if (!pair.Orphaned || !ReferenceEquals(from, pair.Server))
                try { from.Close(); } catch (Exception) { }
            if (!pair.Orphaned || !ReferenceEquals(to, pair.Server))
                try { to.Close(); } catch (Exception) { }
        }
    }

    private sealed class Pair(Socket client, Socket server)
    {
        public Socket Client { get; } = client;
        public Socket Server { get; } = server;
        public volatile bool Orphaned;
    }

    /// <summary>Datagrams from clients, each client getting its own upstream socket so replies find their way back.</summary>
    private async Task RelayFromClientsAsync()
    {
        try
        {
            while (true)
            {
                var datagram = await udp.ReceiveAsync(stop.Token);
                if (UdpFrozen)
                    continue;

                var upstream = upstreams.GetOrAdd(datagram.RemoteEndPoint, client =>
                {
                    var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    IgnoreIcmpResets(socket);
                    socket.Connect(target);
                    lock (loops)
                        loops.Add(RelayToClientAsync(socket, client));
                    return socket;
                });

                await upstream.SendAsync(datagram.Buffer, stop.Token);
            }
        }
        catch (Exception)
        {
            // Stopped.
        }
    }

    private async Task RelayToClientAsync(UdpClient upstream, IPEndPoint client)
    {
        try
        {
            while (true)
            {
                var datagram = await upstream.ReceiveAsync(stop.Token);
                if (!UdpFrozen)
                    await udp.SendAsync(datagram.Buffer, client, stop.Token);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// On Windows an ICMP port-unreachable for an earlier send surfaces as a reset on the next
    /// receive of a UDP socket, which would end a relay loop over a datagram nobody cares about.
    /// </summary>
    private static void IgnoreIcmpResets(UdpClient client)
    {
        if (!OperatingSystem.IsWindows())
            return;

        const int SioUdpConnReset = -1744830452;
        client.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        Thaw();
        ThawUdp();
        tcp.Stop();
        udp.Dispose();
        Reset();
        foreach (var pair in pairs)
            try { pair.Server.Close(); } catch (Exception) { }
        foreach (var upstream in upstreams.Values)
            upstream.Dispose();

        Task[] all;
        lock (loops)
            all = loops.ToArray();
        try { await Task.WhenAll(all).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        stop.Dispose();
    }

    private static TaskCompletionSource Opened()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}
