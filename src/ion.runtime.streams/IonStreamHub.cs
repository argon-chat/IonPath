namespace ion.runtime.network;

using System.Collections.Concurrent;
using System.Formats.Cbor;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

/// <summary>
/// What the hub needs from a connection it indexes, beyond the public context: its group set (also
/// the monitor its membership changes run under), the removal flag, and the way in for pushes.
/// Implemented by the server's connections; a push-only service has none.
/// </summary>
internal interface IIonHubConnection : IIonStreamContext
{
    /// <summary>The group names, and the monitor the hub takes for this connection's membership.</summary>
    HashSet<string> GroupSet { get; }

    /// <summary>Set by the hub, under <see cref="GroupSet"/>'s monitor, once the connection has left the registry.</summary>
    bool Removed { get; set; }

    /// <summary>Whether a <paramref name="type"/> (or this <paramref name="item"/>) can go down this stream.</summary>
    bool Accepts(Type type, object? item);

    /// <summary>Queues a pushed DATA frame; false when it was not queued.</summary>
    bool TryEnqueue(byte[] frame);

    /// <summary>Closes the connection because the host is stopping, inviting the client back.</summary>
    void Shutdown();
}

/// <summary>Encodes pushed items as DATA frames with a stream's element formatter.</summary>
internal static class IonStreamPush
{
    private static readonly ConcurrentDictionary<Type, Func<object?, byte[]>> Encoders = new();

    private static readonly MethodInfo EncodeDefinition =
        typeof(IonStreamPush).GetMethod(nameof(EncodeDataFrame), BindingFlags.NonPublic | BindingFlags.Static)!;

    [ThreadStatic] private static CborWriter? writer;

    /// <summary>
    /// Encodes a pushed item as a DATA frame with the stream's element formatter — not the item's
    /// own, which for a union case would write the case without the union envelope.
    /// </summary>
    public static byte[] Encode(Type elementType, object? item)
        => Encoders.GetOrAdd(elementType, static t =>
            EncodeDefinition.MakeGenericMethod(t).CreateDelegate<Func<object?, byte[]>>())(item);

    private static byte[] EncodeDataFrame<TElement>(object? item)
    {
        // One writer per thread, reset per push: a broadcast allocates its frame and nothing else.
        var w = writer ??= new CborWriter();
        w.Reset();
        IonFormatterStorage<TElement>.Write(w, (TElement)item!);
        return IonStreamProtocol.Frame(IonStreamProtocol.OpData, w);
    }
}

public static class IonStreamHubRegistration
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the stream hub — <see cref="IIonStreamConnections"/>, with whatever
        /// <see cref="IIonStreamBackplane"/> is registered — and nothing else: no endpoints, no
        /// service descriptors, no web stack.
        /// </summary>
        /// <remarks>
        /// <para>This is how a service that does not serve Ion pushes to Ion clients: register the hub
        /// and a backplane, and every <see cref="IIonStreamConnections"/> operation reaches the
        /// connections of the servers on the same backplane — the Ion counterpart of an
        /// <c>IHubContext</c> in a process that does not host the hub.</para>
        ///
        /// <code>
        /// services.AddIonStreamHub()
        ///         .AddIonRedisStreamsBackplane(o => o.Configuration = "redis:6379");
        /// // …
        /// await hub.Group($"spaces/{spaceId}").SendAsync&lt;IArgonEvent&gt;(e);
        /// </code>
        ///
        /// <para><c>AddIonProtocol</c> calls this itself; calling both is harmless.</para>
        /// </remarks>
        public IServiceCollection AddIonStreamHub(Action<IonStreamOptions>? configure = null)
        {
            services.AddOptions<IonStreamOptions>();
            if (configure is not null)
                services.Configure(configure);

            services.TryAddSingleton<IonStreamConnectionManager>();
            services.TryAddSingleton<IIonStreamConnections>(sp => sp.GetRequiredService<IonStreamConnectionManager>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, IonStreamHostedService>());
            return services;
        }
    }
}
