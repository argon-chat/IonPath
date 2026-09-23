namespace ion.runtime.network;

using Microsoft.Extensions.DependencyInjection;

public interface IIonTransportRegistration
{
    IIonTransportRegistration AddService<TInterface, TImpl>(int? port = null, bool excludeGlobalInterceptors = false)
        where TInterface : class, IIonService
        where TImpl : class, TInterface;

    IIonTransportRegistration AddInterceptor<TImpl>(int? port = null)
        where TImpl : class, IIonInterceptor;

    IIonTransportRegistration IonWithSubProtocolTicketExchange<T>()
        where T : class, IIonTicketExchange;

    /// <summary>
    /// Registers connect/disconnect hooks that run for every stream, before the service's own
    /// (on connect) and after them (on disconnect). Resolved from each connection's scope.
    /// </summary>
    IIonTransportRegistration AddStreamLifecycle<T>()
        where T : class, IIonStreamLifecycle;

    /// <summary>Configures heartbeats, timeouts and limits for stream calls.</summary>
    IIonTransportRegistration ConfigureStreams(Action<IonStreamOptions> configure);
}


internal readonly struct IonDescriptorRegistration(IServiceCollection col) : IIonTransportRegistration
{
    internal List<int> BoundPorts { get; } = [];

    public IIonTransportRegistration AddService<TInterface, TImpl>(int? port = null, bool excludeGlobalInterceptors = false) where TInterface : class, IIonService where TImpl : class, TInterface
    {
        col.AddIonService<TInterface, TImpl>(port, excludeGlobalInterceptors);
        if (port.HasValue)
            BoundPorts.Add(port.Value);
        return this;
    }

    public IIonTransportRegistration AddInterceptor<TImpl>(int? port = null) where TImpl : class, IIonInterceptor
    {
        col.AddIonInterceptor<TImpl>(port);
        return this;
    }

    public IIonTransportRegistration AddRequestTerminator<TImpl>() where TImpl : class, IIonRequestTerminator
    {
        col.AddIonRequestTerminator<TImpl>();
        return this;
    }

    public IIonTransportRegistration IonWithSubProtocolTicketExchange<T>() where T : class, IIonTicketExchange
    {
        col.IonWithSubProtocolTicketExchange<T>();
        return this;
    }

    public IIonTransportRegistration AddStreamLifecycle<T>() where T : class, IIonStreamLifecycle
    {
        col.AddIonStreamLifecycle<T>();
        return this;
    }

    public IIonTransportRegistration ConfigureStreams(Action<IonStreamOptions> configure)
    {
        col.Configure(configure);
        return this;
    }
}
