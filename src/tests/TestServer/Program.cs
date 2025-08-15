using ion.runtime.network;
using TestContracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIonProtocol(i =>
{
    i.AddService<IMathInteraction, MathImpl>();
    i.AddService<IVectorMathInteraction, VectorImpl>();
});

var app = builder.Build();

app.MapRpcEndpoints();

app.Run();




public class MathImpl : IMathInteraction
{
    public Task<Int32> Add(int leftOperand, int rightOperand) => Task.FromResult(leftOperand + rightOperand);

    public Task<Int32> Mul(int leftOperand, int rightOperand) => Task.FromResult(leftOperand * rightOperand);

    public Task<Int32> Sub(int leftOperand, int rightOperand) => Task.FromResult(leftOperand - rightOperand);

    public Task<Int32> Div(int leftOperand, int rightOperand) => Task.FromResult(leftOperand / rightOperand);

    public Task<Int32> Pow(int leftOperand, int rightOperand) => Task.FromResult((int)Math.Pow(leftOperand, rightOperand));
}

public class VectorImpl : IVectorMathInteraction
{
    public Task<Vector> Abs(Vector leftOperand) => Task.FromResult(leftOperand);

    public Task<Vector> Add(Vector leftOperand, Vector rightOperand) => Task.FromResult(leftOperand);

    public Task<Vector> AndNot(Vector leftOperand, Vector rightOperand) => Task.FromResult(leftOperand);
    public Task<Vector> Clamp(Vector leftOperand, Vector min, Vector max) => Task.FromResult(leftOperand);
}