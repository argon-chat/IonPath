namespace ion.runtime;

using System.Buffers;
using network;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

public interface IServiceExecutorRouter
{
    Task RouteExecuteAsync(string methodName, CborReader reader, CborWriter writer);
}

public interface IServiceStreamExecutorRouter
{
    IAsyncEnumerable<Memory<byte>> StreamRouteExecuteAsync(string methodName, CborReader reader, [EnumeratorCancellation] CancellationToken ct);
}

