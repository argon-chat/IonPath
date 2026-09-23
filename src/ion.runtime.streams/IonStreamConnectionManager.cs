namespace ion.runtime.network;

using System.Collections.Concurrent;
using System.Formats.Cbor;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>The process-wide registry behind <see cref="IIonStreamConnections"/>.</summary>
/// <remarks>
/// <para><b>No global lock.</b> Two things need care and neither needs the whole server to queue
/// up. A connection that is leaving must never be added back to a group behind its own removal —
/// the leak SignalR users otherwise chase by hand — so each connection's group set and its
/// <see cref="IIonHubConnection.Removed"/> flag change together under that connection's own
/// monitor, which only its own group calls ever contend for. And a group emptied by one call must
/// not swallow a member another call is adding at that moment, so the group index is lock-free:
/// an emptied group is removed only if it is still the same instance, and an add that lands in a
/// group removed under it notices and retries. Broadcasts read the concurrent dictionaries and take
/// no lock at all; a connection they catch mid-removal refuses the push itself.</para>
///
/// <para>With an <see cref="IIonStreamBackplane"/> registered, every operation on a nameable target
/// runs here first and is then published for the other nodes, which run it against their own
/// connections.</para>
/// </remarks>
internal sealed class IonStreamConnectionManager : IIonStreamConnections
{
    private readonly ConcurrentDictionary<string, IIonHubConnection> connections = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IIonHubConnection>> groups =
        new(StringComparer.Ordinal);

    private readonly IOptions<IonStreamOptions> options;
    private readonly ILogger log;
    private readonly IIonStreamBackplane? backplane;
    private int shutdownHooked;

    public IonStreamConnectionManager(
        IOptions<IonStreamOptions> options,
        ILogger<IonStreamConnectionManager> log,
        IServiceProvider services)
    {
        this.options = options;
        this.log = log;
        backplane = services.GetService<IIonStreamBackplane>();
    }

    /// <summary>This process's identity on the backplane.</summary>
    internal string NodeId { get; } = Guid.NewGuid().ToString("N");

    public int Count => connections.Count;

    public IIonStreamContext? Find(string connectionId)
        => connections.TryGetValue(connectionId, out var c) ? c : null;

    public IIonStreamTarget All => new Target(this, IonBackplaneTargetKind.All, null, null);

    public IIonStreamTarget Client(string connectionId)
        => new Target(this, IonBackplaneTargetKind.Connections, null, [connectionId]);

    public IIonStreamTarget Clients(IEnumerable<string> connectionIds)
        => new Target(this, IonBackplaneTargetKind.Connections, null, connectionIds.Distinct(StringComparer.Ordinal).ToArray());

    public IIonStreamTarget Group(string group)
        => new Target(this, IonBackplaneTargetKind.Group, group, null);

    public IIonStreamTarget User(string userIdentifier)
        => new Target(this, IonBackplaneTargetKind.User, userIdentifier, null);

    public IIonStreamTarget Session(string sessionId)
        => new Target(this, IonBackplaneTargetKind.Session, sessionId, null);

    public IIonStreamTarget Where(Func<IIonStreamContext, bool> predicate)
        => new Target(this, predicate);

    public async Task AddToGroupAsync(string connectionId, string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);

