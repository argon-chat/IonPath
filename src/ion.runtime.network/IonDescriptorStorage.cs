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
}

public static class IonExecutorMetadataStorage
{
    public static readonly Dictionary<string, Type> Types = new();
    public static void Add<T>(string typeName) where T : IServiceExecutorRouter 
        => Types.Add(typeName, typeof(T));

    public static IServiceExecutorRouter Take(string serviceName, AsyncServiceScope scope)
    {
        if (Types.TryGetValue(serviceName, out var type))
            return (IServiceExecutorRouter)Activator.CreateInstance(type, scope)!;
        throw new InvalidOperationException();
    }
}