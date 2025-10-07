namespace ion.runtime;

using System.Runtime.CompilerServices;

public static class IAsyncEnumerableEx
{
    public static IAsyncEnumerable<T> Select<T>(
        this IAsyncEnumerable<ReadOnlyMemory<byte>> source,
        Func<ReadOnlyMemory<byte>, T> selector,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        return Core(source, selector);

        static async IAsyncEnumerable<T> Core(
            IAsyncEnumerable<ReadOnlyMemory<byte>> src,
            Func<ReadOnlyMemory<byte>, T> sel,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var bytes in src.WithCancellation(ct).ConfigureAwait(false))
            {
                yield return sel(bytes);
            }
        }
    }
}