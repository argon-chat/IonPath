namespace ion.runtime.network;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class IonDescriptorStorage(IServiceProvider serviceProvider, IOptions<IonTransportOptions> options, ILogger<IonDescriptorStorage> logger)
{
    public IIonService GetService(string serviceName)
    {
        var (key, value) = options.Value.Services.FirstOrDefault(x => x.Key.Name.Equals(serviceName));

        if (value is null || serviceProvider.GetService(value) is not IIonService service)
            throw new InvalidOperationException($"Service '{serviceName}' not found.");
        return service;
    }

    public IServiceExecutorRouter? GetRouter(string serviceName, AsyncServiceScope scope)
    {
        try
        {
            return IonExecutorMetadataStorage.Take(serviceName, scope);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public IServiceStreamExecutorRouter? GetStreamRouter(string serviceName, AsyncServiceScope scope)
    {
        try
        {
            return IonExecutorMetadataStorage.TakeStream(serviceName, scope);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

