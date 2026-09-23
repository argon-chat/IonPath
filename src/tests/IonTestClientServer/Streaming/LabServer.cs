namespace IonTestClientServer.Streaming;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using ion.runtime;
using ion.runtime.network;
using TestContracts;

/// <summary>
/// What the server saw of one connection: the hooks in the order they ran, and how it ended.
/// </summary>
public sealed class LabConnection(string connectionId)
{
    public string ConnectionId { get; } = connectionId;
    public string? User { get; set; }
    public string? Method { get; set; }
    public string? Transport { get; set; }
    public IIonStreamContext? Context { get; set; }

    public int ConnectCalls;
    public int DisconnectCalls;
    public int GlobalDisconnectCalls;
    public volatile bool StreamStopped;
    public volatile bool StreamStoppedBeforeDisconnect;
    public volatile bool TokenCancelledBeforeDisconnect;
    public volatile bool ScopeAliveInDisconnect;
    public volatile bool RemovedFromRegistryBeforeDisconnect;
    public volatile IonStreamState StateDuringDisconnect;
    public volatile bool AbortedDuringConnect;
    public string? HttpProtocol;
    public IReadOnlyCollection<string> GroupsAtDisconnect = [];
    public readonly ConcurrentQueue<string> Trace = new();

    public readonly TaskCompletionSource<IonDisconnectInfo> Disconnected =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public readonly TaskCompletionSource StreamStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>The server-side flight recorder every stream test asserts against.</summary>
public sealed class LabProbe
{
    private readonly ConcurrentDictionary<string, LabConnection> connections = new();
    private readonly ConcurrentQueue<string> connectOrder = new();

    /// <summary>How long the service's connect hook stalls — to test a slow join.</summary>
    public TimeSpan ConnectDelay { get; set; }

    /// <summary>Listen connections get a push from inside their own connect hook.</summary>
    public bool PushOnConnect { get; set; }

    /// <summary>Echo waits this long per input item — a slow reader of its input.</summary>
    public TimeSpan EchoDelay { get; set; }

    /// <summary>Echo returns after this many items, whatever input is still coming; 0 for no limit.</summary>
    public int EchoLimit { get; set; }

    /// <summary>The service's disconnect hook throws, after recording, for these users.</summary>
    public HashSet<string> ThrowOnDisconnect { get; } = [];

    public IReadOnlyCollection<LabConnection> All => connections.Values.ToArray();

    public LabConnection For(string connectionId) => connections.GetOrAdd(connectionId, id => new LabConnection(id));

    public LabConnection? Find(string connectionId) => connections.GetValueOrDefault(connectionId);

    public void Recorded(string connectionId) => connectOrder.Enqueue(connectionId);

    /// <summary>The single connection a test opened, once its connect hook has run.</summary>
    public async Task<LabConnection> SingleAsync(TimeSpan? timeout = null)
    {
        await Eventually.TrueAsync(() => connections.Count >= 1, timeout ?? TimeSpan.FromSeconds(10), "no connection reached the server");
        Assert.That(connections, Has.Count.EqualTo(1), "expected exactly one connection");
        return connections.Values.Single();
    }

    public async Task<LabConnection> ByUserAsync(string user, TimeSpan? timeout = null)
    {
        LabConnection? found = null;
        await Eventually.TrueAsync(() => (found = connections.Values.FirstOrDefault(c => c.User == user)) is not null,
            timeout ?? TimeSpan.FromSeconds(10), $"no connection for user {user}");
        return found!;
    }
}

/// <summary>Tickets carry the user name: the ticket exchange's whole job, reduced to a test double.</summary>
/// <remarks>
/// Every token starts with two zero bytes — the case base56 encoders and decoders get wrong — so
/// any client, in any language, that loses them fails every stream test, not one dedicated to it.
/// </remarks>
public sealed class LabTickets : IIonTicketExchange
{
    public const string UserHeader = "x-lab-user";

    public Task<ReadOnlyMemory<byte>> OnExchangeCreateAsync(IIonCallContext callContext)
    {
        var user = callContext.RequestItems.TryGetValue(UserHeader, out var u) ? u : "anonymous";
        return Task.FromResult<ReadOnlyMemory<byte>>((byte[])[0, 0, .. Encoding.UTF8.GetBytes(user)]);
    }

