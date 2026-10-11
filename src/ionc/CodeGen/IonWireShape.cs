namespace ion.compiler.CodeGen;

using ion.runtime;

/// <summary>
/// What a generated reader needs to know about a positional array — a message, a union case
/// payload or a method's argument list — beyond its length. Shared by all three generators so
/// that C#, TypeScript and Rust accept exactly the same short payloads.
/// </summary>
/// <remarks>
/// <para>
/// A positional array can arrive <b>longer</b> than the reader's schema (a newer writer appended
/// fields; the reader skips them) or <b>shorter</b> (an older writer, from before fields were
/// appended). The short case is decodable only when every field the payload lacks is nullable:
/// those read as null. So the reader requires the leading fields up to and including the last
/// non-nullable one, reads each later field only while the payload still has it, and raises the
/// typed field-count error below the required count.
/// </para>
/// <para>
/// "Nullable" is the same test every generator already uses to pick the nullable read path
/// (<see cref="IonType.IsMaybe"/>), so a field that can be absent is exactly a field whose reader
/// can already produce null.
/// </para>
/// </remarks>
public static class IonWireShape
{
    /// <summary>
    /// How many leading items a payload must carry: the index of the last non-nullable item plus
    /// one, or 0 when every item is nullable.
    /// </summary>
    /// <remarks>
    /// Not the number of non-nullable items: in <c>{ a: i4; b: string?; c: i4; }</c> it is 3,
    /// because a two-item payload has nothing to put in <c>c</c>.
    /// </remarks>
    public static int RequiredCount(IEnumerable<ITypeWithName> items)
    {
        var required = 0;
        var position = 0;

        foreach (var item in items)
        {
            position++;
            if (!IsNullable(item.Type))
                required = position;
        }

        return required;
    }

    /// <summary>Whether an absent value of this type reads as null.</summary>
    public static bool IsNullable(IonType type) => type.IsMaybe;
}
