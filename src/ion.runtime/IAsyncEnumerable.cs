namespace ion.runtime;

public static class IAsyncEnumerableEx
{
    public static async IAsyncEnumerable<T> Select<T>(
        this IAsyncEnumerable<ReadOnlyMemory<byte>> source, Func<ReadOnlyMemory<byte>, T> selector)
    {
        await foreach (var bytes in source)
            yield return selector(bytes);
    }
}