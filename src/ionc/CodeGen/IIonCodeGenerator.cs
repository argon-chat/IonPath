namespace ion.compiler.CodeGen;

using ion.runtime;

/// <summary>
/// Унифицированный интерфейс для кодогенераторов Ion.
/// Поддерживает генерацию для разных целевых языков (C#, TypeScript, Go).
/// </summary>
public interface IIonCodeGenerator
{
    /// <summary>
    /// Генерирует заголовок файла.
    /// </summary>
    string FileHeader();

    /// <summary>
    /// Генерирует файл проекта (.csproj, package.json, go.mod).
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
