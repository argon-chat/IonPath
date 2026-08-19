namespace ion.runtime;

using network;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

public class IonRequestException : Exception
{
    public IonRequestException(IonProtocolError error) : this(error, null, null) { }

    /// <param name="httpStatusCode">The transport status the error arrived on, when there was one.</param>
    /// <param name="responseBody">
    /// The raw response body, when it could not be decoded as an Ion error. A proxy, a load
    /// balancer, or ASP.NET's own error page all answer with something that is not CBOR, and
    /// discarding it is what reduces every such failure to an unactionable status line.
    /// </param>
    public IonRequestException(IonProtocolError error, int? httpStatusCode, string? responseBody)
        : base(Describe(error, httpStatusCode, responseBody))
    {
        Error = error;
        HttpStatusCode = httpStatusCode;
        ResponseBody = responseBody;
    }

    public IonProtocolError Error { get; }

    /// <summary>The HTTP status the error arrived on, or null when it did not come from a response.</summary>
    public int? HttpStatusCode { get; }

    /// <summary>The undecodable response body, or null when the server sent a proper Ion error.</summary>
    public string? ResponseBody { get; }

    private static string Describe(IonProtocolError error, int? httpStatusCode, string? responseBody)
    {
        var text = $"Ion request throw exception, {error.code}: {error.msg}";

        if (httpStatusCode is { } status)
            text += $" (HTTP {status})";

        if (!string.IsNullOrEmpty(responseBody))
            text += Environment.NewLine + "Response body: " + responseBody;

        return text;
    }
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
    IServiceProvider ServiceProvider { get; }
}