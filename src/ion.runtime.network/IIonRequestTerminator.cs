namespace ion.runtime.network;

using System.Reflection;
using Microsoft.AspNetCore.Http;

public interface IIonRequestTerminator
{
    Type InterfaceName { get; }
    MethodInfo MethodName { get; }

    Task OnTerminateAsync(HttpResponse response, CancellationToken ct = default);
}