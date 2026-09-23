namespace ion.runtime.network;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

/// <summary>Registers <see cref="RedisStreamsBackplane"/> as the process's <see cref="IIonStreamBackplane"/>.</summary>
/// <remarks>
/// <para>Pair it with <c>AddIonProtocol</c> on a server, or with <c>AddIonStreamHub</c> alone in a
/// service that only pushes — neither needs ASP.NET Core from this package. Order does not matter:
/// the connection manager looks the backplane up when it is first resolved, after the container is
/// built, and its hosted service starts the backplane with the host and stops it with the host.</para>
///
/// <para>Each call replaces any <see cref="IIonStreamBackplane"/> registered before it, so calling
/// it twice leaves one backplane, configured by both calls' option actions.</para>
/// </remarks>
public static class IonRedisBackplaneExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds a Redis stream backplane that opens its own connections from
        /// <see cref="IonRedisBackplaneOptions.Configuration"/> or
        /// <see cref="IonRedisBackplaneOptions.ConfigurationFactory"/>.
        /// </summary>
        /// <example><code>
        /// builder.Services.AddIonRedisStreamsBackplane(o => o.Configuration = "redis:6379");
        /// </code></example>
        public IServiceCollection AddIonRedisStreamsBackplane(Action<IonRedisBackplaneOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            services.Configure(configure);
            return services.AddBackplane(static sp => new RedisStreamsBackplane(
                sp.GetRequiredService<IOptions<IonRedisBackplaneOptions>>(),
                sp.GetService<ILogger<RedisStreamsBackplane>>()));
        }

        /// <summary>
        /// Adds a Redis stream backplane that publishes through <paramref name="connection"/>, which
        /// it does not dispose, and reads on a dedicated connection of its own.
        /// </summary>
        public IServiceCollection AddIonRedisStreamsBackplane(IConnectionMultiplexer connection,
            Action<IonRedisBackplaneOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(connection);
            return services.AddIonRedisStreamsBackplane(_ => connection, configure);
        }

        /// <summary>
        /// Adds a Redis stream backplane that publishes through the multiplexer
        /// <paramref name="connection"/> returns — typically the application's registered
        /// <see cref="IConnectionMultiplexer"/>, which it does not dispose — and reads on a dedicated
        /// connection of its own.
        /// </summary>
        /// <example><code>
        /// builder.Services.AddIonRedisStreamsBackplane(sp => sp.GetRequiredService&lt;IConnectionMultiplexer&gt;());
        /// </code></example>
        public IServiceCollection AddIonRedisStreamsBackplane(Func<IServiceProvider, IConnectionMultiplexer> connection,
            Action<IonRedisBackplaneOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(connection);

            services.AddOptions<IonRedisBackplaneOptions>();
            if (configure is not null)
                services.Configure(configure);

            return services.AddBackplane(sp => new RedisStreamsBackplane(
                connection(sp),
                sp.GetRequiredService<IOptions<IonRedisBackplaneOptions>>(),
                sp.GetService<ILogger<RedisStreamsBackplane>>()));
        }

        private IServiceCollection AddBackplane(Func<IServiceProvider, RedisStreamsBackplane> factory)
        {
            // Replace, not TryAdd: the latest call wins instead of silently losing to an earlier one.
            services.Replace(ServiceDescriptor.Singleton(factory));
            services.Replace(ServiceDescriptor.Singleton<IIonStreamBackplane>(
                static sp => sp.GetRequiredService<RedisStreamsBackplane>()));
            return services;
        }
    }
}
