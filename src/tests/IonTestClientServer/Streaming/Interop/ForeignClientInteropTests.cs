namespace IonTestClientServer.Streaming.Interop;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ion.runtime;
using TestContracts;

/// <summary>
/// The JavaScript and Rust clients against the real server — not against fakes of it.
/// </summary>
/// <remarks>
/// <para>Each test starts a server, then runs the other language's interop suite
/// (<c>packages/ion.webcore.js/test/interop</c>, <c>src/tests/RustInterop</c>) against it with
/// <c>ION_INTEROP_URL</c> and <c>ION_INTEROP_RUN</c>. The foreign suite asserts what its client
/// saw; this side plays the server's part in the scenarios that need one (a kick, pushes, a long
/// silence) while it runs, and afterwards asserts what the server saw.</para>
///
/// <para>The contract, per scenario — the user name travels in the <c>x-lab-user</c> header of the
/// ticket exchange, set by an interceptor, so it also proves interceptors reach the exchange:</para>
/// <list type="table">
///   <item><term>{run}-complete</term><description>Count(10, 50, 0): exactly 10..59, then a normal end. Server: Completed.</description></item>
///   <item><term>{run}-break</term><description>Count(0, 1000000, 5): three items, then the client stops early. Server: ClientClosed.</description></item>
///   <item><term>{run}-abort</term><description>Count(0, 1000000, 5): three items, then cancelled (AbortSignal / explicit close). Server: ClientClosed.</description></item>
///   <item><term>{run}-error</term><description>Explode(2): items 0 and 1, then an error with code INTERNAL_ERROR. Server: Faulted.</description></item>
///   <item><term>banned-{run}</term><description>Count(0, 1, 0): refused with code BANNED, no retry. Server: Rejected.</description></item>
///   <item><term>{run}-kicked</term><description>Listen("kick-{run}"): the server closes it with reason "kicked", allowReconnect false; no reconnect. Server: ServerClosed.</description></item>
///   <item><term>{run}-push</term><description>Listen("push-{run}"): the server pushes LabEvent(1..3, "push-{run}", "p1".."p3"); the client stops after the third. Server: ClientClosed.</description></item>
///   <item><term>{run}-union</term><description>Signals("{run}"): the server pushes Joined("ann"), Said("ann","hi"), Left("ann"); the client stops after the third. Server: ClientClosed.</description></item>
///   <item><term>{run}-echo</term><description>Echo("a".."j"): "A".."J" in order, then a normal end when the input ends. Server: Completed.</description></item>
///   <item><term>{run}-blobs</term><description>Blobs(1048579, 3): three payloads, byte i of payload n = (n + i) mod 251. Server: Completed.</description></item>
///   <item><term>{run}-idle</term><description>Listen("idle-{run}"): nothing for 4 s — past the 1.5 s client timeout, so only heartbeats keep it up — then the server pushes body "still-here"; the client stops after it. Server: ClientClosed, never timed out.</description></item>
///   <item><term>{run}-resume</term><description>JavaScript only, through <c>ION_INTEROP_PROXY_URL</c>: Listen("resume-{run}"): the server pushes r1, r2, resets the connection, pushes r3, waits for the session to be resumed, pushes r4; the client stops after r4, having seen r1..r4 once each and resumed. Server: one connection, resumed at least once, ClientClosed.</description></item>
/// </list>
/// <para>The server announces a 150 ms keep-alive and a 1.5 s client timeout in READY; a client
/// that does not adapt its heartbeat to them is dropped during the idle scenario.</para>
/// </remarks>
[TestFixture]
[Category("Interop")]
[Parallelizable(ParallelScope.None)]
public class ForeignClientInteropTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Test]
    public Task The_JavaScript_client_against_the_real_server()
        => RunAsync("js", "node", ["node_modules/vitest/vitest.mjs", "--run", "test/interop"], Path.Combine("packages", "ion.webcore.js"));

    [Test]
    public Task The_Rust_client_against_the_real_server()
        => RunAsync("rs", "cargo", ["test", "--quiet"], Path.Combine("src", "tests", "RustInterop"));

    private static async Task RunAsync(string language, string tool, string[] args, string relativeDirectory)
    {
        var directory = Path.Combine(RepositoryRoot(), relativeDirectory);
        if (!Directory.Exists(directory))
            Assert.Ignore($"{directory} does not exist");
        if (!await ToolExistsAsync(tool))
            Assert.Ignore($"'{tool}' is not on PATH, so the {language} interop suite cannot run");

        await using var host = await LabHost.StartAsync(plainHttp: true);
        await using var proxy = FaultyProxy.Start(host.PlainPort!.Value);
        var run = language + Guid.NewGuid().ToString("N")[..8];

        using var stop = new CancellationTokenSource();
        var problems = new ConcurrentQueue<string>();
        var choreography = Task.Run(() => PlayTheServerAsync(host, proxy, run, problems, stop.Token));

        var (exitCode, output) = await RunProcessAsync(tool, args, directory, new Dictionary<string, string>
        {
            ["ION_INTEROP_URL"] = host.PlainBaseAddress.ToString().TrimEnd('/'),
            ["ION_INTEROP_PROXY_URL"] = $"http://127.0.0.1:{proxy.Port}",
            ["ION_INTEROP_RUN"] = run
        }, TimeSpan.FromMinutes(10));

        await stop.CancelAsync();
        try { await choreography; } catch (OperationCanceledException) { }

        if (exitCode != 0)
            Assert.Fail($"The {language} interop suite failed (exit {exitCode}):{Environment.NewLine}{Tail(output, 12_000)}");

        TestContext.Out.WriteLine(Tail(output, 4_000));
        Assert.That(problems, Is.Empty, "server-side choreography: " + string.Join("; ", problems));

        await AssertTheServerSawAsync(host, run, resumes: language == "js");
    }

    /// <summary>The server's half of the scenarios that need one, played as the connections arrive.</summary>
    private static async Task PlayTheServerAsync(LabHost host, FaultyProxy proxy, string run, ConcurrentQueue<string> problems, CancellationToken ct)
    {
        var handled = new HashSet<string>();
        var pending = new List<Task>();

        while (!ct.IsCancellationRequested)
        {
            foreach (var c in host.Probe.All)
            {
                if (c.User is null || !c.StreamStarted.Task.IsCompleted || !handled.Add(c.ConnectionId))
                    continue;

                if (c.User == $"{run}-kicked")
                {
                    pending.Add(host.Connections.Client(c.ConnectionId).CloseAsync("kicked", allowReconnect: false));
                }
                else if (c.User == $"{run}-push")
                {
                    for (var i = 1; i <= 3; i++)
                        await host.Connections.Group($"push-{run}").SendAsync(new LabEvent(i, $"push-{run}", $"p{i}"), ct);
                }
                else if (c.User == $"{run}-union")
                {
                    var room = host.Connections.Group($"room:{run}");
                    await room.SendAsync<ILabSignal>(new Joined("ann"), ct);
                    await room.SendAsync<ILabSignal>(new Said("ann", "hi"), ct);
                    await room.SendAsync<ILabSignal>(new Left("ann"), ct);
                }
                else if (c.User == $"{run}-resume")
                {
                    var connection = c;
                    pending.Add(Task.Run(async () =>
                    {
                        var topic = host.Connections.Group($"resume-{run}");
                        await topic.SendAsync(new LabEvent(1, $"resume-{run}", "r1"), ct);
                        await topic.SendAsync(new LabEvent(2, $"resume-{run}", "r2"), ct);
                        await Task.Delay(300, ct);

                        proxy.Reset();
                        await topic.SendAsync(new LabEvent(3, $"resume-{run}", "r3"), ct);

                        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                        while (ResumesOf(host, connection) == 0)
                        {
                            if (DateTime.UtcNow > deadline)
                            {
                                problems.Enqueue($"resume: the session was not resumed ({connection.Context?.State}, {connection.Context?.Disconnect})");
                                return;
                            }

                            await Task.Delay(20, ct);
                        }

                        await topic.SendAsync(new LabEvent(4, $"resume-{run}", "r4"), ct);
                    }, ct));
                }
                else if (c.User == $"{run}-idle")
                {
                    var connection = c;
                    pending.Add(Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(4), ct);
                        if (connection.Disconnected.Task.IsCompleted)
                        {
                            problems.Enqueue($"idle: the connection ended during the silence ({await connection.Disconnected.Task})");
                            return;
                        }

                        await host.Connections.Client(connection.ConnectionId)
                            .SendAsync(new LabEvent(1, $"idle-{run}", "still-here"), ct);
                    }, ct));
                }
            }

            await Task.Delay(20, ct);
        }

        foreach (var task in pending)
            try { await task; } catch (OperationCanceledException) { }
    }

    private static int ResumesOf(LabHost host, LabConnection c)
        => host.Log.Lines(Microsoft.Extensions.Logging.LogLevel.Information)
            .Count(l => l.Contains($"Stream connection {c.ConnectionId} resumed", StringComparison.Ordinal));

    private static async Task AssertTheServerSawAsync(LabHost host, string run, bool resumes)
    {
        await Eventually.TrueAsync(() => host.Connections.Count == 0, Wait, $"every connection ended ({host.Connections.Count} left)");

        if (resumes)
        {
            var resumed = host.Probe.All.Where(c => c.User == $"{run}-resume").ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(resumed, Has.Length.EqualTo(1), "resume: one connection, however many transports");
                if (resumed.Length == 1)
                {
                    Assert.That(ResumesOf(host, resumed[0]), Is.GreaterThanOrEqualTo(1), "resume: resumed at least once");
                    Assert.That(resumed[0].Context?.Disconnect?.Reason, Is.EqualTo(IonDisconnectReason.ClientClosed), $"resume: {resumed[0].Context?.Disconnect}");
                    Assert.That(resumed[0].DisconnectCalls, Is.EqualTo(1), "resume: one disconnect, at the very end");
                }
            });
        }

        var expected = new (string User, IonDisconnectReason Reason)[]
        {
            ($"{run}-complete", IonDisconnectReason.Completed),
            ($"{run}-break", IonDisconnectReason.ClientClosed),
            ($"{run}-abort", IonDisconnectReason.ClientClosed),
            ($"{run}-error", IonDisconnectReason.Faulted),
            ($"banned-{run}", IonDisconnectReason.Rejected),
            ($"{run}-kicked", IonDisconnectReason.ServerClosed),
            ($"{run}-push", IonDisconnectReason.ClientClosed),
            ($"{run}-union", IonDisconnectReason.ClientClosed),
            ($"{run}-echo", IonDisconnectReason.Completed),
            ($"{run}-blobs", IonDisconnectReason.Completed),
            ($"{run}-idle", IonDisconnectReason.ClientClosed)
        };

        Assert.Multiple(() =>
        {
            foreach (var (user, reason) in expected)
            {
                var connections = host.Probe.All.Where(c => c.User == user).ToArray();
                Assert.That(connections, Has.Length.EqualTo(1), $"{user}: exactly one connection — no retry, no reconnect");
                if (connections.Length != 1)
                    continue;

                var c = connections[0];
                Assert.That(c.Context?.Disconnect?.Reason, Is.EqualTo(reason), $"{user}: {c.Context?.Disconnect}");
                Assert.That(c.GlobalDisconnectCalls, Is.EqualTo(1), $"{user}: global disconnect hook calls");
                Assert.That(c.DisconnectCalls, Is.EqualTo(reason == IonDisconnectReason.Rejected ? 0 : 1), $"{user}: service disconnect hook calls");
            }
        });
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "IonPath.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("IonPath.slnx not found above the test directory");
    }

    private static async Task<bool> ToolExistsAsync(string tool)
    {
        try
        {
            var (code, _) = await RunProcessAsync(tool, ["--version"], Environment.CurrentDirectory, new Dictionary<string, string>(), TimeSpan.FromSeconds(30));
            return code == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string tool, string[] args, string directory, IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(tool)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        foreach (var (key, value) in environment)
            info.Environment[key] = value;

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {tool}");
        var output = new StringBuilder();
        var gate = new object();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (gate) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            lock (gate) output.AppendLine($"[killed after {timeout}]");
            return (-1, output.ToString());
        }

        lock (gate) return (process.ExitCode, output.ToString());
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];
}
