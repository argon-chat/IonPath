namespace IonTestClientServer;

using System.Net.WebSockets;
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

    [SetUp]
    public void Setup()
    {
        _factoryAsp = new IonTestFactoryAsp();
        httpClient = _factoryAsp.CreateClient();
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
        var client = IonClient.Create(httpClient, WsFactory);
        var service = client.ForService<IMathInteraction>(scope);

        var response = await service.Add(4, 4);
        That(response == 8);
    }

    private Task<WebSocket> WsFactory(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = _factoryAsp.Server.CreateWebSocketClient();
        protocols ??= [];
        foreach (var protocol in protocols) socket.SubProtocols.Add(protocol);
        return socket.ConnectAsync(uri, ct);
    }

    [Test]
    public async Task UnaryCall_Test_TreeObj()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, WsFactory);
        var service = client.ForService<IVectorMathInteraction>(scope);

        var response = await service.Do(new Vector(1, 2, 3));
    }

    [Test]
    public async Task UnaryCall_Test_ArrPow()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, WsFactory);
        var service = client.ForService<IMathInteraction>(scope);

        var response = await service.PowArray(1, new IonArray<int>([1, 2, 3, 4]));
    }


    [Test]
    public async Task UnaryCall_Test_Nullable()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, WsFactory);
        var service = client.ForService<IMathInteraction>(scope);

        var response1 = await service.ToPositive(1, null);
        var response2 = await service.ToPositive(1, -1);

        That(response1 == null);
        That(response2 == 1);
    }


    private class InterceptorTest : IIonInterceptor
    {
        public Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
        {
            context.RequestItems.Add("authToken", "123");
            return next(context, ct);
        }
    }

    [Test]
    public async Task UnaryCall_Test_StreamRandomInt()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, WsFactory).WithInterceptor<InterceptorTest>();
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
        var client = IonClient.Create(httpClient, WsFactory);
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

    [Test]
    public async Task UnaryCall_Bytes()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, WsFactory);
        var service = client.ForService<ITestBlobs>(scope);
        var bytes = new IonBytes([1, 2, 3, 4, 5]);
        var expected = bytes.ToArray().AsEnumerable().Reverse().ToArray();
        await service.Do(bytes);
        var response = await service.DoIt(bytes);

        That(response.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public async Task UnaryCall_Bytes_Empty()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, WsFactory);
        var service = client.ForService<ITestBlobs>(scope);
        
        var response = await service.DoIt(IonBytes.Empty);

        That(response.ToArray(), Is.Empty);
    }

    [Test]
    [TestCase(1)]
    [TestCase(100)]
    [TestCase(1024)]              // 1 KB
    [TestCase(10 * 1024)]         // 10 KB
    [TestCase(100 * 1024)]        // 100 KB
    [TestCase(1024 * 1024)]       // 1 MB
    [TestCase(10 * 1024 * 1024)]  // 10 MB
    public async Task UnaryCall_Bytes_VariousSizes(int size)
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, WsFactory);
        var service = client.ForService<ITestBlobs>(scope);

        var data = new byte[size];
        Random.Shared.NextBytes(data);
        var bytes = new IonBytes(data);
        var expected = data.AsEnumerable().Reverse().ToArray();

        var response = await service.DoIt(bytes);

        That(response.Length, Is.EqualTo(size));
        That(response.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public async Task UnaryCall_Bytes_LargeFile_50MB()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, WsFactory);
        var service = client.ForService<ITestBlobs>(scope);

        const int size = 50 * 1024 * 1024; // 50 MB
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        var bytes = new IonBytes(data);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await service.DoIt(bytes);
        sw.Stop();

        That(response.Length, Is.EqualTo(size));
        TestContext.WriteLine($"50MB roundtrip: {sw.ElapsedMilliseconds}ms");
    }

    [Test]
    public async Task UnaryCall_Bytes_Dispose_ReleasesMemory()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient, WsFactory);
        var service = client.ForService<ITestBlobs>(scope);

        var data = new byte[1024];
        Random.Shared.NextBytes(data);

        var response = await service.DoIt(new IonBytes(data));
        
        That(response.Length, Is.EqualTo(1024));
        
        // После dispose память должна освободиться
        response.Dispose();
        
        That(response.Length, Is.EqualTo(0));
        That(response.IsEmpty, Is.True);
    }
}