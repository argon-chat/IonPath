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
    string EnumDeclaration(string name, IEnumerable<EnumMember> members, EnumOptions? options = null, string? doc = null);

    /// <summary>
    /// Генерирует flags enum (с атрибутом [Flags] в C#)
    /// </summary>
    string FlagsDeclaration(string name, string? baseType, IEnumerable<EnumMember> members, string? doc = null);

    /// <summary>
    /// Генерирует record/interface для message типа
    /// </summary>
    string MessageDeclaration(string name, IEnumerable<FieldDecl> fields, string? doc = null);

    /// <summary>
    /// Генерирует typedef/type alias
    /// </summary>
    string TypedefDeclaration(string name, string underlyingType, string? doc = null);

    /// <summary>
    /// Генерирует интерфейс сервиса
    /// </summary>
    string ServiceInterfaceDeclaration(string name, IEnumerable<MethodDecl> methods, string? baseInterface = null, string? doc = null);

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
    string UnionBaseDeclaration(string name, IEnumerable<string> caseNames, IEnumerable<FieldDecl>? sharedFields = null, string? doc = null);

    /// <summary>
    /// Генерирует case тип union
    /// </summary>
    string UnionCaseDeclaration(string caseName, string unionName, int caseIndex, IEnumerable<FieldDecl> fields, string? doc = null);

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

    // ═══════════════════════════════════════════════════════════════════
    // DOCUMENTATION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Форматирует документационный комментарий для целевого языка
    /// (XML doc в C#, JSDoc в TypeScript, rustdoc в Rust, doc comment в Go).
    /// <para>
    /// Возвращает <c>""</c> когда документации нет — ни пустой строки, ни маркера,
    /// ни лишнего перевода строки. Иначе возвращает блок, в котором каждая строка
    /// начинается с <paramref name="indent"/> и который заканчивается переводом строки,
    /// так что вызывающий код может просто сделать <c>sb.Append(...)</c> без проверок.
    /// </para>
    /// </summary>
    /// <param name="doc">Сырой текст документации (строки разделены '\n'), либо <c>null</c>.</param>
    /// <param name="indent">Отступ, соответствующий окружающему сгенерированному коду.</param>
    /// <param name="parameters">Документированные параметры (<c>&lt;param&gt;</c> / <c>@param</c>).</param>
    /// <param name="identifier">
    /// Имя объявления. Используется только Go, где по конвенции комментарий начинается с имени.
    /// </param>
    string DocComment(
        string? doc,
        string indent = "",
        IReadOnlyList<DocParam>? parameters = null,
        string? identifier = null);

    /// <summary>
    /// Форматирует документацию уровня файла/модуля
    /// (<c>//</c> в C#, JSDoc в TypeScript, <c>//!</c> в Rust, package doc в Go).
    /// Возвращает <c>""</c> когда документации нет.
    /// </summary>
    string ModuleDocComment(string? doc, string? name = null);
}

// ═══════════════════════════════════════════════════════════════════════════
// DECLARATION MODELS
// ═══════════════════════════════════════════════════════════════════════════

public record EnumMember(string Name, string Value, string? Doc = null);

public record EnumOptions(string? BaseType = null, bool IsFlags = false);

public record FieldDecl(string Name, string Type, bool IsOptional = false, string? Doc = null);

public record MethodDecl(
    string Name,
    string ReturnType,
    IReadOnlyList<ParameterDecl> Parameters,
    MethodModifiers Modifiers = MethodModifiers.None,
    IReadOnlyList<string>? Attributes = null,
    string? Doc = null
)
{
    /// <summary>
    /// Documented parameters in signature order, for <c>&lt;param&gt;</c>/<c>@param</c> emission.
    /// </summary>
    public IReadOnlyList<DocParam> DocParams
        => Parameters.Select(p => new DocParam(p.Name, p.Doc)).ToList();
}

public record ParameterDecl(
    string Name,
    string Type,
    bool IsStream = false,
    string? DefaultValue = null,
    string? Doc = null
);

public record ClassDecl(
    string Name,
    IReadOnlyList<FieldDecl>? Fields = null,
    IReadOnlyList<MethodDecl>? Methods = null,
    IReadOnlyList<string>? Implements = null,
    string? Extends = null,
    ClassModifiers Modifiers = ClassModifiers.None,
    IReadOnlyList<ConstructorParam>? ConstructorParams = null,
    string? Doc = null
);

public record ConstructorParam(string Name, string Type, string? DefaultValue = null, string? Doc = null);

/// <summary>
/// Helpers shared by the emitters for turning declaration models into <see cref="DocParam"/> lists.
/// </summary>
public static class DeclDocEx
{
    public static IReadOnlyList<DocParam> ToDocParams(this IEnumerable<FieldDecl> fields)
        => fields.Select(f => new DocParam(f.Name, f.Doc)).ToList();

    public static IReadOnlyList<DocParam> ToDocParams(this IEnumerable<ConstructorParam> parameters)
        => parameters.Select(p => new DocParam(p.Name, p.Doc)).ToList();
}

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
