namespace ion.runtime.network;

using System.Formats.Cbor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class IonDescriptorStorage(IServiceProvider serviceProvider, IOptions<IonTransportOptions> options, ILogger<IonDescriptorStorage> logger)
{

    public IIonService GetService(string serviceName)
    {
        var (key, value) = options.Value.Services.FirstOrDefault(x => x.Key.Name.Equals(serviceName));
        if (serviceProvider.GetService(interfaceType) is not IIonService service)
            throw new InvalidOperationException($"Service '{serviceName}' not found.");
        return 
    }
}

public class IonServiceExecutor(AsyncServiceScope scope)
{
    public async Task FooBar_Execute(CborReader reader)
    {
        var service = scope.ServiceProvider.GetRequiredService<ITestService>();

        const int argumentSize = 2;

        var arraySize = reader.ReadStartArray();

        if (arraySize is null)
            throw new InvalidOperationException();
        if (argumentSize != arraySize)
            throw new InvalidOperationException();
        var arg_i = reader.ReadInt32();
        var arg_b = reader.ReadTextString();

        reader.ReadEndArray();

        await service.FooBar(arg_i, arg_b);
    }
}

public interface ITestService : IIonService
{
    Task FooBar(int i, string b);
}