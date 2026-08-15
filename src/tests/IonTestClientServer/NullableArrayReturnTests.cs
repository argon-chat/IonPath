namespace IonTestClientServer;

using ion.runtime;
using ion.runtime.client;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;
using TestContracts;
using static Assert;

/// <summary>
/// Every modifier stacking in <b>return</b> position, over the real generated client → HTTP →
/// generated executor → implementation path.
/// </summary>
/// <remarks>
/// <para>
/// The bug these guard: <c>T[]?</c> (<c>Maybe&lt;Array&lt;T&gt;&gt;</c>) needs <em>two</em>
/// wrappers peeled to name the transport's element type, and the C# client peeled only one. The
/// leftover was the bare word <c>Array</c>, so <c>CallAsyncNullable&lt;Array&gt;</c> did not
/// compile at all. A compile error is the loud failure mode; the quiet one — which the null
/// assertions below exist for — is a client that decodes a single element where the executor
/// wrote a whole array.
/// </para>
/// <para>
/// The neighbouring stackings (<c>T[]</c>, <c>T?</c>, <c>T~</c>, <c>T~[]</c>, <c>T~?</c>) are
/// asserted alongside because they share the template-selection chain in
/// <c>IonCSharpGenerator.GenerateServiceClientImpl</c>: picking the wrong branch for one of them
/// is exactly how this class of regression reappears.
/// </para>
/// </remarks>
public class NullableArrayReturnTests
{
    private IonTestFactoryAsp _factoryAsp = null!;
    private HttpClient _httpClient = null!;

    [SetUp]
    public void Setup()
    {
        _factoryAsp = new IonTestFactoryAsp();
        _httpClient = _factoryAsp.CreateClient();
    }

    [TearDown]
    public void Teardown()
    {
        _httpClient.Dispose();
        _factoryAsp.Dispose();
    }

    private Task<WebSocket> WsFactory(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = _factoryAsp.Server.CreateWebSocketClient();
        protocols ??= [];
        foreach (var protocol in protocols) socket.SubProtocols.Add(protocol);
        return socket.ConnectAsync(uri, ct);
    }

    // ── i4[]? — value-typed element ─────────────────────────────────────────

    [Test]
    public async Task Return_NullableArray_ValueElement_Populated()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IMathInteraction>(scope);

        var response = await service.PowArrayMaybe(2, new IonArray<int>([1, 2, 3, 4]));

        That(response, Is.Not.Null, "a non-null request must not come back null");
        That(response!.Value.Values, Is.EqualTo(new[] { 2, 4, 8, 16 }),
            "the whole array must survive, not just its first element");
    }

    [Test]
    public async Task Return_NullableArray_ValueElement_Null()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IMathInteraction>(scope);

        var response = await service.PowArrayMaybe(2, null);

        That(response, Is.Null, "the executor wrote CBOR null; the client must decode it as null");
    }

    [Test]
    public async Task Return_NullableArray_ValueElement_Empty()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IMathInteraction>(scope);

        var response = await service.PowArrayMaybe(2, new IonArray<int>([]));

        That(response, Is.Not.Null, "an empty array is not null; conflating the two loses information");
        That(response!.Value.Size, Is.Zero);
    }

    // ── string[]? — reference-typed element ─────────────────────────────────

    [Test]
    public async Task Return_NullableArray_ReferenceElement_Populated()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IMathInteraction>(scope);

        var response = await service.Spell(3, new IonArray<int>([2, 3]));

        That(response, Is.Not.Null);
        That(response!.Value.Values, Is.EqualTo(new[] { "3^2=9", "3^3=27" }));
    }

    [Test]
    public async Task Return_NullableArray_ReferenceElement_Null()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IMathInteraction>(scope);

        That(await service.Spell(3, null), Is.Null);
    }

    // ── Vector[] and Vector[]? — message element ────────────────────────────

    [Test]
    public async Task Return_Array_MessageElement()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IVectorMathInteraction>(scope);

        var response = await service.Repeat(new Vector(1, 2, 3), 3);

        That(response.Size, Is.EqualTo(3));
        That(response.Values, Is.All.EqualTo(new Vector(1, 2, 3)));
    }

    [Test]
    public async Task Return_NullableArray_MessageElement_Populated()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IVectorMathInteraction>(scope);

        var response = await service.Spread(new Vector(1, 2, 3), 3);

        That(response, Is.Not.Null);
        That(response!.Value.Values, Is.EqualTo(new[]
        {
            new Vector(1, 2, 3), new Vector(2, 3, 4), new Vector(3, 4, 5)
        }));
    }

    [Test]
    public async Task Return_NullableArray_MessageElement_Null()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IVectorMathInteraction>(scope);

        That(await service.Spread(new Vector(1, 2, 3), 0), Is.Null);
    }

    // ── T~, T~[], T~?, T~[]? ────────────────────────────────────────────────

    [Test]
    public async Task Return_Partial()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IPatchInteraction>(scope);

        var response = await service.Apply(Patch(7, "seven"));

        That(response.GetField(x => x.n).Value, Is.EqualTo(7));
        That(response.GetField(x => x.s).Value, Is.EqualTo("seven"));
    }

    [Test]
    public async Task Return_PartialArray()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IPatchInteraction>(scope);

        var response = await service.ApplyAll(new IonArray<IonPartial<PatchTarget>>(
            [Patch(1, "one"), Patch(2, "two")]));

        That(response.Size, Is.EqualTo(2));
        That(response[0].GetField(x => x.n).Value, Is.EqualTo(1));
        That(response[1].GetField(x => x.s).Value, Is.EqualTo("two"));
    }

    [Test]
    public async Task Return_NullablePartial()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IPatchInteraction>(scope);

        var populated = await service.ApplyMany(new IonArray<IonPartial<PatchTarget>>([Patch(9, "nine")]));
        That(populated, Is.Not.Null);
        That(populated!.GetField(x => x.n).Value, Is.EqualTo(9));

        That(await service.ApplyMany(new IonArray<IonPartial<PatchTarget>>([])), Is.Null);
    }

    [Test]
    public async Task Return_NullablePartialArray_Populated()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IPatchInteraction>(scope);

        var response = await service.ApplySome(new IonArray<IonPartial<PatchTarget>>(
            [Patch(4, "four"), Patch(5, "five")]));

        That(response, Is.Not.Null);
        That(response!.Value.Size, Is.EqualTo(2),
            "both patches must survive: a single-element decode would return Size 1 or throw");
        That(response.Value[1].GetField(x => x.n).Value, Is.EqualTo(5));
    }

    [Test]
    public async Task Return_NullablePartialArray_Null()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IPatchInteraction>(scope);

        That(await service.ApplySome(new IonArray<IonPartial<PatchTarget>>([])), Is.Null);
    }

    // ── T? — the neighbour that must keep working ───────────────────────────

    [Test]
    public async Task Return_NullableScalar_Unchanged()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var service = IonClient.Create(_httpClient, WsFactory).ForService<IMathInteraction>(scope);

        That(await service.ToPositive(1, -5), Is.EqualTo(5));
        That(await service.ToPositive(1, null), Is.Null);
    }

    private static IonPartial<PatchTarget> Patch(int n, string s)
        => new IonPartial<PatchTarget>().Modify(x => x.n, n).Modify(x => x.s, s);
}
