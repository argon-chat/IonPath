namespace ion.runtime.network;

using System.Buffers;
using System.Collections.Concurrent;
using System.Formats.Cbor;
using System.Net.WebSockets;
using System.Reflection;

/// <summary>A message that exceeded <see cref="IonStreamOptions.MaxReceiveMessageSize"/>.</summary>
internal sealed class IonStreamMessageTooLargeException(int limit)
    : Exception($"A client message exceeded the {limit}-byte limit.");

/// <summary>A client broke the stream protocol.</summary>
public sealed class IonStreamProtocolException(string message) : Exception(message);

/// <summary>The client sent an ERROR frame on its input stream; the stream method sees this when it reads on.</summary>
public sealed class IonStreamInputException(IonProtocolError error)
    : IonRequestException(error, (Exception?)null);

/// <summary>A pushed connection overflowed its outbound queue.</summary>
public sealed class IonStreamSlowConsumerException(int capacity)
    : Exception($"The connection fell {capacity} pushed messages behind.");

/// <summary>Server code aborted the connection.</summary>
public sealed class IonStreamAbortedException(string? reason)
    : Exception(reason ?? "The connection was aborted by the server.");

/// <summary>
/// A resumable session lost its transport and the client did not come back within
/// <see cref="IonStreamOptions.ResumeWindow"/>. The inner exception is why the transport was lost.
/// </summary>
public sealed class IonStreamNotResumedException(TimeSpan window, Exception? inner)
    : Exception($"The client did not resume the session within {window}.", inner);

/// <summary>Thrown inside the send path when a frame could not be delivered; never escapes the connection.</summary>
internal sealed class IonStreamSendException(Exception? inner) : Exception("The frame was not sent.", inner);

internal static class IonStreamWire
{
    /// <summary>Receive buffer size; a message that does not fit is assembled across reads.</summary>
    public const int ScratchSize = 16 * 1024;

    /// <summary>
    /// The <c>T</c> of a method returning <c>IAsyncEnumerable&lt;T&gt;</c>, or null when the method
    /// is not a stream.
    /// </summary>
    public static Type? StreamElementType(MethodInfo? method)
    {
        var returnType = method?.ReturnType;
        if (returnType is { IsGenericType: true } &&
            returnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            return returnType.GetGenericArguments()[0];
        return null;
    }
}
