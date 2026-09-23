namespace IonTestClientServer.Streaming.Backplane;

using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;

/// <summary>
/// One Dragonfly container for the cross-node tests in this namespace. Each test isolates itself
/// with a stream key of its own. Without Docker the tests are ignored, not failed.
/// </summary>
[SetUpFixture]
public sealed class DragonflyFixture
{
    public const string Image = "docker.dragonflydb.io/dragonflydb/dragonfly:v2.0.0";

    private static IContainer? container;

    public static string Configuration { get; private set; } = "";

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        await WaitForDockerAsync(TimeSpan.FromMinutes(2));

        container = new ContainerBuilder(Image)
            .WithCommand("--proactor_threads=2")
            .WithPortBinding(6379, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(6379)
                .UntilCommandIsCompleted("redis-cli", "-p", "6379", "PING"))
            .Build();

        await container.StartAsync();
        Configuration = $"{container.Hostname}:{container.GetMappedPublicPort(6379)}";

        // The container answering inside is not the host mapping answering.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                await using var probe = await ConnectionMultiplexer.ConnectAsync(Configuration);
                await probe.GetDatabase().PingAsync();
                break;
            }
            catch (RedisConnectionException) when (DateTime.UtcNow < until)
            {
                await Task.Delay(250);
            }
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (container is not null)
            await container.DisposeAsync();
    }

    private static async Task WaitForDockerAsync(TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                using var docker = new DockerClientBuilder().WithTimeout(TimeSpan.FromSeconds(5)).Build();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await docker.System.PingAsync(timeout.Token);
                return;
            }
            catch (Exception ex) when (DateTime.UtcNow < deadline)
            {
                await TestContext.Progress.WriteLineAsync($"Waiting for Docker: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Assert.Ignore($"Docker is not available, so the cross-node tests cannot run ({ex.Message})");
            }
        }
    }
}
