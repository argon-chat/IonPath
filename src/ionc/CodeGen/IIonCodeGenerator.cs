namespace ion.compiler.CodeGen;

using ion.runtime;
using ion.syntax;

/// <summary>
/// Унифицированный интерфейс для кодогенераторов Ion.
/// Поддерживает генерацию для разных целевых языков (C#, TypeScript, Rust).
/// </summary>
public interface IIonCodeGenerator
{
    /// <summary>
    /// Генерирует заголовок файла.
    /// </summary>
    string FileHeader();

    /// <summary>
    /// Генерирует файл проекта (.csproj, package.json, Cargo.toml).
    /// </summary>
    void GenerateProjectFile(string projectName, FileInfo outputFile);

    /// <summary>
    /// Генерирует глобальные типы (type aliases, global usings).
    /// </summary>
    string GenerateGlobalTypes();

    /// <summary>
    /// Генерирует модуль целиком (типы + сервисы).
    /// </summary>
    string GenerateModule(IonModule module);

    /// <summary>
    /// Генерирует только типы (без сервисов).
    /// </summary>
    string GenerateTypes(IEnumerable<IonType> types);

    /// <summary>
    /// Генерирует интерфейсы сервисов.
    /// </summary>
    string GenerateServices(IonModule module);

    /// <summary>
    /// Генерирует форматтеры (сериализаторы/десериализаторы).
    /// </summary>
    string GenerateAllFormatters(IEnumerable<IonType> types);

    /// <summary>
    /// Генерирует module init (регистрация форматтеров).
    /// </summary>
    string GenerateModuleInit(
        IEnumerable<IonType> types,
        IReadOnlyList<IonService> services,
        bool clientToo,
        bool serverToo);

    /// <summary>
    /// Генерирует серверные executor'ы сервисов.
    /// </summary>
    string GenerateAllServiceExecutors(IEnumerable<IonService> services);

    /// <summary>
    /// Генерирует клиентские реализации сервисов.
    /// </summary>
    string GenerateAllServiceClientImpl(IEnumerable<IonService> services);
}

/// <summary>
/// Method-modifier predicates the code generators branch on.
/// </summary>
/// <remarks>
/// These live here rather than on <see cref="IonMethod"/> itself because the distinction is
/// codegen-only: <c>ion.compiler.runtime</c> is shared with the compiler, which routes and
/// validates an <c>internal</c> method exactly like any other.
/// </remarks>
public static class IonMethodModifierExtensions
{
    /// <summary>
    /// Whether the method carries the <c>internal</c> modifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>internal</c> means <em>not part of the generated client's public API</em>. The method is
    /// still declared on the service interface, still dispatched by the server executor and still
    /// carried on the wire unchanged — only the generated client stops advertising it, so a peer
    /// service compiled alongside the contracts can still call it while an outside consumer of the
    /// client cannot.
    /// </para>
    /// <para>
    /// Each target spells that in its own native visibility: C# uses an explicit interface
    /// implementation (off the concrete client type's surface), Rust drops <c>pub</c> (crate
    /// private), and TypeScript — which cannot
    /// narrow the visibility of a member required by an <c>implements</c> clause — marks it
    /// <c>@internal</c> so it is stripped from the published <c>.d.ts</c>.
    /// </para>
    /// </remarks>
    public static bool IsInternal(this IonMethod method)
        => method.modifiers.Any(m => m is IonMethodModifiers.Internal);
}
