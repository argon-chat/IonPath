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
    public Task<Int32> Add(int leftOperand, int rightOperand) => Task.FromResult(leftOperand + rightOperand);

    public Task<Int32> Mul(int leftOperand, int rightOperand) => Task.FromResult(leftOperand * rightOperand);

    public Task<Int32> Sub(int leftOperand, int rightOperand) => Task.FromResult(leftOperand - rightOperand);

    public Task<Int32> Div(int leftOperand, int rightOperand) => Task.FromResult(leftOperand / rightOperand);

    public Task<Int32> Pow(int leftOperand, int rightOperand) => Task.FromResult((int)Math.Pow(leftOperand, rightOperand));
    public Task<IonArray<Int32>> PowArray(int leftOperand, IonArray<Int32> rightOperand)
        => Task.FromResult(new IonArray<int>(rightOperand.Values.Select(x => (int)Math.Pow(leftOperand, x)).ToList()));

    public async Task<int?> ToPositive(int leftOperand, int? rightOperand)
    {
        if (rightOperand is null)
            return null;
        return Math.Abs(rightOperand.Value);
    }
}

public class VectorImpl : IVectorMathInteraction
{
    public Task<Vector> Abs(Vector leftOperand) => Task.FromResult(leftOperand);

    public Task<Vector> Add(Vector leftOperand, Vector rightOperand) => Task.FromResult(leftOperand);

    public Task<Vector> AndNot(Vector leftOperand, Vector rightOperand) => Task.FromResult(leftOperand);
    public Task<Vector> Clamp(Vector leftOperand, Vector min, Vector max) => Task.FromResult(leftOperand);
    public Task<VectorOfVectorOfVector> Do(Vector leftOperand) =>
        Task.FromResult(new VectorOfVectorOfVector(new VectorOfVector(leftOperand, leftOperand, leftOperand),
            new VectorOfVector(leftOperand, leftOperand, leftOperand)));
}

public class RandomStreamImpl : IRandomStreamInteraction
{
    public async IAsyncEnumerable<Int32> Integer(int seed)
    {
        for (var i = 0; i < 10; i++)
        {
            yield return await YieldInt();
        }
    }

    private static async Task<int> YieldInt()
    {
        await Task.Delay(200);
        return Random.Shared.Next();
    }
}