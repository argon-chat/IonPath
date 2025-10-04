using ion.runtime;
using ion.runtime.network;
using TestContracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIonProtocol(i =>
{
    i.AddService<IMathInteraction, MathImpl>();
    i.AddService<IVectorMathInteraction, VectorImpl>();
    i.AddService<IRandomStreamInteraction, RandomStreamImpl>();
});

var app = builder.Build();

app.Use(async (context, func) =>
{
    await func(context);
});

app.MapRpcEndpoints();
app.UseWebSockets();

app.Run();




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
}

public class VectorImpl : IVectorMathInteraction
{
    public Task<Vector> Abs(Vector leftOperand, CancellationToken ct = default) => Task.FromResult(leftOperand);

    public Task<Vector> Add(Vector leftOperand, Vector rightOperand, CancellationToken ct = default) => Task.FromResult(leftOperand);

    public Task<Vector> AndNot(Vector leftOperand, Vector rightOperand, CancellationToken ct = default) => Task.FromResult(leftOperand);
    public Task<Vector> Clamp(Vector leftOperand, Vector min, Vector max, CancellationToken ct = default) => Task.FromResult(leftOperand);
    public Task<VectorOfVectorOfVector> Do(Vector leftOperand, CancellationToken ct = default) =>
        Task.FromResult(new VectorOfVectorOfVector(new VectorOfVector(leftOperand, leftOperand, leftOperand),
            new VectorOfVector(leftOperand, leftOperand, leftOperand)));
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