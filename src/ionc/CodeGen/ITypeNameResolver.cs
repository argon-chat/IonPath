namespace ion.compiler.CodeGen;

using ion.runtime;

/// <summary>
/// Резолвер имён типов из Ion в целевой язык.
/// Отвечает за маппинг примитивов, generic типов, nullable, arrays.
/// </summary>
public interface ITypeNameResolver
{
    /// <summary>
    /// Резолвит полное имя типа для использования в коде.
    /// Обрабатывает Maybe, Array, generics, unions.
    /// </summary>
    string Resolve(IonType type);

    /// <summary>
    /// Резолвит имя примитивного/скалярного типа.
    /// i4 → int (C#) или number (TS)
    /// </summary>
    string ResolvePrimitive(string ionTypeName);

    /// <summary>
    /// Резолвит имя для union интерфейса (добавляет I prefix где нужно).
    /// </summary>
    string ResolveUnionInterface(IonUnion union);

    /// <summary>
    /// Резолвит имя для formatter storage reference.
    /// </summary>
    string ResolveFormatterRef(IonType type);

    /// <summary>
    /// Оборачивает тип в nullable.
    /// </summary>
    string WrapNullable(string typeName);

    /// <summary>
    /// Оборачивает тип в array.
    /// </summary>
    string WrapArray(string typeName);

    /// <summary>
    /// Форматирует generic тип.
    /// </summary>
    string FormatGeneric(string baseName, IEnumerable<string> typeArgs);

    /// <summary>
    /// Возвращает имя типа без I/prefix для использования в строках.
    /// </summary>
    string GetRawTypeName(IonType type);
}

/// <summary>
/// Базовая реализация с общей логикой.
/// </summary>
public abstract class TypeNameResolverBase : ITypeNameResolver
{
    /// <summary>
    /// Флаг использования Maybe wrapper вместо nullable.
    /// </summary>
    public bool UseMaybeWrapper { get; set; }

    public virtual string Resolve(IonType type)
    {
        return type switch
        {
            IonGenericType { IsMaybe: true } maybe => ResolveMaybe(maybe),
            IonGenericType { IsArray: true } array => ResolveArray(array),
            // Before the ResolveGeneric fallthrough, which names its arguments with
            // `a.name.Identifier` — the raw Ion spelling — and would emit `Map<string, User>`
            // instead of the target's own map type over the target's own key/value types.
            IonGenericType { IsMap: true } map => ResolveMap(map),
            IonGenericType { IsSet: true } set => ResolveSet(set),
            IonGenericType { IsPartial: true } partial => ResolvePartial(partial),
            IonGenericType generic => ResolveGeneric(generic),
            IonUnion union => ResolveUnionInterface(union),
            _ => ResolvePrimitive(type.name.Identifier)
        };
    }

    protected virtual string ResolveMaybe(IonGenericType maybe)
    {
        var inner = Resolve(maybe.TypeArguments[0]);
        return UseMaybeWrapper ? FormatGeneric(MaybeWrapperName, [inner]) : WrapNullable(inner);
    }

    protected virtual string ResolveArray(IonGenericType array)
    {
        var inner = Resolve(array.TypeArguments[0]);
        return array.FixedSize is { } size ? WrapFixedArray(inner, size) : WrapArray(inner);
    }

    /// <summary><c>Map&lt;K, V&gt;</c> in the target's own spelling.</summary>
    protected virtual string ResolveMap(IonGenericType map)
        => FormatGeneric(MapTypeName, [Resolve(map.TypeArguments[0]), Resolve(map.TypeArguments[1])]);

    /// <summary><c>Set&lt;T&gt;</c> in the target's own spelling.</summary>
    protected virtual string ResolveSet(IonGenericType set)
        => FormatGeneric(SetTypeName, [Resolve(set.TypeArguments[0])]);

    /// <summary>
    /// <c>T[N]</c> in the target's own spelling. Defaults to the unsized form, because most
    /// targets have no type that carries a length — Rust's const-generic <c>[T; N]</c> is the
    /// exception and overrides this.
    /// </summary>
    protected virtual string WrapFixedArray(string typeName, int size) => WrapArray(typeName);

    protected virtual string ResolvePartial(IonGenericType partial)
    {
        var inner = Resolve(partial.TypeArguments[0]);
        return FormatGeneric(PartialWrapperName, [inner]);
    }

    protected virtual string ResolveGeneric(IonGenericType generic)
    {
        var typeArgs = generic.TypeArguments.Select(a => a.name.Identifier);
        return FormatGeneric(generic.name.Identifier, typeArgs);
    }

    public virtual string ResolveUnionInterface(IonUnion union) => $"I{union.name.Identifier}";

    public abstract string ResolvePrimitive(string ionTypeName);
    public abstract string ResolveFormatterRef(IonType type);
    public abstract string WrapNullable(string typeName);
    public abstract string WrapArray(string typeName);
    public abstract string FormatGeneric(string baseName, IEnumerable<string> typeArgs);

    public virtual string GetRawTypeName(IonType type)
    {
        if (type is IonUnion union)
            return union.name.Identifier;
        return type.name.Identifier;
    }

    /// <summary>
    /// Имя Maybe wrapper (IonMaybe в C#/TS).
    /// </summary>
    protected virtual string MaybeWrapperName => "IonMaybe";

    /// <summary>
    /// Имя Partial wrapper.
    /// </summary>
    protected virtual string PartialWrapperName => "IonPartial";

    /// <summary>
    /// Имя Array wrapper.
    /// </summary>
    protected virtual string ArrayWrapperName => "IonArray";

