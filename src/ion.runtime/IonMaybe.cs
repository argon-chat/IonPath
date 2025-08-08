namespace ion.runtime;

public readonly record struct IonMaybe<T>
{
    public T Value { get; }
    public bool HasValue { get; }

    private IonMaybe(T value, bool hasValue)
    {
        Value = value;
        HasValue = hasValue;
    }

    public static explicit operator T(IonMaybe<T> ionMaybe)
    {
        if (ionMaybe.HasValue)
            return ionMaybe.Value;

        throw new InvalidOperationException("Cannot convert a None value.");
    }

    public static implicit operator IonMaybe<T>(T value) =>
        EqualityComparer<T>.Default.Equals(value, default) ? None : Some(value);

    public static IonMaybe<T> Some(T value) => new(value, true);
    public static IonMaybe<T> None => new(default(T), false);
}

public readonly record struct IonArray<T>
{
    public IReadOnlyList<T> Values { get; }

    public static IonArray<T> Empty => new([]);

    public int Size => Values.Count;

    public T this[in int index] => Values[index];

    public IonArray(IEnumerable<T> enumerable) => Values = enumerable.ToList().AsReadOnly();
    public IonArray(IList<T> enumerable) => Values = enumerable.AsReadOnly();
    public IonArray(T[] enumerable) => Values = enumerable.AsReadOnly();
    public IonArray(Span<T> enumerable) => Values = enumerable.ToArray().AsReadOnly();
}