    public Task<(IonProtocolError?, object? ticket)> OnExchangeTransactionAsync(ReadOnlyMemory<byte> exchangeToken)
    {
        if (exchangeToken.Length < 2 || exchangeToken.Span[0] != 0 || exchangeToken.Span[1] != 0)
            return Task.FromResult<(IonProtocolError?, object?)>(
                (new IonProtocolError("TICKET_MANGLED", $"the leading zero bytes were lost: {Convert.ToHexString(exchangeToken.Span)}"), null));

        var user = Encoding.UTF8.GetString(exchangeToken.Span[2..]);
        return Task.FromResult<(IonProtocolError?, object?)>(user == "forged"
            ? (new IonProtocolError("TICKET_REFUSED", "forged ticket"), null)
            : (null, user));
    }

    public void OnTicketApply(object ticketObject)
    {
    }
}

/// <summary>A global hook: checks that global hooks wrap the service's own.</summary>
public sealed class LabGlobalHook(LabProbe probe) : IIonStreamLifecycle
{
    public Task OnConnectedAsync(IIonStreamContext context)
    {
        probe.For(context.ConnectionId).Trace.Enqueue("global-connected");
        return Task.CompletedTask;
    }

    public Task OnDisconnectedAsync(IIonStreamContext context, IonDisconnectInfo info)
    {
        var c = probe.For(context.ConnectionId);
        Interlocked.Increment(ref c.GlobalDisconnectCalls);
        c.Trace.Enqueue("global-disconnected");
        return Task.CompletedTask;
    }
}

/// <summary>A scoped service, to prove the disconnect hook runs inside the connection's scope.</summary>
public sealed class LabScopedMarker : IDisposable
{
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

public sealed class StreamLabImpl(
    LabProbe probe,
    IIonStreamContextAccessor accessor,
    IIonStreamConnections connections,
    LabScopedMarker marker) : IStreamLab, IIonStreamLifecycle
{
    private IIonStreamContext Context => accessor.Context ?? throw new InvalidOperationException("no stream context");

    public async Task OnConnectedAsync(IIonStreamContext context)
    {
        var c = probe.For(context.ConnectionId);
        c.Context = context;
        c.User = context.Ticket as string;
        c.Method = context.Method.Name;
        c.Transport = context.TransportName;
        c.HttpProtocol = context.GetHttpContext().Request.Protocol;
        context.UserIdentifier = c.User;
        Interlocked.Increment(ref c.ConnectCalls);
        c.Trace.Enqueue("connected");
        probe.Recorded(context.ConnectionId);

        if (c.User?.StartsWith("banned", StringComparison.Ordinal) == true)
            throw new IonRequestException(new IonProtocolError("BANNED", "not welcome here"));

        if (c.User == "self-abort")
            context.Abort("changed my mind");

        if (c.User == "close-on-connect")
            context.Close("not now", allowReconnect: true);

        if (probe.PushOnConnect && context.Method.Name == nameof(IStreamLab.Listen))
            await context.SendAsync(new LabEvent(0, "welcome", "sent from OnConnected"));

        if (probe.ConnectDelay > TimeSpan.Zero)
        {
            await Task.Delay(probe.ConnectDelay);
            c.AbortedDuringConnect = context.ConnectionAborted.IsCancellationRequested;
        }
    }

    public Task OnDisconnectedAsync(IIonStreamContext context, IonDisconnectInfo info)
    {
        var c = probe.For(context.ConnectionId);
        c.StreamStoppedBeforeDisconnect = c.StreamStopped || c.Method is null;
        c.TokenCancelledBeforeDisconnect = context.ConnectionAborted.IsCancellationRequested;
        c.ScopeAliveInDisconnect = !marker.Disposed;
        c.GroupsAtDisconnect = context.Groups;
        c.RemovedFromRegistryBeforeDisconnect = connections.Find(context.ConnectionId) is null;
        c.StateDuringDisconnect = context.State;
        Interlocked.Increment(ref c.DisconnectCalls);
        c.Trace.Enqueue("disconnected");
        c.Disconnected.TrySetResult(info);

        // Re-entrancy: closing or aborting a connection from its own disconnect hook is a no-op, not a deadlock.
        context.Close("again");
        context.Abort("and again");

        if (c.User is not null && probe.ThrowOnDisconnect.Contains(c.User))
            throw new InvalidOperationException("OnDisconnected blew up");

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<int> Count(int from, int count, int delayMs, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var c = probe.For(Context.ConnectionId);
        c.StreamStarted.TrySetResult();
        try
        {
            for (var i = 0; i < count; i++)
            {
                yield return from + i;
                if (delayMs > 0)
                    await Task.Delay(delayMs, ct);
            }
        }
        finally
        {
            c.StreamStopped = true;
            c.Trace.Enqueue("stream-stopped");
        }
    }

    public async IAsyncEnumerable<LabEvent> Listen(string topic, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var context = Context;
        var c = probe.For(context.ConnectionId);
        await context.AddToGroupAsync(topic, ct);
        c.StreamStarted.TrySetResult();
        try
        {
            await foreach (var e in IonStream.PushOnly<LabEvent>(ct))
                yield return e;
        }
        finally
        {
            c.StreamStopped = true;
            c.Trace.Enqueue("stream-stopped");
        }
    }

    public async IAsyncEnumerable<ILabSignal> Signals(string room, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var context = Context;
        var c = probe.For(context.ConnectionId);
        await context.AddToGroupAsync("room:" + room, ct);
        c.StreamStarted.TrySetResult();
        try
        {
            await foreach (var e in IonStream.PushOnly<ILabSignal>(ct))
                yield return e;
        }
        finally
        {
            c.StreamStopped = true;
        }
    }

    public async IAsyncEnumerable<string> Echo(IAsyncEnumerable<string>? input, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var c = probe.For(Context.ConnectionId);
        c.StreamStarted.TrySetResult();
        try
        {
            var n = 0;
            await foreach (var s in input!.WithCancellation(ct))
            {
                if (probe.EchoDelay > TimeSpan.Zero)
                    await Task.Delay(probe.EchoDelay, ct);

                yield return s.ToUpperInvariant();

                if (probe.EchoLimit > 0 && ++n >= probe.EchoLimit)
                    yield break;
            }
        }
        finally
        {
            c.StreamStopped = true;
        }
    }

    public async IAsyncEnumerable<int> Explode(int after, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var c = probe.For(Context.ConnectionId);
        c.StreamStarted.TrySetResult();
        try
        {
            for (var i = 0; i < after; i++)
            {
                yield return i;
                await Task.Yield();
            }

            throw new InvalidOperationException("boom");
        }
        finally
        {
            c.StreamStopped = true;
        }
    }

    public async IAsyncEnumerable<int> Stubborn(int holdMs, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var c = probe.For(Context.ConnectionId);
        c.StreamStarted.TrySetResult();
        try
        {
            yield return 1;
            // Deliberately deaf to ct.
            await Task.Delay(holdMs, CancellationToken.None);
            yield return 2;
        }
        finally
        {
            c.StreamStopped = true;
            c.Trace.Enqueue("stream-stopped");
        }
    }

    public async IAsyncEnumerable<IonBytes> Blobs(int size, int count, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var c = probe.For(Context.ConnectionId);
        c.StreamStarted.TrySetResult();
        try
        {
            for (var n = 0; n < count; n++)
            {
                yield return new IonBytes(Blob(size, n));
                await Task.Yield();
            }
        }
        finally
        {
            c.StreamStopped = true;
        }
    }

    public static byte[] Blob(int size, int n)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++)
            data[i] = (byte)((n + i) % 251);
        return data;
    }
}

/// <summary>Polling assertions for things that happen on another thread.</summary>
public static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out after {timeout.TotalSeconds:F1}s: {because}");
            await Task.Delay(10);
        }
    }

    public static async Task<T> WithinAsync<T>(Task<T> task, TimeSpan timeout, string because)
    {
        try
        {
            return await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Timed out after {timeout.TotalSeconds:F1}s: {because}");
            throw;
        }
    }
}
