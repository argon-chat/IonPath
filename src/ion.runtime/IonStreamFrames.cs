namespace ion.runtime;

using System.Buffers;
using System.Runtime.CompilerServices;

/// <summary>The encode and decode loops generated stream executors delegate to.</summary>
/// <remarks>
/// Both loops reuse one <see cref="CborWriter"/> / <see cref="CborReader"/> and one pooled buffer for
/// the whole stream, so a stream that yields a million items allocates for the items themselves and
/// nothing else.
/// </remarks>
public static class IonStreamFrames
{
    /// <summary>Encodes every item of <paramref name="source"/> into a DATA frame in a reused, pooled buffer.</summary>
    public static async IAsyncEnumerable<Memory<byte>> Encode<T>(
        IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var writer = new CborWriter();
        var buffer = ArrayPool<byte>.Shared.Rent(256);

        try
        {
            await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
            {
                writer.Reset();
                IonFormatterStorage<T>.Write(writer, item);

                var size = writer.BytesWritten + 1;
                if (buffer.Length < size)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(size);
                }

                buffer[0] = IonStreamProtocol.OpData;
                writer.Encode(buffer.AsSpan(1));
                yield return buffer.AsMemory(0, size);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Decodes an input stream of <c>array(1)[item]</c> payloads, or returns null for a method
    /// called without one.
    /// </summary>
    /// <remarks>
    /// One <see cref="CborReader"/> is re-pointed at each payload. The payloads themselves are not
    /// pooled: a decoded item may keep its payload (<c>bytes</c> decodes to an <see cref="IonBytes"/>
    /// over it), so each one is the item's own storage.
    /// </remarks>
    public static IAsyncEnumerable<T>? DecodeInput<T>(IAsyncEnumerable<ReadOnlyMemory<byte>>? input)
        => input is null ? null : DecodeInputCore<T>(input);

    private static async IAsyncEnumerable<T> DecodeInputCore<T>(
        IAsyncEnumerable<ReadOnlyMemory<byte>> input,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        CborReader? reader = null;

        await foreach (var payload in input.WithCancellation(ct).ConfigureAwait(false))
        {
            if (reader is null) reader = new CborReader(payload);
            else reader.Reset(payload);

            reader.ReadStartArray();
            var item = IonFormatterStorage<T>.Read(reader);
            reader.ReadEndArray();
            yield return item;
        }
    }
}
