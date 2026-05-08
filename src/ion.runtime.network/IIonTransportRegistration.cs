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


}