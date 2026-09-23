namespace ion.runtime.network;

using Microsoft.AspNetCore.Http;

/// <summary>The HTTP side of a stream connection, for code that runs on the server.</summary>
public static class IonStreamContextHttpExtensions
{
    /// <summary>
    /// The upgrade request the connection came in on — a WebSocket upgrade or a WebTransport
    /// CONNECT. Its lifetime is the connection's.
    /// </summary>
    /// <exception cref="InvalidOperationException">The context is not a connection this server accepted.</exception>
    public static HttpContext GetHttpContext(this IIonStreamContext context)
        => (context as IonStreamConnection)?.HttpContext
           ?? throw new InvalidOperationException("This stream context did not come in over HTTP on this server.");
}

internal sealed class IonStreamContextHolder : IIonStreamContextAccessor
{
    public IIonStreamContext? Context { get; set; }
}