    /// <summary>Имя Map типа в целевом языке.</summary>
    protected virtual string MapTypeName => "Map";

    /// <summary>Имя Set типа в целевом языке.</summary>
    protected virtual string SetTypeName => "Set";
}

/// <summary>
/// Rust реализация type resolver.
/// </summary>
public sealed class RustTypeNameResolver : TypeNameResolverBase
{
    private static readonly Dictionary<string, string> PrimitiveMap = new()
    {
        ["void"] = "()",
        ["bool"] = "bool",
        ["i1"] = "i8",
        ["i2"] = "i16",
        ["i4"] = "i32",
        ["i8"] = "i64",
        ["i16"] = "i128",
        ["u1"] = "u8",
        ["u2"] = "u16",
        ["u4"] = "u32",
        ["u8"] = "u64",
        ["u16"] = "u128",
        ["f2"] = "ion_rustcore::IonF16",
        ["f4"] = "f32",
        ["f8"] = "f64",
        ["string"] = "String",
        ["bytes"] = "ion_rustcore::IonBytes",
        ["guid"] = "uuid::Uuid",
        // Unchanged by the `datetime` wire-format correction, and deliberately so: only the
        // *encoding* moved (from a bare `[ticks, offset_minutes]` array to tag 0 + RFC 3339).
        // `chrono::DateTime<FixedOffset>` already carries the offset as part of the value, which
        // is exactly what the new format requires, so this is the one target that needed no
        // remapping. `ion_rustcore` implements IonFormat for it directly.
        ["datetime"] = "chrono::DateTime<chrono::FixedOffset>",
        ["dateonly"] = "ion_rustcore::IonDateOnly",
        ["timeonly"] = "ion_rustcore::IonTimeOnly",
        ["duration"] = "ion_rustcore::IonDuration",
        // Exact base-10 decimal (CBOR tag 4). `ion_rustcore::IonDecimal` is dependency-free —
        // an i32 exponent and an i128 mantissa — so no `rust_decimal` dependency is pulled into
        // the generated crate.
        ["decimal"] = "ion_rustcore::IonDecimal",
        ["bigint"] = "i128",
        ["uri"] = "String",
    };

    public override string ResolvePrimitive(string ionTypeName)
        => PrimitiveMap.GetValueOrDefault(ionTypeName, ionTypeName);

    public override string ResolveUnionInterface(IonUnion union) => union.name.Identifier;

    public override string WrapNullable(string typeName) => $"Option<{typeName}>";

    public override string WrapArray(string typeName) => $"Vec<{typeName}>";

    public override string FormatGeneric(string baseName, IEnumerable<string> typeArgs)
        => $"{baseName}<{string.Join(", ", typeArgs)}>";

    public override string ResolveFormatterRef(IonType type)
    {
        if (type.IsVoid)
            throw new InvalidOperationException("Cannot get formatter ref for void type");
        var resolved = Resolve(type);
        return $"<{resolved} as IonFormat>::ion_read(d)?";
    }

    /// <summary>
    /// Returns the write expression for a given type.
    /// </summary>
    public string ResolveWriteRef(IonType type, string valueExpr)
    {
        var resolved = Resolve(type);
        return $"{valueExpr}.ion_write(e)?;";
    }

    protected override string MaybeWrapperName => "ion_rustcore::IonMaybe";

    /// <summary>
    /// <c>Partial&lt;T&gt;</c> resolves to <c>ion_rustcore::IonPartial&lt;T&gt;</c>, the type alias
    /// <c>&lt;T as IonPartialSchema&gt;::Patch</c> — i.e. the generated <c>TPatch</c> struct.
    /// </summary>
    /// <remarks>
    /// This used to be <c>Option</c>, which is semantic loss rather than a shape mismatch:
    /// <c>Option&lt;T&gt;</c> is one presence bit for the whole message and cannot express
    /// per-field modified/cleared/untouched at all.
    /// </remarks>
    protected override string PartialWrapperName => "ion_rustcore::IonPartial";

    protected override string ArrayWrapperName => "Vec";

    /// <summary>
    /// <c>T[N]</c> becomes the const-generic array <c>[T; N]</c>.
    /// </summary>
    /// <remarks>
    /// Rust is the one target whose type system can carry the declared length, so it is the one
    /// target where a wrong length is a compile error rather than a decode error. The blanket
    /// <c>impl&lt;T: IonFormat, const N: usize&gt; IonFormat for [T; N]</c> in
    /// <c>ion.rustcore/src/formatter.rs</c> then routes reads and writes through
    /// <c>read_fixed_array::&lt;T&gt;(d, N)</c> with no call-site plumbing at all — which is why the
    /// Rust generator needs no fixed-array read/write arms, only this name.
    /// </remarks>
    protected override string WrapFixedArray(string typeName, int size) => $"[{typeName}; {size}]";

    /// <summary>
    /// <c>HashMap</c>/<c>HashSet</c>, not their <c>BTree</c> siblings. <c>ion.rustcore</c> blanket
    /// impls <c>IonFormat</c> for both pairs, and the ordering on the wire is canonical CBOR
    /// either way, so the choice is about the key bound only: <c>HashMap</c> asks for
    /// <c>Eq + Hash</c>, which every legal Ion key type satisfies, while <c>BTreeMap</c> would ask
    /// for <c>Ord</c> — which a generated enum does not derive.
    /// </summary>
    protected override string MapTypeName => "std::collections::HashMap";

    /// <inheritdoc cref="MapTypeName"/>
    protected override string SetTypeName => "std::collections::HashSet";
}
