namespace ion.runtime.network;

using StackExchange.Redis;

/// <summary>
/// Settings for <see cref="RedisStreamsBackplane"/>. Configure through
/// <c>services.AddIonRedisStreamsBackplane(o =&gt; …)</c> or
/// <c>services.Configure&lt;IonRedisBackplaneOptions&gt;(…)</c>.
/// </summary>
/// <remarks>
/// <para>Every node must use the same server and the same <see cref="StreamKey"/>; nodes on
/// different keys do not see each other, which is how two deployments share one Redis.</para>
///
/// <para>The defaults keep <see cref="RedisStreamsBackplane.StopAsync"/> responsive (it never waits
/// longer than one <see cref="BlockTimeout"/>) and let a node that lost its reader connection for a
/// while catch up on up to <see cref="MaxLength"/> messages instead of dropping them.</para>
/// </remarks>
public sealed class IonRedisBackplaneOptions
{
    /// <summary>The default <see cref="StreamKey"/>.</summary>
    public const string DefaultStreamKey = "ion:streams:backplane";

    /// <summary>
    /// A StackExchange.Redis configuration string, e.g. <c>"redis:6379,password=…"</c>. Ignored
    /// when <see cref="ConfigurationFactory"/> is set.
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// Builds the connection settings. Wins over <see cref="Configuration"/>; use it for what a
    /// string cannot carry — certificate callbacks, token-based auth, a custom reconnect policy.
    /// It is called once per connection the backplane opens, so it must return a fresh instance.
    /// </summary>
    public Func<ConfigurationOptions>? ConfigurationFactory { get; set; }

    /// <summary>
    /// The stream every node appends to and reads from. Also the only key the backplane touches,
    /// so it decides the cluster slot as well.
    /// </summary>
    public string StreamKey { get; set; } = DefaultStreamKey;

    /// <summary>
    /// The approximate number of entries the stream keeps (<c>XADD … MAXLEN ~ n</c>); 0 or less
    /// disables trimming. It bounds memory and, with it, how far behind a reader can fall — through
    /// a reconnect, say — and still catch up without losing messages.
    /// </summary>
    public int MaxLength { get; set; } = 100_000;

    /// <summary>
    /// How long one <c>XREAD</c> waits for new entries before it returns empty and is issued again.
    /// Short, because it is also how long an abandoned read keeps the reader connection busy after a
    /// stop. Must be positive: <c>BLOCK 0</c> would wait forever.
    /// </summary>
    public TimeSpan BlockTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The most entries one <c>XREAD</c> returns (<c>COUNT</c>).</summary>
    public int ReadBatchSize { get; set; } = 256;

    /// <summary>The first pause after a failed read; each further failure in a row doubles it.</summary>
    public TimeSpan MinRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The longest pause between failed reads.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}
