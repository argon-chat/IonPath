namespace IonTestClientServer;

using ion.runtime.client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using ion.runtime;
using ion.runtime.network;
using Microsoft.Extensions.Logging;
using TestContracts;
using static Assert;

public class Tests
{
    private IonTestFactoryAsp _factoryAsp = null!;
    private HttpClient httpClient = null!;
    private WebSocketClient webSocketClient = null!;

    [SetUp]
    public void Setup()
    {
        _factoryAsp = new IonTestFactoryAsp();
        httpClient = _factoryAsp.CreateClient();
        webSocketClient = _factoryAsp.Server.CreateWebSocketClient();
    }

    [TearDown]
    public void Teardown()
    {
        httpClient.Dispose();
        _factoryAsp.Dispose();
    }


    [Test]
    public async Task UnaryCall_Test_Add_4_Plus_4()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, (uri, ct) => webSocketClient.ConnectAsync(uri, ct));
        var service = client.ForService<IMathInteraction>(scope);

        var response = await service.Add(4, 4);
        That(response == 8);
    }

    [Test]
    public async Task UnaryCall_Test_TreeObj()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, (uri, ct) => webSocketClient.ConnectAsync(uri, ct));
        var service = client.ForService<IVectorMathInteraction>(scope);

        var response = await service.Do(new Vector(1, 2, 3));
    }

    [Test]
    public async Task UnaryCall_Test_ArrPow()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, (uri, ct) => webSocketClient.ConnectAsync(uri, ct));
        var service = client.ForService<IMathInteraction>(scope);

        var response = await service.PowArray(1, new IonArray<int>([1, 2, 3, 4]));
    }


    [Test]
    public async Task UnaryCall_Test_Nullable()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, (uri, ct) => webSocketClient.ConnectAsync(uri, ct));
        var service = client.ForService<IMathInteraction>(scope);

        var response1 = await service.ToPositive(1, null);
        var response2 = await service.ToPositive(1, -1);

        That(response1 == null);
        That(response2 == 1);
    }

    [Test]
    public async Task UnaryCall_Test_StreamRandomInt()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, (uri, ct) => webSocketClient.ConnectAsync(uri, ct));
        var service = client.ForService<IRandomStreamInteraction>(scope);

        var list = new List<int>();

        await foreach (var number in service.Integer(0, 1))
        {
            list.Add(number);
        }

        That(list, Has.Count.EqualTo(10));
    }

    [Test]
    public async Task FullDuplexStream_Test_StreamRandomFloats()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, (uri, ct) => webSocketClient.ConnectAsync(uri, ct));
        var service = client.ForService<IRandomStreamInteraction>(scope);

        var result = new List<float>();
        float[] floats = [1.1f, 2.2f, 3.3f, 4.4f, 5.5f, 6.6f, 7.7f, 8.8f, 9.9f];

        await foreach (var number in service.Floats(0, ToAsyncEnumerable(floats)))
            result.Add(number);

        That(result, Has.Count.EqualTo(floats.Length));
        That(result, Is.EqualTo(floats));

        static async IAsyncEnumerable<float> ToAsyncEnumerable(float[] values)
        {
            foreach (var v in values)
            {
                yield return v;
                await Task.Yield();
            }
        }
    }
}