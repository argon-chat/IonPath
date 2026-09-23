namespace IonRedisBackplaneTests;

using System.Collections.Concurrent;
using System.Globalization;
using ion.runtime.network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>One simulated server node: a <see cref="RedisStreamsBackplane"/> and what it has received.</summary>
internal sealed class Node : IAsyncDisposable
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);

    private readonly List<IonBackplaneMessage> received = [];
    private int inHandler;

    public Node(string streamKey, Action<IonRedisBackplaneOptions>? configure = null, string? id = null)
    {
        Id = id ?? "node-" + Guid.NewGuid().ToString("N")[..8];
        Options = new IonRedisBackplaneOptions
        {
            Configuration = Dragonfly.Configuration,
            StreamKey = streamKey,
            BlockTimeout = TimeSpan.FromMilliseconds(500),
            MinRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(500)
        };
        configure?.Invoke(Options);
        Backplane = new RedisStreamsBackplane(Microsoft.Extensions.Options.Options.Create(Options), Log);
    }

    public string Id { get; }

    public IonRedisBackplaneOptions Options { get; }

    public RedisStreamsBackplane Backplane { get; }

    public TestLog<RedisStreamsBackplane> Log { get; } = new();

    /// <summary>Runs after a message is recorded; a test uses it to throw or to slow the handler down.</summary>
    public Func<IonBackplaneMessage, ValueTask>? AfterReceive { get; set; }

    /// <summary>Set when the handler was ever entered while another call of it was still running.</summary>
    public bool OverlapSeen { get; private set; }

    public IonBackplaneMessage[] Received
    {
        get
        {
            lock (received)
                return received.ToArray();
        }
    }

    public int[] Sequence(string? from = null)
        => Received.Where(m => from is null || m.OriginNodeId == from).Select(SequenceOf).ToArray();

    public static async Task<Node> StartAsync(string streamKey, Action<IonRedisBackplaneOptions>? configure = null)
    {
        var node = new Node(streamKey, configure);
        await node.StartAsync();
        return node;
    }

    public Task StartAsync() => Backplane.StartAsync(Id, HandleAsync, CancellationToken.None);

    public Task StopAsync() => Backplane.StopAsync(CancellationToken.None);

    public ValueTask PublishAsync(int sequence, byte[]? payload = null)
        => Backplane.PublishAsync(Message(Id, sequence, payload));

    /// <summary>Publishes <paramref name="count"/> messages numbered from <paramref name="first"/>, pipelined.</summary>
    public Task PublishManyAsync(int first, int count)
    {
        var sends = new Task[count];
        for (var i = 0; i < count; i++)
            sends[i] = PublishAsync(first + i).AsTask();
        return Task.WhenAll(sends);
    }

    public async Task WaitForCountAsync(int count, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? DefaultWait);
        while (Received.Length < count)
        {
            if (DateTime.UtcNow > until)
                Assert.Fail($"{Id} received {Received.Length} message(s) within {timeout ?? DefaultWait}, expected {count}");
            await Task.Delay(10);
        }
    }

    /// <summary>Waits <paramref name="period"/> and asserts nothing more arrived meanwhile.</summary>
    public async Task AssertQuietAsync(TimeSpan period, int expectedCount)
    {
        await Task.Delay(period);
        Assert.That(Received, Has.Length.EqualTo(expectedCount), $"{Id} received more than expected");
    }

    private async ValueTask HandleAsync(IonBackplaneMessage message)
    {
        if (Interlocked.Increment(ref inHandler) != 1)
            OverlapSeen = true;
        try
        {
            lock (received)
                received.Add(message);

            if (AfterReceive is { } after)
                await after(message);
        }
        finally
        {
            Interlocked.Decrement(ref inHandler);
        }
    }

    public ValueTask DisposeAsync() => Backplane.DisposeAsync();

    public static IonBackplaneMessage Message(string origin, int sequence, byte[]? payload = null) => new()
    {
        OriginNodeId = origin,
        Command = IonBackplaneCommand.Send,
        TargetKind = IonBackplaneTargetKind.Group,
        Targets = ["group-" + sequence.ToString(CultureInfo.InvariantCulture)],
        PayloadType = "test-payload",
        Payload = payload ?? BitConverter.GetBytes(sequence),
        Reason = sequence.ToString(CultureInfo.InvariantCulture)
    };

    public static int SequenceOf(IonBackplaneMessage message) => int.Parse(message.Reason!, CultureInfo.InvariantCulture);

    public static string NewStreamKey() => $"ion:test:{TestContext.CurrentContext.Test.MethodName}:{Guid.NewGuid():N}";

    /// <summary>Polls <paramref name="condition"/> until it holds, failing with <paramref name="what"/> after <paramref name="timeout"/>.</summary>
    public static Task EventuallyAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
        => EventuallyAsync(() => Task.FromResult(condition()), what, timeout);

    /// <inheritdoc cref="EventuallyAsync(Func{bool}, string, TimeSpan?)"/>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? DefaultWait);
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
                Assert.Fail($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }
}

/// <summary>An <see cref="ILogger{T}"/> that keeps what it is told, for asserting on logged failures.</summary>
internal sealed class TestLog<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Text, Exception? Exception)> entries = new();

    public IReadOnlyCollection<(LogLevel Level, string Text, Exception? Exception)> Entries => entries.ToArray();

    public int Count(LogLevel level) => entries.Count(e => e.Level == level);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var text = formatter(state, exception);
        entries.Enqueue((logLevel, text, exception));
        if (logLevel >= LogLevel.Warning)
            TestContext.Progress.WriteLine($"[{logLevel}] {text}{(exception is null ? "" : " — " + exception.GetType().Name + ": " + exception.Message)}");
    }
}
