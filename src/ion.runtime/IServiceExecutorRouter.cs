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

/// <summary>The generated executor of a service's <c>stream</c> methods.</summary>
public interface IServiceStreamExecutorRouter
{
    /// <summary>Whether the method takes a client input stream.</summary>
    bool IsAllowInputStream(string methodName);

    /// <summary>
    /// Decodes the arguments, runs the stream method and yields finished DATA frames,
    /// <c>[OpData][cbor item]</c>.
    /// </summary>
    /// <remarks>
    /// Each yielded frame is borrowed from a buffer the executor reuses (see
    /// <see cref="IonStreamFrames.Encode{T}"/>): it is valid until the next <c>MoveNextAsync</c>,
    /// which is exactly as long as the server needs it to send. Argument decoding happens before the
    /// first item, so malformed arguments fail the call instead of being sent half a stream.
    /// </remarks>
    IAsyncEnumerable<Memory<byte>> StreamRouteFramesAsync(
        string methodName,
        CborReader initialArgs,
        IAsyncEnumerable<ReadOnlyMemory<byte>>? inputStream,
        CancellationToken ct);
}
