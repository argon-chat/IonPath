namespace ion.runtime.network;

using Microsoft.Extensions.DependencyInjection;

public interface IIonTransportRegistration
{
    IIonTransportRegistration AddService<TInterface, TImpl>()
        where TInterface : class, IIonService
        where TImpl : class, TInterface;

    IIonTransportRegistration AddInterceptor<TImpl>() 
        where TImpl : class, IIonInterceptor;

    IIonTransportRegistration IonWithSubProtocolTicketExchange<T>()
        where T : class, IIonTicketExchange;
}


internal readonly struct IonDescriptorRegistration(IServiceCollection col) : IIonTransportRegistration
{
    public IIonTransportRegistration AddService<TInterface, TImpl>() where TInterface : class, IIonService where TImpl : class, TInterface
    {
        col.AddIonService<TInterface, TImpl>();
        return this;
    }

    public IIonTransportRegistration AddInterceptor<TImpl>() where TImpl : class, IIonInterceptor
    {
        col.AddIonInterceptor<TImpl>();
        return this;
    }

    public IIonTransportRegistration IonWithSubProtocolTicketExchange<T>() where T : class, IIonTicketExchange
    {
        col.IonWithSubProtocolTicketExchange<T>();
        return this;
    }
}