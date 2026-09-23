namespace IonTestClientServer.Streaming;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ion.runtime;
using ion.runtime.client;
using ion.runtime.network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestContracts;

/// <summary>
/// A real Kestrel server — HTTPS, HTTP/1.1 + 2 + 3, WebTransport switched on — serving
/// <see cref="IStreamLab"/> on a loopback port. Real sockets, so an abort is a real abort and a
/// WebTransport session is a real QUIC connection.
/// </summary>
public sealed class LabHost : IAsyncDisposable
{
    private const string WebTransportSwitch = "Microsoft.AspNetCore.Server.Kestrel.Experimental.WebTransportAndH3Datagrams";

    private readonly WebApplication app;

    private LabHost(WebApplication app, int port, bool http3, int? plainPort)
    {
        this.app = app;
        Port = port;
        Http3 = http3;
        PlainPort = plainPort;
    }

    public int Port { get; }

    /// <summary>A plain-HTTP port, when asked for — for clients (Node, Rust tests) that should not have to trust a test certificate.</summary>
    public int? PlainPort { get; }

    public Uri PlainBaseAddress => new($"http://127.0.0.1:{PlainPort ?? throw new InvalidOperationException("Started without a plain-HTTP port.")}");
    public bool Http3 { get; }
    public Uri BaseAddress => new($"https://localhost:{Port}");
    public IServiceProvider Services => app.Services;
    public LabProbe Probe => app.Services.GetRequiredService<LabProbe>();
    public IIonStreamConnections Connections => app.Services.GetRequiredService<IIonStreamConnections>();
    public LabLog Log => app.Services.GetRequiredService<LabLog>();

    /// <summary>Test timings: failures show up in well under a second.</summary>
    public static void FastTimings(IonStreamOptions o)
    {
        o.KeepAliveInterval = TimeSpan.FromMilliseconds(150);
        o.ClientTimeout = TimeSpan.FromMilliseconds(1500);
        o.HandshakeTimeout = TimeSpan.FromSeconds(3);
        o.CloseTimeout = TimeSpan.FromSeconds(2);
        o.StreamStopTimeout = TimeSpan.FromSeconds(2);
    }

    public static async Task<LabHost> StartAsync(
        Action<IonStreamOptions>? streams = null,
        Action<IServiceCollection>? services = null,
        bool http3 = true,
        bool plainHttp = false,
        int? port = null)
    {
        // Process-wide, and read when Kestrel's options are built — so before the builder.
        AppContext.SetSwitch(WebTransportSwitch, true);

        for (var attempt = 0; ; attempt++)
        {
            var chosenPort = port ?? FreePort();
            int? plainPort = plainHttp ? FreePort() : null;
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var log = new LabLog();
            builder.Logging.AddProvider(log);
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            // A rule for this provider outranks appsettings' per-category ones, which would hide Ion's debug lines.
            builder.Logging.AddFilter<LabLog>(null, LogLevel.Debug);
            builder.Services.AddSingleton(log);

            // Kestrel waits this long for HTTP/3 connections a client has not closed yet; every Ion
            // connection is gone well before, so the default 30 s would only stretch the test run.
            builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(5));
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Loopback, chosenPort, listen =>
                {
                    listen.Protocols = http3 ? HttpProtocols.Http1AndHttp2AndHttp3 : HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(TestCertificate.Value);
                });

