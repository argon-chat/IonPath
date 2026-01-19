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
        return WrapArray(inner);
    }

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
}

/// <summary>
/// C# реализация type resolver.
/// </summary>
public sealed class CSharpTypeNameResolver : TypeNameResolverBase
{
    private static readonly Dictionary<string, string> PrimitiveMap = new()
    {
        ["void"] = "void",
        ["bool"] = "bool",
        ["i1"] = "i1",
        ["i2"] = "i2",
        ["i4"] = "i4",
        ["i8"] = "i8",
        ["i16"] = "i16",
        ["u1"] = "u1",
        ["u2"] = "u2",
        ["u4"] = "u4",
        ["u8"] = "u8",
        ["u16"] = "u16",
        ["f2"] = "f2",
        ["f4"] = "f4",
        ["f8"] = "f8",
        ["string"] = "string",
        ["guid"] = "guid",
        ["datetime"] = "datetime",
        ["dateonly"] = "dateonly",
        ["timeonly"] = "timeonly",
        ["duration"] = "duration",
        ["bigint"] = "BigInteger",
        ["uri"] = "Uri",
    };

    public override string ResolvePrimitive(string ionTypeName)
        => PrimitiveMap.GetValueOrDefault(ionTypeName, ionTypeName);

    public override string WrapNullable(string typeName) => $"{typeName}?";

    public override string WrapArray(string typeName) => $"{ArrayWrapperName}<{typeName}>";

    public override string FormatGeneric(string baseName, IEnumerable<string> typeArgs)
        => $"{baseName}<{string.Join(", ", typeArgs)}>";

    public override string ResolveFormatterRef(IonType type)
    {
        if (type.IsVoid)
            throw new InvalidOperationException("Cannot get formatter ref for void type");
        return $"IonFormatterStorage<{Resolve(type)}>";
    }
}

/// <summary>
/// TypeScript реализация type resolver.
/// </summary>
public sealed class TypeScriptTypeNameResolver : TypeNameResolverBase
{
    private static readonly Dictionary<string, string> PrimitiveMap = new()
    {
        ["void"] = "void",
        ["bool"] = "boolean",
        ["i1"] = "number",
        ["i2"] = "number",
        ["i4"] = "number",
        ["i8"] = "bigint",
        ["i16"] = "bigint",
        ["u1"] = "number",
        ["u2"] = "number",
        ["u4"] = "number",
        ["u8"] = "bigint",
        ["u16"] = "bigint",
        ["f2"] = "number",
        ["f4"] = "number",
        ["f8"] = "number",
        ["string"] = "string",
        ["guid"] = "Guid",
        ["datetime"] = "DateTimeOffset",
        ["dateonly"] = "DateOnly",
        ["timeonly"] = "TimeOnly",
        ["duration"] = "Duration",
        ["bigint"] = "bigint",
        ["uri"] = "string",
    };

    public override string ResolvePrimitive(string ionTypeName)
        => PrimitiveMap.GetValueOrDefault(ionTypeName, ionTypeName);

    public override string WrapNullable(string typeName) => $"{typeName} | null";

    public override string WrapArray(string typeName) => $"{ArrayWrapperName}<{typeName}>";

    public override string FormatGeneric(string baseName, IEnumerable<string> typeArgs)
        => $"{baseName}<{string.Join(", ", typeArgs)}>";

    public override string ResolveFormatterRef(IonType type)
    {
        if (type.IsVoid)
            throw new InvalidOperationException("Cannot get formatter ref for void type");
        var resolved = Resolve(type);
        return $"IonFormatterStorage.get<{resolved}>('{resolved}')";
    }

    protected override string ResolveMaybe(IonGenericType maybe)
    {
        var inner = Resolve(maybe.TypeArguments[0]);
        return UseMaybeWrapper ? FormatGeneric(MaybeWrapperName, [inner]) : $"{inner} | null";
    }
}
