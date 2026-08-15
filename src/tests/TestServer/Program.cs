using ion.runtime;
using ion.runtime.network;
using TestContracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIonProtocol(i =>
{
    i.AddService<IMathInteraction, MathImpl>();
    i.AddService<IVectorMathInteraction, VectorImpl>();
    i.AddService<IRandomStreamInteraction, RandomStreamImpl>();
    i.AddService<ITestBlobs, BytesTest>();
    i.AddService<IPatchInteraction, PatchImpl>();

    i.IonWithSubProtocolTicketExchange<TicketExchanger>();
});

var app = builder.Build();

app.Use(async (context, func) =>
{
    await func(context);
});

app.MapRpcEndpoints();
app.UseWebSockets();

app.Run();


public class TicketExchanger : IIonTicketExchange
{
    public async Task<ReadOnlyMemory<byte>> OnExchangeCreateAsync(IIonCallContext callContext)
    {
        if (callContext.RequestItems.TryGetValue("authToken", out var value))
            return new ReadOnlyMemory<byte>([1, 2]);
        return ReadOnlyMemory<byte>.Empty;
    }

    public async Task<(IonProtocolError?, object? ticket)> OnExchangeTransactionAsync(ReadOnlyMemory<byte> exchangeToken)
    {
        if (exchangeToken.ToArray().SequenceEqual<byte>([1, 2]))
            return (null, "ok");
        return (IonProtocolError.UPSTREAM_ERROR("Bad token"), null);
    }

    public void OnTicketApply(object ticketObject)
    {
        // ok
    }
}


public class MathImpl : IMathInteraction
{
    public Task<Int32> Add(int leftOperand, int rightOperand, CancellationToken ct = default) => Task.FromResult(leftOperand + rightOperand);

    public Task<Int32> Mul(int leftOperand, int rightOperand, CancellationToken ct = default) => Task.FromResult(leftOperand * rightOperand);

    public Task<Int32> Sub(int leftOperand, int rightOperand, CancellationToken ct = default) => Task.FromResult(leftOperand - rightOperand);

    public Task<Int32> Div(int leftOperand, int rightOperand, CancellationToken ct = default) => Task.FromResult(leftOperand / rightOperand);

    public Task<Int32> Pow(int leftOperand, int rightOperand, CancellationToken ct = default) => Task.FromResult((int)Math.Pow(leftOperand, rightOperand));
    public Task<IonArray<Int32>> PowArray(int leftOperand, IonArray<Int32> rightOperand, CancellationToken ct = default)
        => Task.FromResult(new IonArray<int>(rightOperand.Values.Select(x => (int)Math.Pow(leftOperand, x)).ToList()));

    public async Task<int?> ToPositive(int leftOperand, int? rightOperand, CancellationToken ct = default)
    {
        if (rightOperand is null)
            return null;
        return Math.Abs(rightOperand.Value);
    }

    // `i4[]?` — null propagates rather than degrading to an empty array, so the round-trip test
    // can tell "null" and "[]" apart on the wire.
    public Task<IonArray<int>?> PowArrayMaybe(int leftOperand, IonArray<int>? rightOperand,
        CancellationToken ct = default)
        => Task.FromResult(rightOperand is null
            ? (IonArray<int>?)null
            : new IonArray<int>(rightOperand.Value.Values.Select(x => (int)Math.Pow(leftOperand, x)).ToList()));

    // Same shape, reference-typed element.
    public Task<IonArray<string>?> Spell(int leftOperand, IonArray<int>? rightOperand,
        CancellationToken ct = default)
        => Task.FromResult(rightOperand is null
            ? (IonArray<string>?)null
            : new IonArray<string>(rightOperand.Value.Values
                .Select(x => $"{leftOperand}^{x}={(int)Math.Pow(leftOperand, x)}").ToList()));
}

public class VectorImpl : IVectorMathInteraction
{
    public Task<Vector> Abs(Vector leftOperand, CancellationToken ct = default) => Task.FromResult(leftOperand);

    public Task<Vector> Add(Vector leftOperand, Vector rightOperand, CancellationToken ct = default) => Task.FromResult(leftOperand);

    public Task<Vector> AndNot(Vector leftOperand, Vector rightOperand, CancellationToken ct = default) => Task.FromResult(leftOperand);
    public Task<Vector> Clamp(Vector leftOperand, Vector min, Vector max, CancellationToken ct = default) => Task.FromResult(leftOperand);

    // `index` is declared as `Rank` and the return as `Scalar` in VectorInteraction.ion. Both are
    // typedefs, and a typedef is a transparent alias, so the generated interface is plain
    // `byte` / `float`. (The `global using Rank = System.Byte;` that ionc emits lives in the
    // TestContracts assembly — global usings are not exported across assembly boundaries, exactly
    // like the existing `u4` / `f4` aliases.)
    public Task<float> Component(Vector leftOperand, byte index, CancellationToken ct = default) =>
        Task.FromResult(index switch { 0 => leftOperand.x, 1 => leftOperand.y, _ => leftOperand.z });

