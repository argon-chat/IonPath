namespace ion.runtime;

public interface IServiceExecutorRouter
{
    Task RouteExecuteAsync(string methodName, CborReader reader, CborWriter writer);
}