namespace ion.runtime.network;

using System.Reflection;
using System.Runtime.CompilerServices;

internal class IonRequestTerminatorStorage
{
    private readonly Lazy<Dictionary<(Type, MethodInfo), IIonRequestTerminator>> terminators;

    public IonRequestTerminatorStorage(IEnumerable<IIonRequestTerminator> t)
    {
        terminators = new(() =>
            t.ToDictionary(
                x => (x.InterfaceName, x.MethodName),
                x => x));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IIonRequestTerminator? TakeTerminator(Type interfaceName, MethodInfo methodName)
    {
        terminators.Value.TryGetValue((interfaceName, methodName), out var result);
        return result;
    }
}