                if (plainPort is { } plain)
                    k.Listen(IPAddress.Loopback, plain, listen => listen.Protocols = HttpProtocols.Http1);
            });

            builder.Services.AddSingleton<LabProbe>();
            builder.Services.AddScoped<LabScopedMarker>();
            builder.Services.AddIonProtocol(i =>
            {
                i.AddService<IStreamLab, StreamLabImpl>();
                i.IonWithSubProtocolTicketExchange<LabTickets>();
                i.AddStreamLifecycle<LabGlobalHook>();
                i.ConfigureStreams(o =>
                {
                    FastTimings(o);
                    streams?.Invoke(o);
                });
            });
            services?.Invoke(builder.Services);

            var app = builder.Build();
            app.UseWebSockets();
            app.MapRpcEndpoints();

            try
            {
                await app.StartAsync();
                return new LabHost(app, chosenPort, http3, plainPort);
            }
            catch (IOException) when (attempt < 5 && port is null)
            {
                // The port was taken between probing and binding.
                await app.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// A client for this host, as <paramref name="user"/> (the ticket carries the name), on the
    /// given transports in order — WebSocket only by default.
    /// </summary>
    public IonClient Client(string user = "alice", Action<IonStreamClientOptions>? configure = null,
        params IonStreamTransportKind[] transports)
        => ClientVia(BaseAddress, user, configure, transports);

    /// <summary>A client that reaches the server through <paramref name="baseAddress"/> — a <see cref="FaultyProxy"/>, say.</summary>
    public IonClient ClientVia(Uri baseAddress, string user = "alice", Action<IonStreamClientOptions>? configure = null,
        params IonStreamTransportKind[] transports)
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        var http = new HttpClient(handler) { BaseAddress = baseAddress };

        return IonClient.Create(http)
            .WithInterceptor(new UserInterceptor(user))
            .WithStreamOptions(o =>
            {
                o.ServerCertificateValidation = (_, _, _, _) => true;
                o.Transports = transports.Length > 0 ? transports : [IonStreamTransportKind.WebSocket];
                o.KeepAliveInterval = TimeSpan.FromMilliseconds(150);
                o.ServerTimeout = TimeSpan.FromMilliseconds(1500);
                o.HandshakeTimeout = TimeSpan.FromSeconds(3);
                o.CloseTimeout = TimeSpan.FromSeconds(2);
                // Most tests watch one connection end and assert how; reconnecting would hide exactly
                // that. The reconnect and resume tests turn it on (Reconnecting, below).
                o.Reconnect = null;
                configure?.Invoke(o);
            });
    }

    public IStreamLab Lab(string user = "alice", Action<IonStreamClientOptions>? configure = null,
        params IonStreamTransportKind[] transports)
        => Client(user, configure, transports).ForService<IStreamLab>(app.Services);

    public IStreamLab LabVia(Uri baseAddress, string user = "alice", Action<IonStreamClientOptions>? configure = null,
        params IonStreamTransportKind[] transports)
        => ClientVia(baseAddress, user, configure, transports).ForService<IStreamLab>(app.Services);

    /// <summary>
    /// The client defaults — reconnect with resume — at test speed: backoff in tens of milliseconds,
    /// so a test that waits out several attempts still takes well under a second.
    /// </summary>
    public static Action<IonStreamClientOptions> Reconnecting(Action<IonStreamClientOptions>? more = null) => o =>
    {
        o.Reconnect = new IonStreamReconnectPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(50),
            MaxDelay = TimeSpan.FromMilliseconds(400)
        };
        o.Resume = true;
        more?.Invoke(o);
    };

    /// <summary>Stops the host the way a deployment does: ApplicationStopping, then the server.</summary>
    public Task StopAsync() => app.StopAsync();

    public async ValueTask DisposeAsync()
    {
        try { await app.StopAsync(); }
        catch (Exception) { }
        await app.DisposeAsync();
    }

    /// <summary>
    /// A port free for both TCP and UDP (HTTP/3 binds the same number on both). Picked below the
    /// dynamic range: on Windows, Hyper-V and WSL reserve large blocks of it per protocol, so an
    /// ephemeral UDP port is often an excluded TCP one.
    /// </summary>
    internal static int FreePort()
    {
        for (var i = 0; i < 200; i++)
        {
            var port = Random.Shared.Next(20_000, 40_000);
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                var tcp = new TcpListener(IPAddress.Loopback, port);
                tcp.Start();
                tcp.Stop();
                return port;
            }
            catch (SocketException)
            {
            }
        }

        throw new InvalidOperationException("No loopback port is free for both TCP and UDP.");
    }

    private sealed class UserInterceptor(string user) : IIonInterceptor
    {
        public Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
        {
            context.RequestItems[LabTickets.UserHeader] = user;
            return next(context, ct);
        }
    }
}

/// <summary>
/// A self-signed certificate for localhost. Re-imported through PFX because Schannel — which QUIC
/// uses on Windows — cannot use an ephemeral private key.
/// </summary>
public static class TestCertificate
{
    public static readonly X509Certificate2 Value = Create();

    private static X509Certificate2 Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }
}

/// <summary>Collects the server's log lines, so a test can assert on a warning and print them on failure.</summary>
public sealed class LabLog : ILoggerProvider
{
    public readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    public IEnumerable<string> Lines(LogLevel minimum = LogLevel.Warning)
        => Entries.Where(e => e.Level >= minimum).Select(e => $"{e.Level} {e.Category}: {e.Message}");

    private sealed class Logger(LabLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => owner.Entries.Enqueue((logLevel, category,
                formatter(state, exception) + (exception is null ? "" : " | " + exception.GetType().Name + ": " + exception.Message)));
    }
}
