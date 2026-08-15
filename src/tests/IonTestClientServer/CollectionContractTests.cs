namespace IonTestClientServer;

using ion.runtime;
using ion.runtime.client;
using Microsoft.Extensions.DependencyInjection;
using System.Formats.Cbor;
using System.Net.WebSockets;
using TestContracts;
using static Assert;

/// <summary>
/// <c>Map&lt;K,V&gt;</c>, <c>Set&lt;T&gt;</c> and <c>T[N]</c> over the real generated path:
/// generated client → HTTP → generated executor → implementation, and back.
/// </summary>
/// <remarks>
/// <para>
/// The interesting assertions here are on <b>bytes</b>, not on values. A round-trip that merely
/// returns the right value proves almost nothing about these three types: a map encoded in
/// iteration order round-trips perfectly against <em>itself</em> and only breaks against another
/// runtime, and a fixed array that quietly decoded 15 items into a 16-slot contract also
/// round-trips against itself. So each container is pinned to an exact hex string, and the two
/// order-independence claims are made the only way they can be — by building the same logical
/// value twice, in two different insertion orders, and comparing what went on the wire.
/// </para>
/// <para>
/// The wire framing throughout is <c>81</c> (a one-element CBOR array, the argument list) followed
/// by the encoded argument.
/// </para>
/// </remarks>
public class CollectionContractTests
{
    private IonCollectionFactory _factory = null!;
    private WireRecorder _wire = null!;
    private HttpClient _httpClient = null!;

    [SetUp]
    public void Setup()
    {
        _factory = new IonCollectionFactory();
        _wire = new WireRecorder();
        _httpClient = _factory.CreateDefaultClient(_wire);
    }

    [TearDown]
    public void Teardown()
    {
        // HttpClient already disposes its handler chain; the explicit call keeps NUnit1032 happy
        // and HttpMessageHandler.Dispose is idempotent.
        _httpClient.Dispose();
        _wire.Dispose();
        _factory.Dispose();
    }

    private Task<WebSocket> WsFactory(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = _factory.Server.CreateWebSocketClient();
        protocols ??= [];
        foreach (var protocol in protocols) socket.SubProtocols.Add(protocol);
        return socket.ConnectAsync(uri, ct);
    }

    private ICollectionInteraction Service(AsyncServiceScope scope)
        => IonClient.Create(_httpClient, WsFactory).ForService<ICollectionInteraction>(scope);

    // ═══════════════════════════════════════════════════════════════════════
    //  Map<K,V> — canonical key order, and independence from insertion order
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE CANONICAL-ORDERING GUARANTEE. Two dictionaries holding the same entries, built in
    /// opposite insertion orders, must put <b>identical bytes</b> on the wire.
    /// </summary>
    /// <remarks>
    /// Without it a C# <see cref="Dictionary{TKey,TValue}"/>, a JavaScript <c>Map</c> and a Rust
    /// <c>HashMap</c> — three different iteration orders — would each emit their own byte string
    /// for the same logical map, and no two runtimes could agree on a hash, a signature or a cache
    /// key. <see cref="Dictionary{TKey,TValue}"/> does preserve insertion order in practice for an
    /// add-only dictionary, which is exactly why this test is written with two orders rather than
    /// trusting one.
    /// </remarks>
    [Test]
    public async Task Map_TwoInsertionOrders_ProduceIdenticalRequestBytes()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var ascending = new Dictionary<string, int>();
        ascending["a"] = 1;
        ascending["b"] = 2;
        ascending["c"] = 3;

        var descending = new Dictionary<string, int>();
        descending["c"] = 3;
        descending["b"] = 2;
        descending["a"] = 1;

        await service.CountByTag(ascending);
        var first = _wire.RequestHex;

        await service.CountByTag(descending);
        var second = _wire.RequestHex;

