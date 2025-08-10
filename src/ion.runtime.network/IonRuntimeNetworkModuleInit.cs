namespace ion.runtime.network;

using System.Runtime.CompilerServices;

public static class IonRuntimeNetworkModuleInit
{
    [ModuleInitializer]
    public static void Init()
    {
        IonFormatterStorage<IonProtocolError>.Value = new IonProtocolErrorFormatter();
    }
}