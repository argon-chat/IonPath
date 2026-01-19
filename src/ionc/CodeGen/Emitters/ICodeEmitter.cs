namespace ion.compiler.CodeGen.Emitters;

using ion.runtime;

/// <summary>
/// Абстракция над синтаксисом целевого языка.
/// Каждый метод генерирует фрагмент кода без знания о Ion-специфике.
/// </summary>
public interface ICodeEmitter
{
    /// <summary>
    /// Язык генератора (CSharp, TypeScript, Go, etc.)
    /// </summary>
    string Language { get; }

    /// <summary>
    /// Расширение файла (.cs, .ts, .go)
    /// </summary>
    string FileExtension { get; }

    // ═══════════════════════════════════════════════════════════════════
    // FILE STRUCTURE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Генерирует заголовок файла (auto-generated comment, pragmas, etc.)
    /// </summary>
    string FileHeader(string? @namespace = null);

    /// <summary>
    /// Оборачивает код в namespace/module
    /// </summary>
    string WrapInNamespace(string @namespace, string content);

    // ═══════════════════════════════════════════════════════════════════
    // TYPE DECLARATIONS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Генерирует enum: enum Foo { A = 1, B = 2 }
    /// </summary>
    string EnumDeclaration(string name, IEnumerable<EnumMember> members, EnumOptions? options = null);

    /// <summary>
    /// Генерирует flags enum (с атрибутом [Flags] в C#)
    /// </summary>
    string FlagsDeclaration(string name, string? baseType, IEnumerable<EnumMember> members);

    /// <summary>
    /// Генерирует record/interface для message типа
    /// </summary>
    string MessageDeclaration(string name, IEnumerable<FieldDecl> fields);

    /// <summary>
    /// Генерирует typedef/type alias
    /// </summary>
    string TypedefDeclaration(string name, string underlyingType);

    /// <summary>
    /// Генерирует интерфейс сервиса
    /// </summary>
    string ServiceInterfaceDeclaration(string name, IEnumerable<MethodDecl> methods, string? baseInterface = null);

    /// <summary>
    /// Генерирует класс
    /// </summary>
    string ClassDeclaration(ClassDecl decl);

    // ═══════════════════════════════════════════════════════════════════
    // UNION TYPES
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Генерирует базовый тип union (interface/abstract class)
    /// </summary>
    string UnionBaseDeclaration(string name, IEnumerable<string> caseNames, IEnumerable<FieldDecl>? sharedFields = null);

    /// <summary>
    /// Генерирует case тип union
    /// </summary>
    string UnionCaseDeclaration(string caseName, string unionName, int caseIndex, IEnumerable<FieldDecl> fields);

    // ═══════════════════════════════════════════════════════════════════
    // TYPE NAMES
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Форматирует nullable тип: T? или T | null
    /// </summary>
    string NullableType(string innerType);

    /// <summary>
    /// Форматирует array тип: T[] или Array&lt;T&gt;
    /// </summary>
    string ArrayType(string innerType);

    /// <summary>
    /// Форматирует generic тип: Foo&lt;T, U&gt;
    /// </summary>
    string GenericType(string baseName, IEnumerable<string> typeArgs);

    /// <summary>
    /// Форматирует async return тип: Task&lt;T&gt; или Promise&lt;T&gt;
    /// </summary>
    string AsyncReturnType(string? innerType);

    /// <summary>
    /// Форматирует stream return тип: IAsyncEnumerable&lt;T&gt; или AsyncIterable&lt;T&gt;
    /// </summary>
    string StreamReturnType(string innerType);

    /// <summary>
    /// Форматирует stream input тип для аргументов
    /// </summary>
    string StreamInputType(string innerType);

    // ═══════════════════════════════════════════════════════════════════
    // FORMATTING HELPERS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Возвращает строку отступа для указанного уровня
    /// </summary>
    string Indent(int level);

    /// <summary>
    /// Форматирует идентификатор (escaping keywords, case conversion)
    /// </summary>
    string FormatIdentifier(string name);

    /// <summary>
    /// Форматирует значение enum (с учётом bigint в TS, etc.)
    /// </summary>
    string FormatEnumValue(string value, int? bits = null);

    /// <summary>
    /// Атрибут/декоратор для generated code
    /// </summary>
    string GeneratedCodeAttribute { get; }
}

// ═══════════════════════════════════════════════════════════════════════════
// DECLARATION MODELS
// ═══════════════════════════════════════════════════════════════════════════

public record EnumMember(string Name, string Value);

public record EnumOptions(string? BaseType = null, bool IsFlags = false);

public record FieldDecl(string Name, string Type, bool IsOptional = false);

public record MethodDecl(
    string Name,
    string ReturnType,
    IReadOnlyList<ParameterDecl> Parameters,
    MethodModifiers Modifiers = MethodModifiers.None,
    IReadOnlyList<string>? Attributes = null
);

public record ParameterDecl(
    string Name,
    string Type,
    bool IsStream = false,
    string? DefaultValue = null
);

public record ClassDecl(
    string Name,
    IReadOnlyList<FieldDecl>? Fields = null,
    IReadOnlyList<MethodDecl>? Methods = null,
    IReadOnlyList<string>? Implements = null,
    string? Extends = null,
    ClassModifiers Modifiers = ClassModifiers.None,
    IReadOnlyList<ConstructorParam>? ConstructorParams = null
);

public record ConstructorParam(string Name, string Type, string? DefaultValue = null);

[Flags]
public enum MethodModifiers
{
    None = 0,
    Async = 1,
    Stream = 2,
    Static = 4
}

[Flags]
public enum ClassModifiers
{
    None = 0,
    Sealed = 1,
    Abstract = 2,
    Export = 4
}
