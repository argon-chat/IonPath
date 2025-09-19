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
    public async Task UnaryCall_Test_StreamRandomInt()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, (uri, ct) => webSocketClient.ConnectAsync(uri, ct));
        var service = client.ForService<IRandomStreamInteraction>(scope);

        var list = new List<int>();

        await foreach (var number in service.Integer(0))
        {
            list.Add(number);
        }
        That(list, Has.Count.EqualTo(10));
    }
}
