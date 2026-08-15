namespace IonTestClientServer;

using ion.runtime;
using ion.runtime.network;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestContracts;

/// <summary>
/// Server-side implementation of <see cref="ICollectionInteraction"/>, plus the test host that
/// registers it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives here and not in <c>src/tests/TestServer/Program.cs</c>.</b> Every other
/// contract in this solution is implemented there and wired with
/// <c>builder.Services.AddIonProtocol(i =&gt; i.AddService&lt;IFoo, FooImpl&gt;())</c>, and this one
/// should join them — see the codegen report. Until it does, the registration is layered on from
/// the test side: <see cref="IonCollectionFactory"/> re-enters the same
/// <c>AddIonService&lt;TInterface, TImpl&gt;</c> extension that <c>AddIonProtocol</c> calls
/// internally, which is all the endpoint needs (it dispatches off
/// <c>IonTransportOptions.Services</c> and the generated
/// <c>IonExecutorMetadataStorage</c> entry, both of which are already in place). Nothing about the
/// generated client, executor or formatters is bypassed.
/// </para>
/// <para>
/// Most methods echo their argument. That is deliberate: the assertions are about the <em>wire</em>
/// — canonical ordering, tag 258, exact length — so a transform in the middle would only add a
/// second thing that could be wrong. <see cref="Rotate"/> is the exception, because a pure echo
/// cannot tell "the server decoded and re-encoded it" apart from "the server sent the request bytes
/// straight back".
/// </para>
/// </remarks>
public sealed class CollectionImpl : ICollectionInteraction
{
    public Task<Dictionary<string, int>> CountByTag(Dictionary<string, int> tags,
        CancellationToken ct = default)
        => Task.FromResult(tags);

    public Task<HashSet<int>> Dedup(HashSet<int> ids, CancellationToken ct = default)
        => Task.FromResult(ids);

    /// <summary>Rotates left by one, so the response is provably a decode-then-re-encode.</summary>
    public Task<IonArray<float>> Rotate(IonArray<float> coords, CancellationToken ct = default)
        => Task.FromResult(new IonArray<float>(
            coords.Values.Skip(1).Concat(coords.Values.Take(1)).ToList()));

    public Task<IonArray<float>?> RotateMaybe(IonArray<float>? coords, CancellationToken ct = default)
        => Task.FromResult(coords is null
            ? (IonArray<float>?)null
            : new IonArray<float>(coords.Value.Values.Skip(1).Concat(coords.Value.Values.Take(1)).ToList()));

    public Task<Dictionary<string, Member>?> Lookup(Dictionary<string, Member>? members,
        CancellationToken ct = default)
        => Task.FromResult(members);

    public Task<IonArray<HashSet<int>>> Regroup(IonArray<HashSet<int>> groups,
        CancellationToken ct = default)
        => Task.FromResult(groups);

    public Task<Dictionary<string, IonPartial<Doc>>> Patch(Dictionary<string, IonPartial<Doc>> patches,
        CancellationToken ct = default)
        => Task.FromResult(patches);

    public Task<Dictionary<string, IonArray<Member>>> Roster(Dictionary<string, IonArray<Member>> rosters,
        CancellationToken ct = default)
        => Task.FromResult(rosters);

    /// <summary>
    /// The two-argument method. A write path that emitted nothing for the container would leave the
    /// executor reading <c>weight</c> out of the map's bytes, so a wrong <c>weight</c> here is the
    /// symptom of a desynchronised argument array.
    /// </summary>
    public Task<Dictionary<string, int>> Merge(Dictionary<string, int> baseline, int weight,
        CancellationToken ct = default)
        => Task.FromResult(baseline.ToDictionary(x => x.Key, x => x.Value * weight));

    public Task<ContainerShapes> Echo(ContainerShapes shapes, CancellationToken ct = default)
        => Task.FromResult(shapes);

    public Task<KeyMatrix> EchoKeys(KeyMatrix keys, CancellationToken ct = default)
        => Task.FromResult(keys);
}

/// <summary>
/// <see cref="IonTestFactoryAsp"/> with <see cref="ICollectionInteraction"/> added.
/// </summary>
internal sealed class IonCollectionFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        // Runs after Program.cs's own AddIonProtocol, so IonTransportOptions already exists and
        // this only appends one entry to it.
        => builder.ConfigureServices(services =>
            services.AddIonService<ICollectionInteraction, CollectionImpl>());
}

/// <summary>
/// Captures the exact bytes the generated client puts on the wire, and the bytes it gets back.
/// </summary>
/// <remarks>
/// A <see cref="DelegatingHandler"/> rather than an <c>IIonInterceptor</c>: an interceptor is
/// handed <c>IIonCallContext</c>, which deliberately exposes no payload. This sits below the
/// generated client and above the test server, so what it records is literally what was
/// transmitted — which is what an "identical bytes" claim has to be about.
/// </remarks>
internal sealed class WireRecorder : DelegatingHandler
{
    public byte[]? LastRequest { get; private set; }
    public byte[]? LastResponse { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
            LastRequest = await request.Content.ReadAsByteArrayAsync(cancellationToken);

        var response = await base.SendAsync(request, cancellationToken);

        LastResponse = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        // The generated client reads the body itself; hand it a fresh, unconsumed one.
        response.Content = new ByteArrayContent(LastResponse);
        return response;
    }

    public string RequestHex => Convert.ToHexString(LastRequest ?? []).ToLowerInvariant();
    public string ResponseHex => Convert.ToHexString(LastResponse ?? []).ToLowerInvariant();
}
