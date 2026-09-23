namespace IonRedisBackplaneTests;

using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;

/// <summary>
/// One Dragonfly container for the whole run. Tests isolate themselves by stream key, so sharing
/// it is safe and saves a container start per test.
/// </summary>
/// <remarks>
/// Set <c>ION_TEST_DRAGONFLY_IMAGE</c> to run against another image — a Redis or Valkey one works
/// too, as long as it listens on 6379 and ships <c>redis-cli</c>.
/// </remarks>
[SetUpFixture]
public sealed class Dragonfly
{
    /// <summary>What <c>:latest</c> resolved to when these tests were written, pinned so a release cannot break the run.</summary>
    public const string DefaultImage = "docker.dragonflydb.io/dragonflydb/dragonfly:v2.0.0";

    private const int Port = 6379;

    /// <summary>
    /// Docker Desktop can take a while to come up after a start or an update. Override with
    /// <c>ION_TEST_DOCKER_WAIT_SECONDS</c>, e.g. 0 on a machine known to have no Docker.
    /// </summary>
    private static TimeSpan DockerWait =>
        int.TryParse(Environment.GetEnvironmentVariable("ION_TEST_DOCKER_WAIT_SECONDS"), out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(2);

    private static IContainer? container;
    private static ConnectionMultiplexer? admin;

    /// <summary>A StackExchange.Redis configuration string for the container.</summary>
    public static string Configuration { get; private set; } = "";

    /// <summary>The host the container's port is published on.</summary>
    public static string Host { get; private set; } = "";

    /// <summary>The host port 6379 is published on.</summary>
    public static int MappedPort { get; private set; }

    /// <summary>A connection with admin commands allowed (CLIENT, ACL), for arranging and inspecting.</summary>
    public static ConnectionMultiplexer Admin => admin ?? throw new InvalidOperationException("Dragonfly is not running");

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var image = Environment.GetEnvironmentVariable("ION_TEST_DRAGONFLY_IMAGE") is { Length: > 0 } custom
            ? custom
            : DefaultImage;

        await WaitForDockerAsync();

        var builder = new ContainerBuilder(image);

        // Two threads keep Dragonfly's footprint small; the tests need no more.
        if (image.Contains("dragonfly", StringComparison.OrdinalIgnoreCase))
            builder = builder.WithCommand("--proactor_threads=2");

        try
        {
            container = builder
                .WithPortBinding(Port, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilInternalTcpPortIsAvailable(Port)
                    .UntilCommandIsCompleted("redis-cli", "-p", "6379", "PING"))
                .Build();
        }
        catch (DockerUnavailableException ex)
        {
            // The daemon answered a ping, but Testcontainers resolves its endpoint its own way.
            Assert.Ignore($"Testcontainers cannot find Docker, so the Redis backplane tests cannot run: {ex.Message}");
            return;
        }

        // From here on a failure is a real one — a bad image, a port that never opens — not an absent Docker.
        await container.StartAsync();

        Host = container.Hostname;
        MappedPort = container.GetMappedPublicPort(Port);
        Configuration = $"{Host}:{MappedPort}";

        // The port answering inside the container is not the host mapping answering; PING through it.
        var config = ConfigurationOptions.Parse(Configuration);
        config.AllowAdmin = true;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                admin = await ConnectionMultiplexer.ConnectAsync(config);
                await admin.GetDatabase().PingAsync();
                break;
            }
            catch (RedisConnectionException) when (DateTime.UtcNow < until)
            {
                if (admin is not null)
                    await admin.DisposeAsync();
                admin = null;
                await Task.Delay(250);
            }
        }

        await TestContext.Progress.WriteLineAsync($"Dragonfly ({image}) is up at {Configuration}");
    }

    /// <summary>
    /// Pings the Docker daemon until it answers, and ignores the whole run if it never does.
    /// </summary>
    /// <remarks>
    /// Polled with a client of its own rather than through Testcontainers, which resolves the
    /// daemon once per process and would keep reporting it absent after it has come up.
    /// </remarks>
    private static async Task WaitForDockerAsync()
    {
        var deadline = DateTime.UtcNow + DockerWait;
        while (true)
        {
            string problem;
            try
            {
                using var docker = new DockerClientBuilder().WithTimeout(TimeSpan.FromSeconds(5)).Build();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await docker.System.PingAsync(timeout.Token);
                return;
            }
            catch (Exception ex)
            {
                var cause = ex is AggregateException { InnerException: { } inner } ? inner : ex;
                problem = $"{cause.GetType().Name}: {cause.Message}";
            }

            if (DateTime.UtcNow >= deadline)
                Assert.Ignore($"Docker is not available, so the Redis backplane tests cannot run ({problem})");

            await TestContext.Progress.WriteLineAsync($"Waiting for Docker: {problem}");
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (admin is not null)
            await admin.DisposeAsync();
        if (container is not null)
            await container.DisposeAsync();
    }

    /// <summary>The <c>CLIENT LIST</c> ids of every connection named <paramref name="clientName"/>.</summary>
    public static async Task<List<string>> ClientIdsAsync(string clientName)
    {
        var list = (string?)await Admin.GetDatabase().ExecuteAsync("CLIENT", "LIST") ?? "";

        // RESP3 delivers CLIENT LIST as a verbatim string, prefixed with its format.
        if (list.StartsWith("txt:", StringComparison.Ordinal))
            list = list[4..];

        var ids = new List<string>();
        foreach (var line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? id = null, name = null;
            foreach (var field in line.Split(' '))
            {
                if (field.StartsWith("id=", StringComparison.Ordinal)) id = field[3..];
                else if (field.StartsWith("name=", StringComparison.Ordinal)) name = field[5..];
            }

            if (id is not null && name == clientName)
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>Kills every connection named <paramref name="clientName"/>; returns how many there were.</summary>
    public static async Task<int> KillClientsAsync(string clientName)
    {
        var ids = await ClientIdsAsync(clientName);
        foreach (var id in ids)
            await Admin.GetDatabase().ExecuteAsync("CLIENT", "KILL", "ID", id);
        return ids.Count;
    }
}
