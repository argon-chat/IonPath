namespace ion.compiler.CodeGen;

using ion.runtime;

/// <summary>
/// Go реализация type resolver.
/// Маппинг Ion типов в Go типы.
/// </summary>
public sealed class GoTypeNameResolver : TypeNameResolverBase
{
    private static readonly Dictionary<string, string> PrimitiveMap = new()
    {
        ["void"] = "",
        ["bool"] = "bool",
        ["i1"] = "int8",
        ["i2"] = "int16",
        ["i4"] = "int32",
        ["i8"] = "int64",
        ["i16"] = "big.Int", // Go doesn't have int128 natively
        ["u1"] = "uint8",
        ["u2"] = "uint16",
        ["u4"] = "uint32",
        ["u8"] = "uint64",
        ["u16"] = "big.Int", // Go doesn't have uint128 natively
        ["f2"] = "float32", // Go doesn't have float16, use float32
        ["f4"] = "float32",
        ["f8"] = "float64",
        ["string"] = "string",
        ["guid"] = "uuid.UUID",
        ["datetime"] = "time.Time",
        ["dateonly"] = "ionwebcore.DateOnly",
        ["timeonly"] = "ionwebcore.TimeOnly",
        ["duration"] = "ionwebcore.Duration",
        ["bigint"] = "*big.Int",
        ["uri"] = "string",
    };

    public override string ResolvePrimitive(string ionTypeName)
        => PrimitiveMap.GetValueOrDefault(ionTypeName, ionTypeName);

    public override string WrapNullable(string typeName)
    {
        // Go uses pointers for nullable types
        // If already a pointer or slice, don't double-wrap
        if (typeName.StartsWith("*") || typeName.StartsWith("[]"))
            return typeName;
        return $"*{typeName}";
    }

    public override string WrapArray(string typeName) => $"[]{typeName}";

    public override string FormatGeneric(string baseName, IEnumerable<string> typeArgs)
        => $"{baseName}[{string.Join(", ", typeArgs)}]";

    public override string ResolveFormatterRef(IonType type)
    {
        if (type.IsVoid)
            throw new InvalidOperationException("Cannot get formatter ref for void type");

        var resolved = Resolve(type);
        // Go uses generic Read/Write functions
        return $"ionwebcore.Read[{resolved}]";
    }

    public override string ResolveUnionInterface(IonUnion union) => $"I{union.name.Identifier}";

    protected override string ResolveMaybe(IonGenericType maybe)
    {
        var inner = Resolve(maybe.TypeArguments[0]);
        return UseMaybeWrapper
            ? FormatGeneric("ionwebcore.IonMaybe", [inner])
            : WrapNullable(inner);
    }

    protected override string ResolveArray(IonGenericType array)
    {
        var inner = Resolve(array.TypeArguments[0]);
        return WrapArray(inner);
    }

    /// <summary>
    /// Имя Maybe wrapper в Go.
    /// </summary>
    protected override string MaybeWrapperName => "ionwebcore.IonMaybe";

    /// <summary>
    /// Имя Array wrapper в Go (Go uses slices natively).
    /// </summary>
    protected override string ArrayWrapperName => ""; // Go uses []T syntax
}
