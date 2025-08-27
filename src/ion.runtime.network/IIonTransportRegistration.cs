namespace ion.runtime.network;

using Microsoft.Extensions.DependencyInjection;

public interface IIonTransportRegistration
{
    IIonTransportRegistration AddService<TInterface, TImpl>()
        where TInterface : class, IIonService
        where TImpl : class, TInterface;
}


public readonly struct IonDescriptorRegistration(IServiceCollection col) : IIonTransportRegistration
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
}