    public Task<VectorOfVectorOfVector> Do(Vector leftOperand, CancellationToken ct = default) =>
        Task.FromResult(new VectorOfVectorOfVector(new VectorOfVector(leftOperand, leftOperand, leftOperand),
            new VectorOfVector(leftOperand, leftOperand, leftOperand)));

    // `Vector[]` — the non-nullable neighbour of Spread, so a regression that swapped the two
    // templates shows up as a wrong value rather than only as a compile error.
    public Task<IonArray<Vector>> Repeat(Vector leftOperand, byte count, CancellationToken ct = default)
        => Task.FromResult(new IonArray<Vector>(Enumerable.Repeat(leftOperand, count).ToList()));

    // `Vector[]?` — count 0 is null, not an empty array.
    public Task<IonArray<Vector>?> Spread(Vector leftOperand, byte count, CancellationToken ct = default)
        => Task.FromResult(count == 0
            ? (IonArray<Vector>?)null
            : new IonArray<Vector>(Enumerable.Range(0, count)
                .Select(i => new Vector(leftOperand.x + i, leftOperand.y + i, leftOperand.z + i)).ToList()));
}

/// <summary>
/// Applies <c>Partial&lt;PatchTarget&gt;</c> patches. Exists so the generated executor and client
/// for a service with <c>T~</c> arguments and returns are exercised over a real transport, not
/// just compiled.
/// </summary>
public class PatchImpl : IPatchInteraction
{
    public Task<IonPartial<PatchTarget>> Apply(IonPartial<PatchTarget> patch, CancellationToken ct = default)
        => Task.FromResult(patch);

    public Task<IonPartial<PatchTarget>?> ApplyMany(IonArray<IonPartial<PatchTarget>> patches,
        CancellationToken ct = default)
        => Task.FromResult(patches.Size == 0 ? null : patches[patches.Size - 1]);

    // `T~[]` — echo, so the client can compare the decoded patches field-by-field.
    public Task<IonArray<IonPartial<PatchTarget>>> ApplyAll(IonArray<IonPartial<PatchTarget>> patches,
        CancellationToken ct = default)
        => Task.FromResult(patches);

    // `T~[]?` — the two collapsing wrappers stacked; an empty input returns null.
    public Task<IonArray<IonPartial<PatchTarget>>?> ApplySome(IonArray<IonPartial<PatchTarget>> patches,
        CancellationToken ct = default)
        => Task.FromResult(patches.Size == 0 ? (IonArray<IonPartial<PatchTarget>>?)null : patches);

    public Task<PatchTarget> ApplyTo(PatchTarget target, IonPartial<PatchTarget> patch,
        CancellationToken ct = default)
    {
        var result = target;
        patch.On(x => x.n, n => result = result with { n = n },
                onRemoved: () => result = result with { n = 0 })
            .On(x => x.f, f => result = result with { f = f },
                onRemoved: () => result = result with { f = 0 })
            .On(x => x.s, s => result = result with { s = s! },
                onRemoved: () => result = result with { s = "" })
            .On(x => x.items, items => result = result with { items = items },
                onRemoved: () => result = result with { items = new IonArray<int>([]) })
            .On(x => x.note, note => result = result with { note = note },
                onRemoved: () => result = result with { note = null });
        return Task.FromResult(result);
    }

    public Task<PatchEnvelope> Rewrap(PatchEnvelope envelope, CancellationToken ct = default)
        => Task.FromResult(envelope);
}

public class BytesTest : ITestBlobs
{
    public async Task Do(IonBytes data, CancellationToken ct = default)
    {
        return;
    }

    public async Task<IonBytes> DoIt(IonBytes data, CancellationToken ct = default)
    {
        var blob = data.Memory.ToArray();
        blob.Reverse();
        return new IonBytes(blob);
    }

    public Task<IonBytes> DoIt2(IonBytes data, CancellationToken ct = default) => DoIt(data, ct);

    public Task<IonBytes> DoIt3(IonBytes data, CancellationToken ct = default) => DoIt(data, ct);
}

public class RandomStreamImpl : IRandomStreamInteraction
{
    private static async Task<int> YieldInt()
    {
        await Task.Delay(200);
        return Random.Shared.Next();
    }

    public async IAsyncEnumerable<Int32> Integer(int seed, int _i, CancellationToken ct = default)
    {
        for (var i = 0; i < 10; i++)
            yield return await YieldInt();
    }

    public async IAsyncEnumerable<Single> Floats(int seed, IAsyncEnumerable<float>? i, CancellationToken ct = default)
    {
        await foreach (var v in i)
        {
            await Task.Delay(50, ct);
            yield return v;
        }
    }
}