        Multiple(() =>
        {
            That(second, Is.EqualTo(first), "the same entries in a different insertion order must serialise identically");
            // 81           argument array of 1
            //   a3         map, 3 entries
            //     6161 01    "a" -> 1
            //     6162 02    "b" -> 2
            //     6163 03    "c" -> 3
            That(first, Is.EqualTo("81a3616101616202616303"),
                "and the shared encoding must be the canonical one, not either insertion order");
        });
    }

    /// <summary>
    /// Canonical order is <b>length first</b>, then lexicographic — RFC 8949 §4.2.1 — which is not
    /// the same as ordinary string order.
    /// </summary>
    /// <remarks>
    /// <c>"z"</c> encodes as <c>617a</c> (2 bytes) and <c>"aa"</c> as <c>626161</c> (3), so the
    /// canonical map puts <c>"z"</c> first while every target's natural string comparison would put
    /// <c>"aa"</c> first. A "sort the keys" implementation that forgot the length rule passes every
    /// single-length test and fails this one.
    /// </remarks>
    [Test]
    public async Task Map_KeyOrderIsLengthFirstNotLexicographic()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var tags = new Dictionary<string, int>();
        tags["aa"] = 1;
        tags["z"] = 2;

        await service.CountByTag(tags);

        // 81 a2  617a 02  626161 01   — "z" (2 bytes) before "aa" (3 bytes)
        That(_wire.RequestHex, Is.EqualTo("81a2617a0262616101"),
            "length-first: the 1-char key sorts ahead of the 2-char key");
    }

    [Test]
    public async Task Map_RoundTripsThroughTheExecutor()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var response = await service.CountByTag(new Dictionary<string, int> { ["x"] = 10, ["y"] = 20 });

        That(response, Is.EquivalentTo(new Dictionary<string, int> { ["x"] = 10, ["y"] = 20 }));
    }

    /// <summary>A definite-length map header, never the indefinite <c>0xbf … 0xff</c> form.</summary>
    [Test]
    public async Task Map_UsesADefiniteLengthHeader()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        await service.CountByTag(new Dictionary<string, int> { ["k"] = 1 });

        Multiple(() =>
        {
            That(_wire.RequestHex, Does.StartWith("81a1"), "0xa1 is 'map, 1 entry'");
            That(_wire.RequestHex, Does.Not.Contain("bf"), "0xbf would be an indefinite-length map");
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Set<T> — tag 258, canonical element order
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>The same claim as the map, for sets.</summary>
    [Test]
    public async Task Set_TwoInsertionOrders_ProduceIdenticalRequestBytes()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var forwards = new HashSet<int>();
        forwards.Add(1);
        forwards.Add(2);
        forwards.Add(3);

        var backwards = new HashSet<int>();
        backwards.Add(3);
        backwards.Add(2);
        backwards.Add(1);

        await service.Dedup(forwards);
        var first = _wire.RequestHex;

        await service.Dedup(backwards);
        var second = _wire.RequestHex;

        Multiple(() =>
        {
            That(second, Is.EqualTo(first), "insertion order must not reach the wire");
            // 81  d90102  83 01 02 03   — tag 258, array of 3, canonically ordered
            That(first, Is.EqualTo("81d9010283010203"));
        });
    }

    /// <summary>
    /// Tag 258 is what distinguishes <c>Set&lt;T&gt;</c> from <c>Array&lt;T&gt;</c> on the wire.
    /// Without it a captured payload is ambiguous to any reader that does not already hold the
    /// schema, and the two are distinct Ion types with distinct schema-lock entries.
    /// </summary>
    [Test]
    public async Task Set_IsTagged258InBothDirections()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var response = await service.Dedup([5, 4]);

        Multiple(() =>
        {
            That(_wire.RequestHex, Does.StartWith("81d90102"), "request: 0xd9 0x01 0x02 is tag 258");
            That(_wire.ResponseHex, Does.StartWith("d90102"), "response: the executor tags it too");
            That(_wire.ResponseHex, Is.EqualTo("d90102820405"), "and orders 4 before 5");
            That(response, Is.EquivalentTo(new[] { 4, 5 }));
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  T[N]
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task FixedArray_RoundTripsAndIsExactlyNItems()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var coords = new IonArray<float>(Enumerable.Range(0, 16).Select(i => (float)i).ToArray());

        var response = await service.Rotate(coords);

        Multiple(() =>
        {
            // 0x90 is "array, 16 items" — a definite length, and the declared one.
            That(_wire.RequestHex, Does.StartWith("8190"));
            That(_wire.ResponseHex, Does.StartWith("90"));
            That(response.Size, Is.EqualTo(16));
            That(response.Values.First(), Is.EqualTo(1f), "the impl rotates left, so this is a real re-encode");
            That(response.Values.Last(), Is.EqualTo(0f));
        });
    }

    /// <summary>
    /// WRITERS ARE EXACT. A mismatched array is rejected by the generated client before a single
    /// byte is sent, with the typed error naming both lengths.
    /// </summary>
    [Test]
    public async Task FixedArray_WrongLength_IsRejectedByTheGeneratedClientBeforeSending()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var tooShort = new IonArray<float>(new float[15]);

        var e = ThrowsAsync<IonFixedArrayLengthException>(() => service.Rotate(tooShort))!;

        Multiple(() =>
        {
            That(e.ExpectedLength, Is.EqualTo(16), "the error names the declared N");
            That(e.ActualLength, Is.EqualTo(15), "and the length it was handed");
            That(e.Message, Does.Contain("16").And.Contain("15"));
            That(e, Is.InstanceOf<IonDecodeException>());
        });
    }

    /// <summary>
    /// THE ENTIRE POINT OF THE FEATURE, on the read side and through generated code: the generated
    /// executor handed a 15-item array for a <c>f4[16]</c> argument raises the typed error rather
    /// than silently accepting a short array.
    /// </summary>
    /// <remarks>
    /// Driven through <c>Ion_CollectionInteraction_ServiceExecutor.RouteExecuteAsync</c> — the same
    /// entry point <c>MapRpcEndpoints</c> calls — rather than over HTTP, because the transport
    /// turns any executor fault into an opaque 5xx and the claim being made is about the
    /// <em>type</em> of the failure.
    /// </remarks>
    [Test]
    [TestCase(15)]
    [TestCase(17)]
    [TestCase(0)]
    public async Task FixedArray_WrongLengthOnTheWire_IsATypedDecodeErrorNamingBothLengths(int actual)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var executor = new Ion_CollectionInteraction_ServiceExecutor(scope);

        var e = ThrowsAsync<IonFixedArrayLengthException>(
            () => executor.RouteExecuteAsync("Rotate", new CborReader(RotateRequest(actual)), new CborWriter()))!;

        Multiple(() =>
        {
            That(e.ExpectedLength, Is.EqualTo(16));
            That(e.ActualLength, Is.EqualTo(actual));
            That(e.Message, Does.Contain("16").And.Contain(actual.ToString()));
        });
    }

    /// <summary>And the declared length is accepted by the same path, so the guard is not blanket.</summary>
    [Test]
    public async Task FixedArray_CorrectLengthOnTheWire_IsAccepted()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var executor = new Ion_CollectionInteraction_ServiceExecutor(scope);

        var writer = new CborWriter();
        await executor.RouteExecuteAsync("Rotate", new CborReader(RotateRequest(16)), writer);

        That(Convert.ToHexString(writer.Encode()).ToLowerInvariant(), Does.StartWith("90"));
    }

    /// <summary>A <c>Rotate</c> request body carrying <paramref name="length"/> floats.</summary>
    private static byte[] RotateRequest(int length)
    {
        var writer = new CborWriter();
        writer.WriteStartArray(1);
        IonFormatterStorage<float>.WriteArray(writer, new IonArray<float>(new float[length]));
        writer.WriteEndArray();
        return writer.Encode();
    }

    [Test]
    public async Task FixedArray_Nullable_KeepsNullAndValueApart()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var populated = await service.RotateMaybe(
            new IonArray<float>(Enumerable.Range(0, 16).Select(i => (float)i).ToArray()));

        That(populated, Is.Not.Null);
        That(populated!.Value.Size, Is.EqualTo(16), "a nullable fixed array is still exactly N when present");

        That(await service.RotateMaybe(null), Is.Null);
        That(_wire.RequestHex, Is.EqualTo("81f6"), "0xf6 is CBOR null");
    }

    /// <summary>The nullable client path checks the length too, not just the plain one.</summary>
    [Test]
    public async Task FixedArray_Nullable_WrongLength_IsRejected()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var e = ThrowsAsync<IonFixedArrayLengthException>(
            () => service.RotateMaybe(new IonArray<float>(new float[3])))!;

        That(e.ActualLength, Is.EqualTo(3));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  stackings
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task NullableMap_KeepsNullEmptyAndPopulatedApart()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var member = new Member(Guid.Parse("00000000-0000-0000-0000-0000000000ff"), "ada");

        var populated = await service.Lookup(new Dictionary<string, Member> { ["ada"] = member });
        That(populated, Is.Not.Null);
        That(populated!["ada"], Is.EqualTo(member));

        var empty = await service.Lookup(new Dictionary<string, Member>());
        That(empty, Is.Not.Null, "an empty map is not null; conflating the two loses information");
        That(empty!, Is.Empty);
        That(_wire.RequestHex, Is.EqualTo("81a0"), "0xa0 is 'map, 0 entries'");

        That(await service.Lookup(null), Is.Null);
        That(_wire.RequestHex, Is.EqualTo("81f6"));
    }

    /// <summary><c>Set&lt;i4&gt;[]</c> — a tagged set is still exactly one item to the array codec.</summary>
    [Test]
    public async Task ArrayOfSets_RoundTrips()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var response = await service.Regroup(new IonArray<HashSet<int>>([[2, 1], [4, 3]]));

        Multiple(() =>
        {
            // 81  82            argument array, then array of 2
            //   d90102 82 0102    tagged set {1,2}
            //   d90102 82 0304    tagged set {3,4}
            That(_wire.RequestHex, Is.EqualTo("8182d901028201" + "02" + "d90102820304"));
            That(response.Size, Is.EqualTo(2));
            That(response[0], Is.EquivalentTo(new[] { 1, 2 }));
            That(response[1], Is.EquivalentTo(new[] { 3, 4 }));
        });
    }

    /// <summary>
    /// <c>Map&lt;string, Member[]&gt;</c> — the nesting that needs the generated
    /// <c>Ion_nested_array_Formatter&lt;T&gt;</c>: a container resolves its value formatter by CLR
    /// type, and <c>ion.runtime</c> registers none for <c>IonArray&lt;T&gt;</c>. Without the
    /// adapter this throws "Ion Formatter for type … is not registered" at run time, which no
    /// build catches.
    /// </summary>
    [Test]
    public async Task MapOfArrays_RoundTrips()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var ada = new Member(Guid.Parse("00000000-0000-0000-0000-00000000000a"), "ada");
        var bob = new Member(Guid.Parse("00000000-0000-0000-0000-00000000000b"), "bob");

        var response = await service.Roster(new Dictionary<string, IonArray<Member>>
        {
            ["core"] = new IonArray<Member>([ada, bob])
        });

        That(response["core"].Size, Is.EqualTo(2));
        That(response["core"][1], Is.EqualTo(bob));
    }

    /// <summary><c>Map&lt;string, Doc~&gt;</c> — a container over a sparse patch.</summary>
    /// <remarks>
    /// The patch schema for <c>Doc</c> is reached <em>only</em> through this Map's type argument;
    /// nothing in the schema has a bare <c>Doc~</c> field. A collector that stops at the wrapper
    /// leaves <c>IonPartialSchema&lt;Doc&gt;</c> unregistered and the runtime silently falls back to
    /// reflection with best-effort field order — so this asserts the field values, which is what
    /// would diverge.
    /// </remarks>
    [Test]
    public async Task MapOfPartials_RoundTrips()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var patch = new IonPartial<Doc>().Modify(x => x.title, "spec").Modify(x => x.revision, 7);

        var response = await service.Patch(new Dictionary<string, IonPartial<Doc>> { ["a"] = patch });

        Multiple(() =>
        {
            That(response["a"].GetField(x => x.title).Value, Is.EqualTo("spec"));
            That(response["a"].GetField(x => x.revision).Value, Is.EqualTo(7));
        });
    }

    /// <summary>
    /// A container beside a plain argument. A write path that emitted nothing for the container
    /// while still declaring <c>WriteStartArray(2)</c> produces malformed CBOR, and the symptom is
    /// a wrong <c>weight</c> rather than a compile error.
    /// </summary>
    [Test]
    public async Task ContainerBesideAScalarArgument_KeepsTheArgumentFraming()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var response = await service.Merge(new Dictionary<string, int> { ["a"] = 2, ["b"] = 3 }, 10);

        Multiple(() =>
        {
            That(_wire.RequestHex, Is.EqualTo("82a26161026162030a"), "0x82: two arguments, map then 0x0a");
            That(response["a"], Is.EqualTo(20));
            That(response["b"], Is.EqualTo(30));
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  whole-message round-trips
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Every container field shape in one message, over the real transport.</summary>
    [Test]
    public async Task AllContainerShapes_RoundTrip()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var sent = SampleShapes();
        var response = await service.Echo(sent);

        Multiple(() =>
        {
            That(response.tags, Is.EquivalentTo(sent.tags));
            That(response.ids, Is.EquivalentTo(sent.ids));
            That(response.coords.Values, Is.EqualTo(sent.coords.Values));
            That(response.membersByName!.Keys, Is.EquivalentTo(sent.membersByName!.Keys));
            That(response.groups.Size, Is.EqualTo(sent.groups.Size));
            That(response.layers.Size, Is.EqualTo(sent.layers.Size));
            That(response.offsets!.Value.Values, Is.EqualTo(sent.offsets!.Value.Values));
            That(response.patches["p"].GetField(x => x.title).Value, Is.EqualTo("spec"));
            That(response.rosters["core"].Size, Is.EqualTo(1));
            That(response.cohorts[Tier.Paid], Is.EquivalentTo(new[] { 7, 8 }));
        });
    }

    /// <summary>
    /// The whole message is byte-stable across insertion orders too — the claim has to survive
    /// nesting, not just a top-level argument.
    /// </summary>
    [Test]
    public async Task AllContainerShapes_AreByteStableAcrossInsertionOrders()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        await service.Echo(SampleShapes());
        var forwards = _wire.RequestHex;

        await service.Echo(SampleShapes(reverse: true));
        var backwards = _wire.RequestHex;

        That(backwards, Is.EqualTo(forwards),
            "every Map and Set in the message must encode canonically, at every nesting depth");
    }

    /// <summary>Every key type ION0061 allows, over the real transport.</summary>
    [Test]
    public async Task EveryLegalKeyType_RoundTrips()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var sent = SampleKeys();
        var response = await service.EchoKeys(sent);

        Multiple(() =>
        {
            That(response.byI1, Is.EquivalentTo(sent.byI1));
            That(response.byI2, Is.EquivalentTo(sent.byI2));
            That(response.byI4, Is.EquivalentTo(sent.byI4));
            That(response.byI8, Is.EquivalentTo(sent.byI8));
            That(response.byI16, Is.EquivalentTo(sent.byI16));
            That(response.byU1, Is.EquivalentTo(sent.byU1));
            That(response.byU2, Is.EquivalentTo(sent.byU2));
            That(response.byU4, Is.EquivalentTo(sent.byU4));
            That(response.byU8, Is.EquivalentTo(sent.byU8));
            That(response.byU16, Is.EquivalentTo(sent.byU16));
            That(response.byBool, Is.EquivalentTo(sent.byBool));
            That(response.byString, Is.EquivalentTo(sent.byString));
            That(response.byGuid, Is.EquivalentTo(sent.byGuid));
            That(response.byEnum, Is.EquivalentTo(sent.byEnum));
        });
    }

    /// <summary>
    /// An <c>i4</c> key set that makes the length-first rule observable end to end: <c>-1</c>
    /// encodes as <c>20</c> (one byte) and <c>1000</c> as <c>1903e8</c> (three), so canonical order
    /// puts <c>-1</c> first where a bytewise-only sort would put <c>1000</c> first
    /// (<c>0x19 &lt; 0x20</c>).
    /// </summary>
    [Test]
    public async Task IntegerKeyOrder_PutsShortEncodingsFirst()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = Service(scope);

        var keys = SampleKeys() with
        {
            byI4 = new Dictionary<int, int> { [1000] = 1, [-1] = 2, [0] = 3 }
        };

        var response = await service.EchoKeys(keys);
        var body = Convert.ToHexString(EncodeMap(keys.byI4)).ToLowerInvariant();

        Multiple(() =>
        {
            //  a3  00 03   0x00 = 0    (1 byte)
            //      20 02   0x20 = -1   (1 byte)
            //      1903e8 01           (3 bytes)
            That(body, Is.EqualTo("a30003200219 03e801".Replace(" ", "")));
            That(response.byI4, Is.EquivalentTo(keys.byI4));
        });
    }

    private static byte[] EncodeMap(Dictionary<int, int> map)
    {
        var writer = new CborWriter();
        IonFormatterStorage<Dictionary<int, int>>.Write(writer, map);
        return writer.Encode();
    }

    // ── sample values ───────────────────────────────────────────────────────

    private static ContainerShapes SampleShapes(bool reverse = false)
    {
        var ada = new Member(Guid.Parse("00000000-0000-0000-0000-00000000000a"), "ada");
        var bob = new Member(Guid.Parse("00000000-0000-0000-0000-00000000000b"), "bob");

        var tags = new Dictionary<string, int>();
        var ids = new HashSet<int>();
        var members = new Dictionary<string, Member>();
        var cohort = new HashSet<int>();

        // The whole point: identical contents, opposite insertion orders.
        foreach (var (k, v) in Order(("a", 1), ("b", 2), ("c", 3))) tags[k] = v;
        foreach (var i in Order(1, 2, 3)) ids.Add(i);
        foreach (var (k, v) in Order(("ada", ada), ("bob", bob))) members[k] = v;
        foreach (var i in Order(7, 8)) cohort.Add(i);

        return new ContainerShapes(
            tags: tags,
            ids: ids,
            coords: new IonArray<float>(Enumerable.Range(0, 16).Select(i => i * 0.5f).ToArray()),
            membersByName: members,
            groups: new IonArray<HashSet<int>>([[2, 1]]),
            layers: new IonArray<HashSet<int>>([[4, 3]]),
            offsets: new IonArray<float>(Enumerable.Range(0, 16).Select(i => (float)-i).ToArray()),
            patches: new Dictionary<string, IonPartial<Doc>>
            {
                ["p"] = new IonPartial<Doc>().Modify(x => x.title, "spec").Modify(x => x.revision, 3)
            },
            rosters: new Dictionary<string, IonArray<Member>> { ["core"] = new IonArray<Member>([ada]) },
            cohorts: new Dictionary<Tier, HashSet<int>> { [Tier.Paid] = cohort });

        T[] Order<T>(params T[] items) => reverse ? items.Reverse().ToArray() : items;
    }

    private static KeyMatrix SampleKeys() => new(
        byI1: new Dictionary<sbyte, int> { [-1] = 1, [2] = 2 },
        byI2: new Dictionary<short, int> { [-300] = 1, [300] = 2 },
        byI4: new Dictionary<int, int> { [-70000] = 1, [70000] = 2 },
        byI8: new Dictionary<long, int> { [long.MinValue] = 1, [long.MaxValue] = 2 },
        byI16: new Dictionary<Int128, int> { [Int128.MinValue] = 1, [7] = 2 },
        byU1: new Dictionary<byte, int> { [0] = 1, [255] = 2 },
        byU2: new Dictionary<ushort, int> { [0] = 1, [ushort.MaxValue] = 2 },
        byU4: new Dictionary<uint, int> { [0] = 1, [uint.MaxValue] = 2 },
        byU8: new Dictionary<ulong, int> { [0] = 1, [ulong.MaxValue] = 2 },
        byU16: new Dictionary<UInt128, int> { [0] = 1, [UInt128.MaxValue] = 2 },
        byBool: new Dictionary<bool, int> { [false] = 1, [true] = 2 },
        byString: new Dictionary<string, int> { ["aa"] = 1, ["z"] = 2 },
        byGuid: new Dictionary<Guid, int> { [Guid.Empty] = 1, [Guid.Parse("11111111-1111-1111-1111-111111111111")] = 2 },
        byEnum: new Dictionary<Tier, int> { [Tier.Free] = 1, [Tier.Trial] = 2 });
}
