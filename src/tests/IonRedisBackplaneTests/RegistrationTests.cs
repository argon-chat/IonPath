namespace IonRedisBackplaneTests;

using System.Formats.Cbor;
using ion.runtime.network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

/// <summary><c>AddIonRedisStreamsBackplane</c> and how the backplane plugs into the Ion host.</summary>
[TestFixture]
public sealed class RegistrationTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task The_host_runs_the_backplane_whichever_side_of_AddIonProtocol_it_is_registered(bool backplaneFirst)
    {
        var key = Node.NewStreamKey();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();

        if (backplaneFirst)
            AddBackplane(builder.Services, key);
        builder.Services.AddIonProtocol(_ => { });
        if (!backplaneFirst)
            AddBackplane(builder.Services, key);

        // A second call replaces the first rather than adding another backplane.
        AddBackplane(builder.Services, key);

        using var host = builder.Build();
        var backplane = host.Services.GetRequiredService<IIonStreamBackplane>();
        Assert.Multiple(() =>
        {
            Assert.That(backplane, Is.SameAs(host.Services.GetRequiredService<RedisStreamsBackplane>()));
            Assert.That(host.Services.GetServices<IIonStreamBackplane>().Count(), Is.EqualTo(1));
        });

        var redis = (RedisStreamsBackplane)backplane;
        await host.StartAsync();
        Assert.That(redis.IsRunning, "the Ion hosted service did not start the backplane");

        // Another node's push reaches the connection manager's handler.
        await using (var other = new Node(key))
        {
            var number = new CborWriter();
            number.WriteInt32(42);
            await other.Backplane.PublishAsync(new IonBackplaneMessage
            {
                OriginNodeId = other.Id,
                Command = IonBackplaneCommand.Send,
                TargetKind = IonBackplaneTargetKind.All,
                PayloadType = typeof(int).AssemblyQualifiedName,
                Payload = number.Encode()
            });
            await Node.EventuallyAsync(() => redis.DeliveredCount == 1, "the manager's handler to be called");
        }

        await host.StopAsync();
        Assert.That(redis.IsRunning, Is.False, "the Ion hosted service did not stop the backplane");
    }

    [Test]
    public async Task A_shared_connection_publishes_seeds_the_reader_and_is_left_open()
    {
        var key = Node.NewStreamKey();
        await using var shared = await ConnectionMultiplexer.ConnectAsync(Dragonfly.Configuration);
        await using var listener = await Node.StartAsync(key);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConnectionMultiplexer>(shared);
        services.AddIonRedisStreamsBackplane(sp => sp.GetRequiredService<IConnectionMultiplexer>(), o =>
        {
            o.StreamKey = key;
            o.BlockTimeout = TimeSpan.FromMilliseconds(500);
        });

        await using (var provider = services.BuildServiceProvider())
        {
            var backplane = (RedisStreamsBackplane)provider.GetRequiredService<IIonStreamBackplane>();

            await backplane.PublishAsync(Node.Message("shared-node", 1));
            await listener.WaitForCountAsync(1);

            // No Configuration given: the reader connects with the shared connection's.
            await backplane.StartAsync("shared-node", _ => default, CancellationToken.None);
            await listener.PublishAsync(2);
            await Node.EventuallyAsync(() => backplane.DeliveredCount == 1, "the reader to get the listener's message");
        }

        Assert.That(shared.IsConnected, "disposing the backplane closed the application's connection");
        await shared.GetDatabase().PingAsync();
    }

    [Test]
    public void Options_are_validated_up_front()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidOperationException>(() => _ = new RedisStreamsBackplane(Options.Create(new IonRedisBackplaneOptions())),
                "no configuration at all");
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new RedisStreamsBackplane(Options.Create(
                new IonRedisBackplaneOptions { Configuration = "localhost", BlockTimeout = TimeSpan.Zero })), "BLOCK 0 waits forever");
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = new RedisStreamsBackplane(Options.Create(
                new IonRedisBackplaneOptions { Configuration = "localhost", ReadBatchSize = 0 })));
            Assert.Throws<ArgumentException>(() => _ = new RedisStreamsBackplane(Options.Create(
                new IonRedisBackplaneOptions { Configuration = "localhost", StreamKey = " " })));
        });
    }

    private static void AddBackplane(IServiceCollection services, string key)
        => services.AddIonRedisStreamsBackplane(o =>
        {
            o.Configuration = Dragonfly.Configuration;
            o.StreamKey = key;
            o.BlockTimeout = TimeSpan.FromMilliseconds(500);
        });
}
