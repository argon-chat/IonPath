namespace IonRedisBackplaneTests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// A loopback TCP forwarder a test can cut and restore: a network partition between one client
/// and the server, leaving every other client alone.
/// </summary>
internal sealed class TcpProxy : IAsyncDisposable
{
    private readonly string upstreamHost;
    private readonly int upstreamPort;
    private readonly ConcurrentDictionary<Socket, byte> open = new();
    private readonly Lock gate = new();
    private TcpListener? listener;

    public TcpProxy(string upstreamHost, int upstreamPort)
    {
        this.upstreamHost = upstreamHost;
        this.upstreamPort = upstreamPort;

        var first = new TcpListener(IPAddress.Loopback, 0);
        first.Start();
        Port = ((IPEndPoint)first.LocalEndpoint).Port;
        Listen(first);
    }

    /// <summary>The loopback port clients connect to.</summary>
    public int Port { get; }

    /// <summary>Drops every forwarded connection and refuses new ones until <see cref="Restore"/>.</summary>
    public void Cut()
    {
        lock (gate)
        {
            listener?.Stop();
            listener = null;
        }

        foreach (var socket in open.Keys)
            Close(socket);
    }

    /// <summary>Accepts connections again, on the same port.</summary>
    public void Restore()
    {
        var next = new TcpListener(IPAddress.Loopback, Port);
        next.Start();
        Listen(next);
    }

    private void Listen(TcpListener next)
    {
        lock (gate)
            listener = next;
        _ = AcceptAsync(next);
    }

    private async Task AcceptAsync(TcpListener from)
    {
        while (true)
        {
            Socket client;
            try
            {
                client = await from.AcceptSocketAsync();
            }
            catch (Exception) // stopped
            {
                return;
            }

            _ = ForwardAsync(client);
        }
    }

    private async Task ForwardAsync(Socket client)
    {
        var server = new Socket(SocketType.Stream, ProtocolType.Tcp);
        open[client] = 0;
        open[server] = 0;
        try
        {
            await server.ConnectAsync(upstreamHost, upstreamPort);
            await Task.WhenAny(PumpAsync(client, server), PumpAsync(server, client));
        }
        catch (Exception)
        {
            // Either side went away; closing both below is all there is to do.
        }
        finally
        {
            Close(client);
            Close(server);
        }
    }

    private static async Task PumpAsync(Socket from, Socket to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None);
                if (read == 0)
                    return;
                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None);
            }
        }
        catch (Exception)
        {
            // A closed socket ends the pump.
        }
    }

    private void Close(Socket socket)
    {
        open.TryRemove(socket, out _);
        try
        {
            // Abortive close: the peer sees a reset, as with a cable pulled or a node gone.
            socket.LingerState = new LingerOption(true, 0);
            socket.Close();
        }
        catch (Exception)
        {
            // Already closed.
        }
    }

    public ValueTask DisposeAsync()
    {
        Cut();
        return ValueTask.CompletedTask;
    }
}
