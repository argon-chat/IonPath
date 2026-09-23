namespace ion.runtime.network;

using System.Formats.Cbor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

/// <summary>
/// An <see cref="IIonStreamBackplane"/> on one Redis stream: every node appends its operations with
/// <c>XADD</c> and reads everybody's with a blocking <c>XREAD</c>. Works against Redis 5+, Valkey
/// and Dragonfly.
/// </summary>
/// <remarks>
/// <para><b>Why Streams, not pub/sub.</b> Pub/sub forgets a message the moment it has been fanned
/// out, so a node whose subscription connection blips silently loses whatever was published
/// meanwhile. A stream keeps its entries (up to <see cref="IonRedisBackplaneOptions.MaxLength"/>),
/// and a reader that remembers the id of the last entry it saw resumes exactly there after a
/// reconnect. It also makes "live" checkable: <see cref="StartAsync"/> reads the stream's last id,
/// and everything appended afterwards has, by construction, a larger one.</para>
///
/// <para><b>Why no consumer group.</b> A group splits entries between its consumers — a work queue.
/// A backplane needs the opposite, every node seeing every entry, so each node reads the stream on
/// its own with plain <c>XREAD</c> and keeps its position in memory. Nothing is acknowledged, and
/// there is no per-node state in Redis to clean up when a node goes away.</para>
///
/// <para><b>Why a dedicated reader connection.</b> A <see cref="ConnectionMultiplexer"/> pipelines
/// every command over one socket and matches replies in order, so a blocking <c>XREAD</c> holds up
/// every command queued behind it for as long as it blocks. The reader therefore gets a multiplexer
/// of its own that runs nothing else, while publishing goes through the regular, shared one. It is
/// also why the typed <c>StreamReadAsync</c> has no <c>BLOCK</c>, and why the read is issued raw
/// through <see cref="IDatabaseAsync.ExecuteAsync(string, ICollection{object}, CommandFlags)"/>.</para>
///
/// <para><b>Guarantees.</b> A node receives every other node's messages at most once and in stream
/// order, which is publish order for any one publisher, and never its own. A failed read — a
/// dropped connection, a failover — is retried from the last id seen once the multiplexer has
/// reconnected, so nothing is lost as long as the node is back before
/// <see cref="IonRedisBackplaneOptions.MaxLength"/> newer entries trim the missed ones away. What is
/// published while the backplane is stopped is not replayed: a start begins at the stream's end.</para>
/// </remarks>
public sealed class RedisStreamsBackplane : IIonStreamBackplane, IAsyncDisposable, IDisposable
{
    /// <summary>The one field of every entry; its value is <see cref="IonBackplaneMessage.Encode"/>'s output.</summary>
    private static readonly RedisValue PayloadField = "m";

    /// <summary>Below every id the server assigns, so reading after it reads the whole stream.</summary>
    private static readonly RedisValue ZeroId = "0-0";

    /// <summary>Where the last-seen id sits in the <c>XREAD</c> arguments.</summary>
    private const int LastIdArgument = 6;

    private readonly IonRedisBackplaneOptions options;
    private readonly ILogger log;
    private readonly Func<ConfigurationOptions> configuration;
    private readonly RedisKey streamKey;
    private readonly StreamAddOptions addOptions;

    // The fixed XREAD arguments, boxed once.
    private readonly object countArgument;
    private readonly object blockArgument;
    private readonly object keyArgument;

    private readonly Lock publisherGate = new();
    private IDatabaseAsync? publishDatabase;
    private Task<IDatabaseAsync>? publisherConnecting;
    private ConnectionMultiplexer? ownedPublisher;

    // Serializes start, stop and dispose; never disposed, so a late StopAsync still works.
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private Session? session;
    private volatile bool disposed;

    private long delivered;
    private long readFailures;

    /// <summary>
    /// A backplane that opens its own connections from <see cref="IonRedisBackplaneOptions.Configuration"/>
    /// or <see cref="IonRedisBackplaneOptions.ConfigurationFactory"/>: one to publish, one to read.
    /// </summary>
    public RedisStreamsBackplane(IOptions<IonRedisBackplaneOptions> options, ILogger<RedisStreamsBackplane>? logger = null)
        : this(options?.Value!, publisher: null, logger)
    {
    }

