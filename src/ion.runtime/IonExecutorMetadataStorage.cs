namespace ion.runtime;

using Microsoft.Extensions.DependencyInjection;
using network;
using System.Diagnostics.CodeAnalysis;

public static class IonExecutorMetadataStorage
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IonExecutorMetadataStorage))]
    public static readonly Dictionary<string, Type> ServerTypes = new();
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IonExecutorMetadataStorage))]
    public static readonly Dictionary<string, Type> ClientTypes = new();
    public static void AddExecutor<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.Interfaces)] T>
        (string typeName)
    {
        if (typeof(T).GetInterfaces().Contains(typeof(IServiceExecutorRouter)) || typeof(T).GetInterfaces().Contains(typeof(IServiceStreamExecutorRouter)))
            ServerTypes.TryAdd(typeName, typeof(T));
    }

    public static void AddClient<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.Interfaces)] T
    >(string typeName)
        where T : IIonService
        => ClientTypes.TryAdd(typeName, typeof(T));

    public static IServiceExecutorRouter Take(string serviceName, AsyncServiceScope scope)
    {
        if (ServerTypes.TryGetValue(serviceName, out var type))
            return (IServiceExecutorRouter)Activator.CreateInstance(type, scope)!;
        throw new InvalidOperationException();
    }
    public static IServiceStreamExecutorRouter TakeStream(string serviceName, AsyncServiceScope scope)
    {
        if (ServerTypes.TryGetValue(serviceName, out var type))
            return (IServiceStreamExecutorRouter)Activator.CreateInstance(type, scope)!;
        throw new InvalidOperationException();
    }

    public static IIonService TakeClient(string serviceName, AsyncServiceScope scope, params object[] args)
    {
        if (ClientTypes.TryGetValue(serviceName, out var type))
            return (IIonService)ActivatorUtilities.CreateInstance(scope.ServiceProvider, type, args)!;
        throw new InvalidOperationException();
    }

    public static T TakeClient<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T
    >(AsyncServiceScope scope, params object[] args)
        where T : IIonService
    {
        if (ClientTypes.TryGetValue(typeof(T).Name, out var type))
            return (T)ActivatorUtilities.CreateInstance(scope.ServiceProvider, type, args)!;
        throw new InvalidOperationException();
    }
}