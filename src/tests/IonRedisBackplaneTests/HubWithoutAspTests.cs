namespace IonRedisBackplaneTests;

using System.Formats.Cbor;
using System.Reflection;
using ion.runtime.network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// The stream hub in a service that does not serve Ion: <c>AddIonStreamHub</c> plus a backplane,
/// with no endpoints, no service descriptors and no web stack.
/// </summary>
[TestFixture]
public sealed class HubWithoutAspTests
{
    [Test]
    public void The_hub_and_the_backplane_need_no_web_stack()
    {
        foreach (var assembly in new[] { typeof(IIonStreamConnections).Assembly, typeof(RedisStreamsBackplane).Assembly })
        {
            var web = assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
                .ToArray();

            Assert.That(web, Is.Empty, $"{assembly.GetName().Name} references {string.Join(", ", web)}");
        }
    }

    [Test]
    public async Task A_push_only_service_reaches_the_other_nodes_through_the_hub()
    {
        var key = Node.NewStreamKey();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddIonStreamHub()
            .AddIonRedisStreamsBackplane(o =>
            {
                o.Configuration = Dragonfly.Configuration;
                o.StreamKey = key;
            });

        using var host = builder.Build();
        await host.StartAsync();

        await using var server = await Node.StartAsync(key);
        var hub = host.Services.GetRequiredService<IIonStreamConnections>();

        Assert.That(hub.Count, Is.Zero, "a push-only service has no connections of its own");

        var queued = await hub.Group("spaces/42").Except("c-1").SendAsync(7);
        Assert.That(queued, Is.Zero, "the count is local, and nothing is local here");
        await hub.User("bob").CloseAsync("session revoked", allowReconnect: false);
        await hub.AddToGroupAsync("remote-connection", "vip");

        await server.WaitForCountAsync(3);
        var received = server.Received;

        Assert.Multiple(() =>
        {
            Assert.That(received[0].Command, Is.EqualTo(IonBackplaneCommand.Send));
            Assert.That(received[0].TargetKind, Is.EqualTo(IonBackplaneTargetKind.Group));
            Assert.That(received[0].Targets, Is.EqualTo(new[] { "spaces/42" }));
            Assert.That(received[0].Excluded, Is.EqualTo(new[] { "c-1" }));
            Assert.That(received[0].PayloadType, Is.EqualTo(typeof(int).AssemblyQualifiedName));
            Assert.That(new CborReader(received[0].Payload).ReadInt32(), Is.EqualTo(7));

            Assert.That(received[1].Command, Is.EqualTo(IonBackplaneCommand.Close));
            Assert.That(received[1].TargetKind, Is.EqualTo(IonBackplaneTargetKind.User));
            Assert.That(received[1].Targets, Is.EqualTo(new[] { "bob" }));
            Assert.That(received[1].Reason, Is.EqualTo("session revoked"));

            Assert.That(received[2].Command, Is.EqualTo(IonBackplaneCommand.AddToGroup));
            Assert.That(received[2].Targets, Is.EqualTo(new[] { "remote-connection" }));
            Assert.That(received[2].Group, Is.EqualTo("vip"));
        });

        await host.StopAsync();
    }

    [Test]
    public void AddIonStreamHub_registers_the_hub_and_nothing_of_the_server()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIonStreamHub(o => o.CloseTimeout = TimeSpan.FromSeconds(1));
        services.AddIonStreamHub();

        using var provider = services.BuildServiceProvider();

        Assert.Multiple(() =>
        {
            Assert.That(provider.GetService<IIonStreamConnections>(), Is.Not.Null);
            Assert.That(services.Count(d => d.ServiceType == typeof(IHostedService)), Is.EqualTo(1),
                "registered once however often it is called");
            Assert.That(services.Any(d => d.ServiceType.Name == "IonDescriptorStorage"), Is.False, "no service descriptors");
            Assert.That(services.Any(d => d.ServiceType.Assembly.GetName().Name == "ion.runtime.network"), Is.False,
                "nothing from the server assembly");
        });
    }
}
