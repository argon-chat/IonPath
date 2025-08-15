namespace ion.runtime;

using ion.runtime.network;
using Microsoft.Extensions.DependencyInjection;

public static class IonExecutorMetadataStorage
{
    public static readonly Dictionary<string, Type> ServerTypes = new();
    public static readonly Dictionary<string, Type> ClientTypes = new();
    public static void AddExecutor<T>(string typeName) where T : IServiceExecutorRouter
        => ServerTypes.Add(typeName, typeof(T));

    public static void AddClient<T>(string typeName) where T : IIonService
        => ClientTypes.Add(typeName, typeof(T));

    public static IServiceExecutorRouter Take(string serviceName, AsyncServiceScope scope)
    {
        if (ServerTypes.TryGetValue(serviceName, out var type))
            return (IServiceExecutorRouter)Activator.CreateInstance(type, scope)!;
        throw new InvalidOperationException();
    }

    public static IIonService TakeClient(string serviceName, AsyncServiceScope scope, params object[] args)
    {
        if (ClientTypes.TryGetValue(serviceName, out var type))
            return (IIonService)ActivatorUtilities.CreateInstance(scope.ServiceProvider, type, args)!;
        throw new InvalidOperationException();
    }

    public static T TakeClient<T>(AsyncServiceScope scope, params object[] args) where T : IIonService
    {
        if (ClientTypes.TryGetValue(typeof(T).Name, out var type))
            return (T)ActivatorUtilities.CreateInstance(scope.ServiceProvider, type, args)!;
        throw new InvalidOperationException();
    }
}