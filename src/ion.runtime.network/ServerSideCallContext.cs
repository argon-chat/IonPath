namespace ion.runtime.network;

using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

public class ServerSideCallContext(AsyncServiceScope scope, Type @interface, MethodInfo @method) : IIonCallContext
{
    public Type InterfaceName { get; } = @interface;
    public MethodInfo MethodName { get; } = @method;

    public IDictionary<string, string> RequestItems { get; } =
        new Dictionary<string, string>([], StringComparer.InvariantCultureIgnoreCase);

    public IDictionary<string, string> ResponseItems { get; } =
        new Dictionary<string, string>([], StringComparer.InvariantCultureIgnoreCase);

    public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
    public IServiceProvider ServiceProvider => scope.ServiceProvider;
    public void Dispose() => Stopwatch.Stop();
}