namespace IonTestClientServer;

using ion.runtime;
using ion.runtime.client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Formats.Cbor;
using System.Net.WebSockets;
using TestContracts;

/// <summary>
/// The golden vectors, run through the <b>generated</b> <c>Partial&lt;T&gt;</c> path.
/// </summary>
/// <remarks>
/// <para>
/// <c>TestTypes</c> covers the runtime with a hand written schema. This covers codegen:
/// <c>msg PatchTarget</c> in <c>Contracts/PartialInteraction.ion</c> is declared with the same
/// shape as <c>GoldenPatchTarget</c> in <c>/tests/golden/partial.golden.json</c>, so the bytes
/// the generated record + generated <c>IonPartialSchema&lt;PatchTarget&gt;.Register(...)</c>
/// produce must equal the cross-runtime vectors exactly — including the field ordering, which
/// comes from the emitted schema rather than from the order fields were patched in.
/// </para>
/// <para>
/// Nothing here registers a schema: the generated <c>[ModuleInitializer]</c> in
/// <c>models/moduleInit.cs</c> does, which is what <see cref="Generated_SchemaIsRegistered"/>
/// asserts. The wire names are the Ion field names, which are also the CLR property names —
/// the <c>SetField(x =&gt; x.f)</c> selectors below only compile if the two agree.
/// </para>
/// </remarks>
public class GeneratedPartialTests
{
    [Test]
    public void Generated_SchemaIsRegistered()
    {
        Assert.That(IonPartialSchema<PatchTarget>.IsRegistered, Is.True,
            "codegen must register the schema; falling back to ReflectionPartialSchema is AOT/trim " +
            "hostile and only best-effort about field order");

        Assert.That(IonPartialSchema<PatchTarget>.Fields.Select(f => f.Name),
            Is.EqualTo(new[] { "n", "f", "s", "items", "note" }),
            "schema order is wire order, so it must be Ion declaration order");
    }

    /// <summary>
    /// The headline bug: clearing a value-typed field must encode as <c>null</c> (0xF6), not as
    /// the field's default. Asserted on the raw bytes, not on a round-tripped state.
    /// </summary>
    [Test]
    public void Generated_ClearedValueTypeEncodesAsNull()
    {
        var patch = new IonPartial<PatchTarget>();
        patch.SetField(x => x.f, PartialField<float>.Removed());

        Assert.That(Hex(patch), Is.EqualTo("a16166f6"));
        Assert.That(Hex(patch), Does.Not.Contain("f90000").And.Not.Contain("fa00000000"),
            "'cleared' must not degrade into 'set to zero'");
    }

    [Test]
    [TestCaseSource(nameof(EncodeVectorNames))]
    public void Generated_Golden_Encode(string name)
    {
        var vector = GoldenVectors.Get(name);

        Assert.That(Hex(BuildPatch(name)), Is.EqualTo(vector.Hex),
            $"golden vector '{name}': {vector.Notes}");
    }

    [Test]
    [TestCaseSource(nameof(DecodeVectorNames))]
    public void Generated_Golden_Decode(string name)
    {
        var vector = GoldenVectors.Get(name);

        var decoded = IonFormatterStorage<IonPartial<PatchTarget>>
            .Read(new CborReader(Convert.FromHexString(vector.Hex)));

        Assert.That(Hex(decoded), Is.EqualTo(vector.ReencodedHex ?? vector.Hex),
            $"golden vector '{name}': {vector.Notes}");
    }

    /// <summary>The generated array/optional field shapes survive a full message round-trip.</summary>
    [Test]
    public void Generated_EnvelopeRoundTrips()
    {
        var one = new IonPartial<PatchTarget>();
        one.SetField(x => x.n, PartialField<int>.Modified(7));

        var cleared = new IonPartial<PatchTarget>();
        cleared.SetField(x => x.items, PartialField<IonArray<int>>.Removed());

        var envelope = new PatchEnvelope(
            one,
            new IonArray<IonPartial<PatchTarget>>([one, cleared]),
            null,
            null);

        var writer = new CborWriter();
        IonFormatterStorage<PatchEnvelope>.Write(writer, envelope);

        var decoded = IonFormatterStorage<PatchEnvelope>.Read(new CborReader(writer.Encode()));

        Assert.Multiple(() =>
        {
            Assert.That(decoded.one.StateOf("n"), Is.EqualTo(PartialState.Modified));
            Assert.That(decoded.one.GetField(x => x.n).Value, Is.EqualTo(7));
            Assert.That(decoded.many.Size, Is.EqualTo(2));
            Assert.That(decoded.many[1].StateOf("items"), Is.EqualTo(PartialState.Removed));
            Assert.That(decoded.maybe, Is.Null);
            Assert.That(decoded.maybeMany, Is.Null);
        });
    }

    // ── over a real transport ────────────────────────────────────────────────
    //
    // The generated client declares `writer.WriteStartArray(argsSize)` from the *declared*
    // argument count. When the partial write path emitted nothing, a call with a `T~` argument
    // promised N elements and wrote N-1: malformed CBOR the executor could not read. ApplyTo
    // is the two-argument case that pins that down.

    [Test]
    public async Task Rpc_PatchArgumentAndReturn()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IPatchInteraction>(scope);

        var patch = new IonPartial<PatchTarget>();
        patch.SetField(x => x.n, PartialField<int>.Modified(7));
        patch.SetField(x => x.f, PartialField<float>.Removed());

        var echoed = await service.Apply(patch);

