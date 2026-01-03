namespace ion.runtime.network;

using System.Reflection;

internal interface __internal_ion
{
    void __exchange();


    static MethodInfo __exchange_ref =>
        typeof(__internal_ion).GetMethod(nameof(__exchange), BindingFlags.Public | BindingFlags.Instance)!;
}