namespace ion.runtime;

using System.Buffers;

/// <summary>
/// High-performance binary data container for Ion serialization.
/// Can own pooled memory that should be released via <see cref="Dispose"/>.
/// </summary>
public struct IonBytes : IDisposable
{
    private ReadOnlyMemory<byte> _data;
    private IMemoryOwner<byte>? _owner;

    public readonly ReadOnlyMemory<byte> Memory => _data;
    public readonly ReadOnlySpan<byte> Span => _data.Span;
    public readonly int Length => _data.Length;
    public readonly bool IsEmpty => _data.IsEmpty;

    /// <summary>
    /// Creates IonBytes from existing memory (no ownership transfer).
    /// </summary>
    public IonBytes(ReadOnlyMemory<byte> data)
    {
        _data = data;
        _owner = null;
    }

    /// <summary>
    /// Creates IonBytes from byte array (no ownership transfer).
    /// </summary>
    public IonBytes(byte[] data)
    {
        _data = data;
        _owner = null;
    }

    /// <summary>
    /// Creates IonBytes from Memory (no ownership transfer).
    /// </summary>
    public IonBytes(Memory<byte> data)
    {
        _data = data;
        _owner = null;
    }

    /// <summary>
    /// Creates IonBytes that owns pooled memory. Must call Dispose() when done.
    /// </summary>
    internal IonBytes(IMemoryOwner<byte> owner, int length)
    {
        _owner = owner;
        _data = owner.Memory[..length];
    }

    public static IonBytes Empty => new(ReadOnlyMemory<byte>.Empty);

    public static implicit operator IonBytes(byte[] data) => new(data);
    public static implicit operator IonBytes(Memory<byte> data) => new(data);
    public static implicit operator IonBytes(ReadOnlyMemory<byte> data) => new(data);

    public static implicit operator ReadOnlyMemory<byte>(IonBytes bytes) => bytes._data;
    public static implicit operator ReadOnlySpan<byte>(IonBytes bytes) => bytes._data.Span;

    public readonly byte[] ToArray() => _data.ToArray();

    /// <summary>
    /// Releases pooled memory if owned. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        _owner?.Dispose();
        _owner = null;
        _data = ReadOnlyMemory<byte>.Empty;
    }
}