        if (connections.TryGetValue(connectionId, out var c))
            AddToGroup(c, group);
        else if (backplane is not null)
            await PublishAsync(IonBackplaneCommand.AddToGroup, IonBackplaneTargetKind.Connections, [connectionId], [],
                group: group, ct: ct).ConfigureAwait(false);
    }

    public async Task RemoveFromGroupAsync(string connectionId, string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);

        if (connections.TryGetValue(connectionId, out var c))
            RemoveFromGroup(c, group);
        else if (backplane is not null)
            await PublishAsync(IonBackplaneCommand.RemoveFromGroup, IonBackplaneTargetKind.Connections, [connectionId], [],
                group: group, ct: ct).ConfigureAwait(false);
    }

    internal void Add(IIonHubConnection connection) => connections[connection.ConnectionId] = connection;

    internal void Remove(IIonHubConnection connection)
    {
        string[] left;

        // After this block no group call on this connection can succeed, and `left` is every group
        // it was in — the two are decided together, under the connection's own monitor.
        lock (connection.GroupSet)
        {
            connection.Removed = true;
            left = connection.GroupSet.Count == 0 ? [] : connection.GroupSet.ToArray();
        }

        connections.TryRemove(new KeyValuePair<string, IIonHubConnection>(connection.ConnectionId, connection));

        // GroupSet is left as it was: the disconnect hooks read it as "the groups it was in". No
        // add can race these removals any more — Removed is set — so they need no monitor.
        foreach (var group in left)
            Reinstate(group, Detach(group, connection));
    }

    internal bool AddToGroup(IIonHubConnection connection, string group)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);

        lock (connection.GroupSet)
        {
            if (connection.Removed || connection.Disconnect is not null)
                return false;

            // Indexed while still holding the connection's monitor, so a concurrent Remove sees
            // either no membership or a complete one — never a set entry without its index entry.
            if (connection.GroupSet.Add(group))
                AddMember(group, connection);

            return true;
        }
    }

    internal bool RemoveFromGroup(IIonHubConnection connection, string group)
    {
        ArgumentException.ThrowIfNullOrEmpty(group);

        ConcurrentDictionary<string, IIonHubConnection>? orphaned;
        lock (connection.GroupSet)
        {
            if (connection.Removed || !connection.GroupSet.Remove(group))
                return false;

            // Under the monitor, like the add: detached outside it, this removal could land after a
            // later add of the same group and undo it.
            orphaned = Detach(group, connection);
        }

        // Outside it: reinstating takes other connections' monitors, and holding two is how
        // deadlocks start.
        Reinstate(group, orphaned);
        return true;
    }

    internal IReadOnlyCollection<string> GroupsOf(IIonHubConnection connection)
    {
        lock (connection.GroupSet)
            return connection.GroupSet.ToArray();
    }

    /// <summary>The group names with at least one member (diagnostics and tests).</summary>
    internal IReadOnlyCollection<string> GroupNames => groups.Keys.ToArray();

    /// <summary>
    /// Puts a connection in a group's index. If the group's dictionary was emptied and dropped from
    /// the index between finding it and adding to it, the add went into an orphan: try again with
    /// the group's current dictionary.
    /// </summary>
    private void AddMember(string group, IIonHubConnection connection)
    {
        while (true)
        {
            var members = groups.GetOrAdd(group, static _ => new(StringComparer.Ordinal));
            members[connection.ConnectionId] = connection;

            if (groups.TryGetValue(group, out var current) && ReferenceEquals(current, members))
                return;
        }
    }

    /// <summary>
    /// Takes a connection out of a group's index and drops the group once it is empty. Returns the
    /// dropped dictionary, if this call dropped one, for <see cref="Reinstate"/>.
    /// </summary>
    /// <remarks>
    /// Emptiness is checked, then the group is dropped — and another connection's add can land in
    /// between. Both orders are covered: an add that finishes after the drop sees its dictionary gone
    /// and retries (<see cref="AddMember"/>); one that finished before it is still in the dropped
    /// dictionary, and <see cref="Reinstate"/> puts it back.
    /// </remarks>
    private ConcurrentDictionary<string, IIonHubConnection>? Detach(string group, IIonHubConnection connection)
    {
        if (!groups.TryGetValue(group, out var members))
            return null;

        members.TryRemove(new KeyValuePair<string, IIonHubConnection>(connection.ConnectionId, connection));

        return members.IsEmpty &&
               groups.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, IIonHubConnection>>(group, members))
            ? members
            : null;
    }

    /// <summary>
    /// Re-indexes the members an add slipped into a group dictionary just before it was dropped —
    /// each under its own monitor, and only while it still counts the group as its own, so a member
    /// that has left meanwhile is not resurrected.
    /// </summary>
    private void Reinstate(string group, ConcurrentDictionary<string, IIonHubConnection>? dropped)
    {
        if (dropped is null || dropped.IsEmpty)
            return;

        foreach (var (_, survivor) in dropped)
        {
            lock (survivor.GroupSet)
            {
                if (!survivor.Removed && survivor.GroupSet.Contains(group))
                    AddMember(group, survivor);
            }
        }
    }

    /// <summary>
    /// Closes every connection when the host starts stopping, so clients get a goodbye that says
    /// "come back" instead of a transport the server kills once its shutdown timeout runs out.
    /// </summary>
    internal void HookShutdown(IHostApplicationLifetime lifetime)
    {
        if (Interlocked.Exchange(ref shutdownHooked, 1) == 1)
            return;

        lifetime.ApplicationStopping.Register(ShutdownAll);
    }

    internal void ShutdownAll()
    {
        var all = connections.Values.ToArray();
        if (all.Length == 0)
            return;

        log.LogInformation("Closing {Count} Ion stream connection(s) for shutdown", all.Length);

        foreach (var connection in all)
            connection.Shutdown();

        var o = options.Value;
        var budget = o.CloseTimeout > TimeSpan.Zero && o.StreamStopTimeout > TimeSpan.Zero
            ? o.CloseTimeout + o.StreamStopTimeout
            : TimeSpan.FromSeconds(30);

        try
        {
            // ApplicationStopping callbacks are synchronous; blocking here is what keeps Kestrel from
            // tearing the transports down before the goodbyes are out.
            if (!Task.WhenAll(all.Select(c => c.Completion)).Wait(budget))
                log.LogWarning("Not every Ion stream connection closed within {Budget}", budget);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Waiting for Ion stream connections to close failed");
        }
    }

    // ── backplane ──────────────────────────────────────────────────────────────────────────────

    internal Task StartBackplaneAsync(CancellationToken ct)
        => backplane is null ? Task.CompletedTask : backplane.StartAsync(NodeId, OnBackplaneMessageAsync, ct);

    internal Task StopBackplaneAsync(CancellationToken ct)
        => backplane is null ? Task.CompletedTask : backplane.StopAsync(ct);

    private async ValueTask PublishAsync(
        IonBackplaneCommand command,
        IonBackplaneTargetKind kind,
        string[] targets,
        string[] excluded,
        string? group = null,
        string? payloadType = null,
        ReadOnlyMemory<byte> payload = default,
        string? reason = null,
        bool allowReconnect = false,
        CancellationToken ct = default)
    {
        if (backplane is null)
            return;

        var message = new IonBackplaneMessage
        {
            OriginNodeId = NodeId,
            Command = command,
            TargetKind = kind,
            Targets = targets,
            Excluded = excluded,
            Group = group,
            PayloadType = payloadType,
            Payload = payload,
            Reason = reason,
            AllowReconnect = allowReconnect
        };

        try
        {
            await backplane.PublishAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Local delivery already happened; a backplane outage must not fail the caller's push.
            log.LogError(ex, "Publishing {Command} to the Ion stream backplane failed", command);
        }
    }

    private ValueTask OnBackplaneMessageAsync(IonBackplaneMessage message)
    {
        if (message.OriginNodeId == NodeId)
            return ValueTask.CompletedTask;

        try
        {
            var target = new Target(this, message.TargetKind,
                message.TargetKind is IonBackplaneTargetKind.Connections or IonBackplaneTargetKind.All ? null : message.Targets.FirstOrDefault(),
                message.TargetKind == IonBackplaneTargetKind.Connections ? message.Targets : null,
                message.Excluded.Length == 0 ? null : new HashSet<string>(message.Excluded, StringComparer.Ordinal),
                local: true);

            switch (message.Command)
            {
                case IonBackplaneCommand.Send:
                    target.DeliverRemote(message);
                    break;
                case IonBackplaneCommand.Close:
                    foreach (var c in target.Resolve())
                        c.Close(message.Reason, message.AllowReconnect);
                    break;
                case IonBackplaneCommand.Abort:
                    foreach (var c in target.Resolve())
                        c.Abort(message.Reason);
                    break;
                case IonBackplaneCommand.AddToGroup when message.Group is not null:
                    foreach (var c in target.Resolve())
                        AddToGroup(c, message.Group);
                    break;
                case IonBackplaneCommand.RemoveFromGroup when message.Group is not null:
                    foreach (var c in target.Resolve())
                        RemoveFromGroup(c, message.Group);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Applying a backplane {Command} from node {Node} failed", message.Command, message.OriginNodeId);
        }

        return ValueTask.CompletedTask;
    }

    private static readonly ConcurrentDictionary<string, Type?> PayloadTypes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Type, Func<ReadOnlyMemory<byte>, object?>> Decoders = new();
    private static readonly ConcurrentDictionary<Type, Func<object?, byte[]>> PayloadEncoders = new();

    private static readonly MethodInfo DecodeDefinition =
        typeof(IonStreamConnectionManager).GetMethod(nameof(DecodePayload), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo EncodeDefinition =
        typeof(IonStreamConnectionManager).GetMethod(nameof(EncodePayload), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static object? DecodePayload<T>(ReadOnlyMemory<byte> payload)
        => IonFormatterStorage<T>.Read(new CborReader(payload));

    private static byte[] EncodePayload<T>(object? item)
    {
        var writer = new CborWriter();
        IonFormatterStorage<T>.Write(writer, (T)item!);
        return writer.Encode();
    }

    private static Type? ResolvePayloadType(string name)
        => PayloadTypes.GetOrAdd(name, static n => Type.GetType(n, throwOnError: false));

    /// <summary>
    /// A set of connections named by kind and key, resolved when an operation runs. No closures: a
    /// target is one small object, cheap enough to create per call and safe to cache.
    /// </summary>
    private sealed class Target : IIonStreamTarget
    {
        private readonly IonStreamConnectionManager manager;
        private readonly IonBackplaneTargetKind kind;
        private readonly string? key;
        private readonly string[]? ids;
        private readonly Func<IIonStreamContext, bool>? predicate;
        private readonly HashSet<string>? excluded;
        private readonly bool local;

        public Target(IonStreamConnectionManager manager, IonBackplaneTargetKind kind, string? key, string[]? ids,
            HashSet<string>? excluded = null, bool local = false)
        {
            this.manager = manager;
            this.kind = kind;
            this.key = key;
            this.ids = ids;
            this.excluded = excluded;
            this.local = local;
        }

        public Target(IonStreamConnectionManager manager, Func<IIonStreamContext, bool> predicate, HashSet<string>? excluded = null)
        {
            this.manager = manager;
            this.predicate = predicate;
            this.excluded = excluded;
            local = true;
        }

        /// <summary>A target the backplane cannot carry: a predicate, or one already delivered by a peer.</summary>
        private bool Distributed => !local && manager.backplane is not null;

        public IEnumerable<IIonHubConnection> Resolve()
        {
            IEnumerable<IIonHubConnection> selected;

            if (predicate is not null)
            {
                selected = manager.connections.Values.Where(c => predicate(c));
            }
            else
            {
                switch (kind)
                {
                    case IonBackplaneTargetKind.All:
                        selected = manager.connections.Values;
                        break;
                    case IonBackplaneTargetKind.Connections:
                        selected = SelectIds(manager, ids!);
                        break;
                    case IonBackplaneTargetKind.Group:
                        selected = manager.groups.TryGetValue(key!, out var members) ? members.Values : [];
                        break;
                    case IonBackplaneTargetKind.User:
                        selected = manager.connections.Values.Where(c => string.Equals(c.UserIdentifier, key, StringComparison.Ordinal));
                        break;
                    case IonBackplaneTargetKind.Session:
                        selected = manager.connections.Values.Where(c => string.Equals(c.SessionId, key, StringComparison.Ordinal));
                        break;
                    default:
                        selected = [];
                        break;
                }
            }

            return excluded is null ? selected : selected.Where(c => !excluded.Contains(c.ConnectionId));

            static IEnumerable<IIonHubConnection> SelectIds(IonStreamConnectionManager manager, string[] ids)
            {
                foreach (var id in ids)
                    if (manager.connections.TryGetValue(id, out var c))
                        yield return c;
            }
        }

        public IReadOnlyList<IIonStreamContext> Connections => Resolve().ToArray();

        public IIonStreamTarget Except(params string[] connectionIds)
        {
            var set = new HashSet<string>(connectionIds, StringComparer.Ordinal);
            if (excluded is not null)
                set.UnionWith(excluded);

            return predicate is not null
                ? new Target(manager, predicate, set)
                : new Target(manager, kind, key, ids, set, local);
        }

        public async ValueTask<int> SendAsync<T>(T item, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var queued = DeliverLocal(item);

            if (Distributed && !AllLocal())
            {
                var encoder = PayloadEncoders.GetOrAdd(typeof(T), static t =>
                    EncodeDefinition.MakeGenericMethod(t).CreateDelegate<Func<object?, byte[]>>());

                await manager.PublishAsync(IonBackplaneCommand.Send, kind, Keys(), Excluded(),
                    payloadType: typeof(T).AssemblyQualifiedName, payload: encoder(item), ct: ct).ConfigureAwait(false);
            }

            return queued;
        }

        /// <summary>Queues <paramref name="item"/> to every local connection that can take it, encoding once per element type.</summary>
        private int DeliverLocal<T>(T item)
        {
            Type? firstType = null;
            byte[]? firstFrame = null;
            Dictionary<Type, byte[]>? otherFrames = null;
            var queued = 0;

            foreach (var connection in Resolve())
            {
                if (!connection.Accepts(typeof(T), item))
                    continue;

                byte[] frame;
                if (firstType is null)
                {
                    firstType = connection.ElementType;
                    frame = firstFrame = IonStreamPush.Encode(firstType, item);
                }
                else if (firstType == connection.ElementType)
                {
                    frame = firstFrame!;
                }
                else
                {
                    otherFrames ??= new Dictionary<Type, byte[]>();
                    if (!otherFrames.TryGetValue(connection.ElementType, out frame!))
                        otherFrames[connection.ElementType] = frame = IonStreamPush.Encode(connection.ElementType, item);
                }

                if (connection.TryEnqueue(frame))
                    queued++;
            }

            return queued;
        }

        /// <summary>
        /// Delivers a peer's push. When a connection's element type is the payload type, the peer's
        /// bytes are the frame as they are; otherwise the item is decoded once and re-encoded once per
        /// element type.
        /// </summary>
        public void DeliverRemote(IonBackplaneMessage message)
        {
            if (message.PayloadType is null || ResolvePayloadType(message.PayloadType) is not { } payloadType)
            {
                manager.log.LogWarning("Dropping a backplane push of unknown type {Type}", message.PayloadType);
                return;
            }

            byte[]? asIs = null;
            object? decoded = null;
            var isDecoded = false;
            Dictionary<Type, byte[]>? reencoded = null;

            foreach (var connection in Resolve())
            {
                byte[] frame;
                if (connection.ElementType == payloadType)
                {
                    frame = asIs ??= IonStreamProtocol.Frame(IonStreamProtocol.OpData, message.Payload.Span);
                }
                else
                {
                    if (!isDecoded)
                    {
                        decoded = Decoders.GetOrAdd(payloadType, static t =>
                            DecodeDefinition.MakeGenericMethod(t).CreateDelegate<Func<ReadOnlyMemory<byte>, object?>>())(message.Payload);
                        isDecoded = true;
                    }

                    if (!connection.Accepts(payloadType, decoded))
                        continue;

                    reencoded ??= new Dictionary<Type, byte[]>();
                    if (!reencoded.TryGetValue(connection.ElementType, out frame!))
                        reencoded[connection.ElementType] = frame = IonStreamPush.Encode(connection.ElementType, decoded);
                }

                connection.TryEnqueue(frame);
            }
        }

        public async Task<int> CloseAsync(string? reason = null, bool allowReconnect = false, CancellationToken ct = default)
        {
            var selected = Resolve().ToArray();
            foreach (var connection in selected)
                connection.Close(reason, allowReconnect);

            if (Distributed && !AllLocal())
                await manager.PublishAsync(IonBackplaneCommand.Close, kind, Keys(), Excluded(),
                    reason: reason, allowReconnect: allowReconnect, ct: ct).ConfigureAwait(false);

            await Task.WhenAll(selected.Select(c => c.Completion)).WaitAsync(ct).ConfigureAwait(false);
            return selected.Length;
        }

        public int Abort(string? reason = null)
        {
            var selected = Resolve().ToArray();
            foreach (var connection in selected)
                connection.Abort(reason);

            if (Distributed && !AllLocal())
                _ = manager.PublishAsync(IonBackplaneCommand.Abort, kind, Keys(), Excluded(), reason: reason).AsTask();

            return selected.Length;
        }

        /// <summary>True when every named connection is on this node, so there is nobody to tell.</summary>
        private bool AllLocal()
        {
            if (kind != IonBackplaneTargetKind.Connections || ids is null)
                return false;

            foreach (var id in ids)
                if (!manager.connections.ContainsKey(id))
                    return false;
            return true;
        }

        private string[] Keys() => kind switch
        {
            IonBackplaneTargetKind.All => [],
            IonBackplaneTargetKind.Connections => ids ?? [],
            _ => [key!]
        };

        private string[] Excluded() => excluded is null ? [] : excluded.ToArray();
    }
}

/// <summary>Starts the backplane with the host, closes every connection when it stops.</summary>
internal sealed class IonStreamHostedService(IonStreamConnectionManager manager, IHostApplicationLifetime lifetime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        manager.HookShutdown(lifetime);
        return manager.StartBackplaneAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => manager.StopBackplaneAsync(cancellationToken);
}
