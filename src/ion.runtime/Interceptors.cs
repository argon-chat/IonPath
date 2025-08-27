namespace ion.runtime;

using network;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

public class IonRequestException(IonProtocolError error)
    : Exception($"Ion request throw exception, {error.code}: {error.msg}")
{
    public IonProtocolError Error { get; private set; } = error;
}

public interface IIonInterceptor
{
    Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct);
}

public interface IIonCallContext : IDisposable
{
    Type InterfaceName { get; }
    MethodInfo MethodName { get; }
    IDictionary<string, string> RequestItems { get; }
    IDictionary<string, string> ResponseItems { get; }
    Stopwatch Stopwatch { get; }
    AsyncServiceScope AsyncServiceScope { get; }
}