    /// <summary>
    /// A backplane that publishes through <paramref name="connection"/> — the application's shared
    /// multiplexer, which it does not dispose — and reads on a dedicated one. The reader connects
    /// with <see cref="IonRedisBackplaneOptions.ConfigurationFactory"/> or
    /// <see cref="IonRedisBackplaneOptions.Configuration"/> when set, and with
    /// <paramref name="connection"/>'s own configuration string otherwise (which carries no
    /// callbacks, such as certificate validation — set the factory when those matter).
    /// </summary>
    public RedisStreamsBackplane(IConnectionMultiplexer connection, IOptions<IonRedisBackplaneOptions> options,
        ILogger<RedisStreamsBackplane>? logger = null)
        : this(options?.Value!, connection ?? throw new ArgumentNullException(nameof(connection)), logger)
    {
    }

    private RedisStreamsBackplane(IonRedisBackplaneOptions options, IConnectionMultiplexer? publisher, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StreamKey, $"{nameof(IonRedisBackplaneOptions)}.{nameof(IonRedisBackplaneOptions.StreamKey)}");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ReadBatchSize, $"{nameof(IonRedisBackplaneOptions)}.{nameof(IonRedisBackplaneOptions.ReadBatchSize)}");
        if (options.BlockTimeout < TimeSpan.FromMilliseconds(1) || options.BlockTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(options), options.BlockTimeout,
                $"{nameof(IonRedisBackplaneOptions)}.{nameof(IonRedisBackplaneOptions.BlockTimeout)} must be between 1 ms and 1 hour");
        if (options.MinRetryDelay < TimeSpan.Zero || options.MaxRetryDelay < options.MinRetryDelay)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(IonRedisBackplaneOptions)} retry delays must satisfy 0 <= {nameof(IonRedisBackplaneOptions.MinRetryDelay)} <= {nameof(IonRedisBackplaneOptions.MaxRetryDelay)}");

        this.options = options;
        log = logger ?? NullLogger.Instance;

        if (options.ConfigurationFactory is { } factory)
            configuration = factory;
        else if (options.Configuration is { Length: > 0 } text)
            configuration = () => ConfigurationOptions.Parse(text);
        else if (publisher is not null)
            configuration = () => ConfigurationOptions.Parse(publisher.Configuration);
        else
            throw new InvalidOperationException(
                $"The Redis stream backplane needs {nameof(IonRedisBackplaneOptions)}.{nameof(IonRedisBackplaneOptions.Configuration)}, " +
                $"{nameof(IonRedisBackplaneOptions)}.{nameof(IonRedisBackplaneOptions.ConfigurationFactory)} or a connection to publish through");

        if (publisher is not null)
            publishDatabase = publisher.GetDatabase();

        streamKey = options.StreamKey;
        addOptions = options.MaxLength > 0
            ? new StreamAddOptions { MaxLength = options.MaxLength, Approximate = true }
            : default;

        countArgument = options.ReadBatchSize;
        blockArgument = (int)Math.Ceiling(options.BlockTimeout.TotalMilliseconds);
        keyArgument = streamKey;
    }

    /// <summary>The name the running reader connection has in <c>CLIENT LIST</c>; null while stopped.</summary>
    internal string? ReaderClientName => Volatile.Read(ref session)?.ReaderName;

    /// <summary>True between a successful <see cref="StartAsync"/> and the matching <see cref="StopAsync"/>.</summary>
    internal bool IsRunning => Volatile.Read(ref session) is not null;

    /// <summary>How many messages have been handed to a handler, over every start.</summary>
    internal long DeliveredCount => Interlocked.Read(ref delivered);

    /// <summary>How many reads have failed, over every start.</summary>
    internal long ReadFailureCount => Interlocked.Read(ref readFailures);

    // ── publishing ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Appends <paramref name="message"/> to the stream, trimming it to about <see cref="IonRedisBackplaneOptions.MaxLength"/>.</summary>
    /// <remarks>
    /// <paramref name="ct"/> is observed until the entry is handed to the connection; an <c>XADD</c>
    /// already on its way is not taken back. Concurrent calls are appended in call order: they share
    /// one pipelined connection.
    /// </remarks>
    public ValueTask PublishAsync(IonBackplaneMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ct.IsCancellationRequested)
            return ValueTask.FromCanceled(ct);

        RedisValue payload = message.Encode();
        return Volatile.Read(ref publishDatabase) is { } db
            ? new ValueTask(db.StreamAddAsync(streamKey, PayloadField, payload, addOptions))
            : PublishSlowAsync(payload, ct);
    }

    private async ValueTask PublishSlowAsync(RedisValue payload, CancellationToken ct)
    {
        var db = await GetPublisherAsync().WaitAsync(ct).ConfigureAwait(false);
        await db.StreamAddAsync(streamKey, PayloadField, payload, addOptions).ConfigureAwait(false);
    }

    /// <summary>The publishing database, connecting the owned multiplexer on first use (and again after a failed attempt).</summary>
    private Task<IDatabaseAsync> GetPublisherAsync()
    {
        if (Volatile.Read(ref publishDatabase) is { } ready)
            return Task.FromResult(ready);

        lock (publisherGate)
        {
            if (publisherConnecting is null || publisherConnecting.IsFaulted || publisherConnecting.IsCanceled)
                publisherConnecting = ConnectPublisherAsync();
            return publisherConnecting;
        }
    }

    private async Task<IDatabaseAsync> ConnectPublisherAsync()
    {
        var connection = await ConnectionMultiplexer.ConnectAsync(configuration()).ConfigureAwait(false);
        lock (publisherGate)
        {
            if (disposed)
            {
                connection.Dispose();
                throw new ObjectDisposedException(GetType().FullName);
            }

            ownedPublisher = connection;
            var db = connection.GetDatabase();
            Volatile.Write(ref publishDatabase, db);
            return db;
        }
    }

    // ── reading ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Connects the reader, pins the stream's current last id and starts reading after it. Returns
    /// once that id is known: every entry appended from then on is read.
    /// </summary>
    /// <remarks>
    /// Fails — and so fails the host's start — when Redis cannot be reached: a node that silently
    /// misses every other node's pushes is worse than one that does not come up. The publishing
    /// connection is opened here too, so a misconfiguration surfaces now rather than on the first push.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The backplane is already started.</exception>
    public async Task StartAsync(string nodeId, Func<IonBackplaneMessage, ValueTask> handler, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(handler);

        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session is not null)
                throw new InvalidOperationException("The Redis stream backplane is already started; stop it before starting it again");

            await GetPublisherAsync().WaitAsync(ct).ConfigureAwait(false);

            var readerName = ReaderNameOf(nodeId);
            var reader = await ConnectReaderAsync(readerName, ct).ConfigureAwait(false);
            try
            {
                var lastId = await LastIdAsync(reader.GetDatabase()).WaitAsync(ct).ConfigureAwait(false);
                var started = new Session(nodeId, handler, reader, readerName, lastId);
                started.Loop = Task.Run(() => ReadLoopAsync(started), CancellationToken.None);
                Volatile.Write(ref session, started);

                log.LogInformation("Ion stream backplane reading {StreamKey} after {LastId} as node {NodeId}",
                    options.StreamKey, (string?)lastId, nodeId);
            }
            catch
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops reading: the loop is cancelled — a read in flight is abandoned rather than waited on,
    /// a handler call in flight is let finish — and the reader connection is closed. Safe to call
    /// more than once, and concurrently; a second call returns once the first is done.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        Session? stopping;
        try
        {
            stopping = session;
            if (stopping is null)
                return;

            var stopped = stopping.Stop(log);
            Volatile.Write(ref session, null);

            // The stop carries on in the background if ct gives up on it; the lock is not held that long.
            await stopped.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }

        log.LogInformation("Ion stream backplane stopped reading {StreamKey} as node {NodeId}", options.StreamKey, stopping.NodeId);
    }

    /// <summary>Connects a multiplexer that runs nothing but this node's <c>XREAD</c>s.</summary>
    private async Task<ConnectionMultiplexer> ConnectReaderAsync(string name, CancellationToken ct)
    {
        var config = configuration();
        config.ClientName = name;

        // A blocked XREAD is not a slow one: it gets its whole BLOCK on top of the usual allowance
        // before it counts as timed out (and so as a failed read).
        config.AsyncTimeout = (int)blockArgument + config.AsyncTimeout;

        var connecting = ConnectionMultiplexer.ConnectAsync(config);
        try
        {
            return await connecting.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ = connecting.ContinueWith(static t =>
            {
                if (t.IsCompletedSuccessfully)
                    t.Result.Dispose();
                else
                    _ = t.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>The id of the stream's newest entry, or <c>0-0</c> when there is none yet.</summary>
    private async Task<RedisValue> LastIdAsync(IDatabaseAsync db)
    {
        // XREVRANGE rather than XREAD's "$": "$" means "the end when the read reaches the server", and
        // a push made between StartAsync returning and that read would be skipped.
        var newest = await db.ExecuteAsync("XREVRANGE", [keyArgument, "+", "-", "COUNT", 1], CommandFlags.None)
            .ConfigureAwait(false);
        return newest.Length > 0 ? (RedisValue)newest[0][0] : ZeroId;
    }

    private object[] ReadArguments(RedisValue after)
        => ["COUNT", countArgument, "BLOCK", blockArgument, "STREAMS", keyArgument, after];

    private async Task ReadLoopAsync(Session s)
    {
        var ct = s.Stopping.Token;
        var db = s.Reader.GetDatabase();
        var arguments = ReadArguments(s.LastId);
        var failures = 0;
        var delay = options.MinRetryDelay;

        while (!ct.IsCancellationRequested)
        {
            Task<RedisResult>? read = null;
            try
            {
                read = db.ExecuteAsync("XREAD", arguments, CommandFlags.None);
                var result = await read.WaitAsync(ct).ConfigureAwait(false);

                if (failures > 0)
                {
                    log.LogInformation("Ion stream backplane reader for {StreamKey} recovered after {Failures} failed read(s); resuming after {LastId}",
                        options.StreamKey, failures, (string?)s.LastId);
                    failures = 0;
                    delay = options.MinRetryDelay;
                }

                // Only rewritten once the read that used it has completed, so the multiplexer never
                // sees the array change under a command it still holds.
                if (await DeliverAsync(s, result, ct).ConfigureAwait(false))
                    arguments[LastIdArgument] = s.LastId;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Forget(read);
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref readFailures);
                if (++failures == 1)
                    log.LogWarning(ex, "Reading the Ion stream backplane {StreamKey} failed; retrying after {LastId}",
                        options.StreamKey, (string?)s.LastId);
                else
                    log.LogDebug(ex, "Reading the Ion stream backplane {StreamKey} failed again ({Failures} in a row)",
                        options.StreamKey, failures);

                // A timed-out command can linger in the multiplexer's backlog still holding its
                // arguments; the next read gets an array of its own.
                arguments = ReadArguments(s.LastId);

                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                delay = delay * 2 < options.MaxRetryDelay ? delay * 2 : options.MaxRetryDelay;
            }
        }
    }

    /// <summary>Hands one <c>XREAD</c> reply's entries to the handler, in order.</summary>
    /// <returns>True when the reply held entries, so <see cref="Session.LastId"/> moved.</returns>
    /// <exception cref="InvalidCastException">The reply is not shaped like an <c>XREAD</c> reply.</exception>
    private async ValueTask<bool> DeliverAsync(Session s, RedisResult reply, CancellationToken ct)
    {
        if (EntriesOf(reply) is not { } entries)
            return false;

        var moved = false;
        for (var i = 0; i < entries.Length && !ct.IsCancellationRequested; i++)
        {
            var entry = entries[i];
            var id = (RedisValue)entry[0];
            var message = Decode(s, id, entry[1]);

            s.LastId = id;
            moved = true;

            if (message is null)
                continue;

            try
            {
                await s.Handler(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "The Ion stream backplane handler failed on entry {EntryId} ({Command}) from node {Origin}",
                    (string?)id, message.Command, message.OriginNodeId);
            }

            Interlocked.Increment(ref delivered);
        }

        return moved;
    }

    /// <summary>The entries of an <c>XREAD</c> reply for one stream; null when the read timed out empty.</summary>
    private static RedisResult? EntriesOf(RedisResult reply)
    {
        if (reply.IsNull || reply.Length < 1)
            return null;

        // RESP2 answers [[key, entries]]; RESP3 answers a map, which arrives flattened as [key, entries].
        var first = reply[0];
        var entries = first.Length >= 0 ? first[1] : reply[1];
        return entries.IsNull || entries.Length < 1 ? null : entries;
    }

    /// <summary>The entry's message, or null when it is this node's own, from a newer encoding, or not a message at all.</summary>
    private IonBackplaneMessage? Decode(Session s, RedisValue id, RedisResult fields)
    {
        try
        {
            if (PayloadOf(fields) is not { } payload)
            {
                log.LogWarning("Skipping Ion stream backplane entry {EntryId}: it has no '{Field}' field", (string?)id, (string?)PayloadField);
                return null;
            }

            if (IsFrom(payload, s.EncodedNodeId))
                return null;

            var message = IonBackplaneMessage.Decode(payload);
            if (message is null)
            {
                log.LogDebug("Skipping Ion stream backplane entry {EntryId}: it is from a newer encoding", (string?)id);
                return null;
            }

            // The byte comparison only recognizes this process's own encoder; this catches the rest.
            return string.Equals(message.OriginNodeId, s.NodeId, StringComparison.Ordinal) ? null : message;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Skipping Ion stream backplane entry {EntryId}: it is not a backplane message", (string?)id);
            return null;
        }
    }

    private static ReadOnlyMemory<byte>? PayloadOf(RedisResult fields)
    {
        // [field, value, field, value, …]; ours has one pair, but a hand-written entry may have more.
        for (var i = 0; i + 1 < fields.Length; i += 2)
            if ((RedisValue)fields[i] == PayloadField)
                return (ReadOnlyMemory<byte>)(RedisValue)fields[i + 1];
        return null;
    }

    /// <summary>
    /// Whether an encoded message comes from the node whose CBOR-encoded id is <paramref name="encodedNodeId"/>,
    /// read from its first two elements — version, origin — without decoding the rest or allocating the payload.
    /// </summary>
    private static bool IsFrom(ReadOnlyMemory<byte> encoded, byte[] encodedNodeId)
    {
        var reader = new CborReader(encoded);
        if (reader.ReadStartArray() is not > 1)
            return false;

        reader.SkipValue();
        return reader.PeekState() == CborReaderState.TextString
            && reader.ReadEncodedValue().Span.SequenceEqual(encodedNodeId);
    }

    /// <summary>A <c>CLIENT SETNAME</c>-safe name for the reader connection: no spaces or control characters.</summary>
    private static string ReaderNameOf(string nodeId)
    {
        const string prefix = "ion-backplane-reader-";
        return string.Create(prefix.Length + nodeId.Length, nodeId, static (span, id) =>
        {
            prefix.CopyTo(span);
            for (var i = 0; i < id.Length; i++)
                span[prefix.Length + i] = id[i] is > ' ' and <= '~' ? id[i] : '_';
        });
    }

    /// <summary>Observes an abandoned read, which faults once its connection is closed.</summary>
    private static void Forget(Task? task)
    {
        if (task is null)
            return;

        if (task.IsCompleted)
            _ = task.Exception;
        else
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // ── disposal ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Stops reading and closes the connections this backplane opened; a connection it was given stays open.</summary>
    public async ValueTask DisposeAsync()
    {
        Session? stopping;
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;

            disposed = true;
            stopping = session;
            Volatile.Write(ref session, null);
        }
        finally
        {
            lifecycle.Release();
        }

        if (stopping is not null)
            await stopping.Stop(log).ConfigureAwait(false);

        ConnectionMultiplexer? publisher;
        lock (publisherGate)
        {
            publisher = ownedPublisher;
            ownedPublisher = null;
        }

        if (publisher is not null)
            await publisher.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc cref="DisposeAsync"/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>One start-to-stop run: the reader connection, its loop and where it has read up to.</summary>
    private sealed class Session(
        string nodeId,
        Func<IonBackplaneMessage, ValueTask> handler,
        ConnectionMultiplexer reader,
        string readerName,
        RedisValue lastId)
    {
        private readonly Lock gate = new();
        private Task? stopped;

        public string NodeId { get; } = nodeId;

        /// <summary>The node id as CBOR text, to recognize own messages by their bytes.</summary>
        public byte[] EncodedNodeId { get; } = EncodeText(nodeId);

        public Func<IonBackplaneMessage, ValueTask> Handler { get; } = handler;

        public ConnectionMultiplexer Reader { get; } = reader;

        public string ReaderName { get; } = readerName;

        public CancellationTokenSource Stopping { get; } = new();

        public Task Loop { get; set; } = Task.CompletedTask;

        /// <summary>The id of the last entry read; only the loop writes it.</summary>
        public RedisValue LastId { get; set; } = lastId;

        /// <summary>Cancels the loop, waits for it and closes the reader. Every call gets the same task.</summary>
        public Task Stop(ILogger log)
        {
            lock (gate)
                return stopped ??= StopCoreAsync(log);
        }

        private async Task StopCoreAsync(ILogger log)
        {
            await Stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                await Loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "The Ion stream backplane reader loop ended with an error");
            }
            finally
            {
                // Not waiting for commands to complete: the only one left is the abandoned XREAD.
                await Reader.CloseAsync(allowCommandsToComplete: false).ConfigureAwait(false);
                await Reader.DisposeAsync().ConfigureAwait(false);
                Stopping.Dispose();
            }
        }

        private static byte[] EncodeText(string value)
        {
            var writer = new CborWriter();
            writer.WriteTextString(value);
            return writer.Encode();
        }
    }
}
