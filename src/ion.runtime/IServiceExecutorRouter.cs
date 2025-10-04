namespace ion.runtime;

using System.Buffers;
using network;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

public interface IServiceExecutorRouter
{
    Task RouteExecuteAsync(string methodName, CborReader reader, CborWriter writer, CancellationToken ct = default);
}

public interface IServiceStreamExecutorRouter
{

    bool IsAllowInputStream(string methodName);

    IAsyncEnumerable<Memory<byte>> StreamRouteExecuteAsync(
        string methodName,
        CborReader initialArgs,
        IAsyncEnumerable<ReadOnlyMemory<byte>>? inputStream,
        [EnumeratorCancellation] CancellationToken ct);
}
