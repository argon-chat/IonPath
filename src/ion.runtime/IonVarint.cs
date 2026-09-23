namespace ion.runtime;

using System.Buffers;

/// <summary>QUIC variable-length integers (RFC 9000 §16) — the WebTransport frame length prefix.</summary>
public static class IonVarint
{
    public const ulong Max = (1UL << 62) - 1;

    public static int Size(ulong value) => value switch
    {
        < 1UL << 6 => 1,
        < 1UL << 14 => 2,
        < 1UL << 30 => 4,
        <= Max => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(value), "A QUIC varint holds at most 62 bits.")
    };

    public static int Write(Span<byte> destination, ulong value)
    {
        var size = Size(value);
        switch (size)
        {
            case 1:
                destination[0] = (byte)value;
                break;
            case 2:
                destination[0] = (byte)(0x40 | (value >> 8));
                destination[1] = (byte)value;
                break;
            case 4:
                destination[0] = (byte)(0x80 | (value >> 24));
                destination[1] = (byte)(value >> 16);
                destination[2] = (byte)(value >> 8);
                destination[3] = (byte)value;
                break;
            default:
                destination[0] = (byte)(0xC0 | (value >> 56));
                for (var i = 1; i < 8; i++)
                    destination[i] = (byte)(value >> (8 * (7 - i)));
                break;
        }

        return size;
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out ulong value, out int size)
    {
        value = 0;
        size = 0;
        if (source.IsEmpty)
            return false;

        size = 1 << (source[0] >> 6);
        if (source.Length < size)
            return false;

        value = (ulong)(source[0] & 0x3F);
        for (var i = 1; i < size; i++)
            value = (value << 8) | source[i];
        return true;
    }

    public static bool TryRead(in ReadOnlySequence<byte> source, out ulong value, out int size)
    {
        if (source.FirstSpan.Length >= 8 || source.IsSingleSegment)
            return TryRead(source.FirstSpan, out value, out size);

        Span<byte> head = stackalloc byte[8];
        var available = (int)Math.Min(8, source.Length);
        source.Slice(0, available).CopyTo(head);
        return TryRead(head[..available], out value, out size);
    }
}