        Assert.Multiple(() =>
        {
            Assert.That(echoed.GetField(x => x.n).Value, Is.EqualTo(7));
            Assert.That(echoed.StateOf("f"), Is.EqualTo(PartialState.Removed));
            Assert.That(echoed.StateOf("s"), Is.EqualTo(PartialState.None));
        });
    }

    [Test]
    public async Task Rpc_PatchAlongsideAnotherArgument()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IPatchInteraction>(scope);

        var target = new PatchTarget(1, 2f, "before", new IonArray<int>([9]), "note");

        var patch = new IonPartial<PatchTarget>();
        patch.SetField(x => x.s, PartialField<string?>.Modified("after"));
        patch.SetField(x => x.note, PartialField<string?>.Removed());

        var applied = await service.ApplyTo(target, patch);

        Assert.Multiple(() =>
        {
            Assert.That(applied.s, Is.EqualTo("after"));
            Assert.That(applied.note, Is.Null);
            Assert.That(applied.n, Is.EqualTo(1), "untouched fields must survive");
            Assert.That(applied.items.Values, Is.EqualTo(new[] { 9 }));
        });
    }

    [Test]
    public async Task Rpc_PatchArrayInOptionalOut()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IPatchInteraction>(scope);

        var first = new IonPartial<PatchTarget>();
        first.SetField(x => x.n, PartialField<int>.Modified(1));

        var second = new IonPartial<PatchTarget>();
        second.SetField(x => x.items, PartialField<IonArray<int>>.Removed());

        var last = await service.ApplyMany(new IonArray<IonPartial<PatchTarget>>([first, second]));

        Assert.That(last, Is.Not.Null);
        Assert.That(last!.StateOf("items"), Is.EqualTo(PartialState.Removed));

        Assert.That(await service.ApplyMany(new IonArray<IonPartial<PatchTarget>>([])), Is.Null);
    }

    private IonTestFactoryAsp _factory = null!;
    private HttpClient _httpClient = null!;

    [SetUp]
    public void Setup()
    {
        _factory = new IonTestFactoryAsp();
        _httpClient = _factory.CreateClient();
    }

    [TearDown]
    public void Teardown()
    {
        _httpClient.Dispose();
        _factory.Dispose();
    }

    private Task<WebSocket> WsFactory(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = _factory.Server.CreateWebSocketClient();
        foreach (var protocol in protocols ?? [])
            socket.SubProtocols.Add(protocol);
        return socket.ConnectAsync(uri, ct);
    }

    private static string Hex(IonPartial<PatchTarget> patch)
    {
        var writer = new CborWriter();
        IonFormatterStorage<IonPartial<PatchTarget>>.Write(writer, patch);
        return Convert.ToHexString(writer.Encode()).ToLowerInvariant();
    }

    private static IEnumerable<string> EncodeVectorNames() =>
        GoldenVectors.All.Where(v => v.Direction is "encode" or "roundtrip").Select(v => v.Name);

    private static IEnumerable<string> DecodeVectorNames() =>
        GoldenVectors.All.Where(v => v.Direction is "decode" or "roundtrip").Select(v => v.Name);

    /// <summary>
    /// The same patches <c>TestTypes.BuildPatch</c> builds, but against the generated record.
    /// <c>note</c> is <c>string?</c> rather than <c>IonMaybe&lt;string&gt;</c> here — that is the
    /// no-<c>--maybe</c> representation, and the two are the same bytes by construction.
    /// </summary>
    private static IonPartial<PatchTarget> BuildPatch(string name)
    {
        var p = new IonPartial<PatchTarget>();
        switch (name)
        {
            case "empty":
                break;
            case "modified-scalar-int":
                p.SetField(x => x.n, PartialField<int>.Modified(7));
                break;
            case "modified-scalar-float":
                p.SetField(x => x.f, PartialField<float>.Modified(1.1f));
                break;
            case "modified-scalar-float-half-representable":
                p.SetField(x => x.f, PartialField<float>.Modified(1.5f));
                break;
            case "cleared-scalar-float":
                p.SetField(x => x.f, PartialField<float>.Removed());
                break;
            case "cleared-scalar-reference":
                p.SetField(x => x.s, PartialField<string>.Removed());
                break;
            case "modified-array":
                p.SetField(x => x.items, PartialField<IonArray<int>>.Modified(new IonArray<int>([1, 2, 3])));
                break;
            case "cleared-array":
                p.SetField(x => x.items, PartialField<IonArray<int>>.Removed());
                break;
            case "modified-optional-some":
                p.SetField(x => x.note, PartialField<string?>.Modified("hi"));
                break;
            case "cleared-optional":
                p.SetField(x => x.note, PartialField<string?>.Removed());
                break;
            case "modified-optional-none":
                p.SetField(x => x.note, PartialField<string?>.Modified(null));
                break;
            case "all-fields":
                // Deliberately not in declaration order: the encoder must reorder to schema order.
                p.SetField(x => x.note, PartialField<string?>.Modified("hi"));
                p.SetField(x => x.items, PartialField<IonArray<int>>.Modified(new IonArray<int>([1, 2, 3])));
                p.SetField(x => x.s, PartialField<string>.Modified("ab"));
                p.SetField(x => x.f, PartialField<float>.Removed());
                p.SetField(x => x.n, PartialField<int>.Modified(7));
                break;
            default:
                throw new NotSupportedException($"No builder for golden vector '{name}'");
        }

        return p;
    }
}
