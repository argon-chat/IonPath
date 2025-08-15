namespace IonTestClientServer;

using ion.runtime.client;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task HealthCheck_Returns200()
    {
        await using var scope = _factoryAsp.Services.CreateAsyncScope();
        var client = IonClient.Create(httpClient);
        var service = client.ForService<IMathInteraction>(scope);

        var response = await service.Add(4, 4);
        That(response == 8);
    }

}
