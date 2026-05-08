namespace ion.runtime.network;

using System.Reflection;
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

    /// <summary>
    /// Returns true if the service is allowed on the given local port.
    /// Services without a port binding are accessible from any port.
    /// </summary>
    public bool IsServiceAllowedOnPort(string serviceName, int localPort)
    {
        var iface = GetTransportInterface(serviceName);
        if (iface is null)
            return true; // will fail later with "not found"
        if (!options.Value.PortBindings.TryGetValue(iface, out var boundPort))
            return true; // no port restriction
        return boundPort == localPort;
    }

    public Type? GetTransportInterface(string serviceName) 
        => options.Value.Services.FirstOrDefault(x => x.Key.Name.Equals(serviceName)).Key;

    public MethodInfo? GetTransportMethod(string serviceName, string methodName)
        => options.Value.Services.FirstOrDefault(x => x.Key.Name.Equals(serviceName)).Key?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);

    public IServiceExecutorRouter? GetRouter(string serviceName, AsyncServiceScope scope)
    {
        try
        {
            return IonExecutorMetadataStorage.Take(serviceName, scope);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve executor router for service '{ServiceName}'", serviceName);
            return null;
        }
    }

    public IServiceStreamExecutorRouter? GetStreamRouter(string serviceName, AsyncServiceScope scope)
    {
        try
        {
            return IonExecutorMetadataStorage.TakeStream(serviceName, scope);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve stream executor router for service '{ServiceName}'", serviceName);
            return null;
        }
    }
}

