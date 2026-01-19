namespace ion.compiler.CodeGen;

using Emitters;
using ion.runtime;
using ion.syntax;
using Templates;
using System.Text;

/// <summary>
/// Базовый класс для кодогенераторов.
/// Содержит общую логику обхода типов, сервисов и генерации кода.
/// </summary>
public abstract class CodeGeneratorBase : IIonCodeGenerator
{
    protected readonly string Namespace;
    protected readonly ICodeEmitter Emitter;
    protected readonly ITypeNameResolver TypeResolver;
    protected readonly ITemplateProvider Templates;

    protected CodeGeneratorBase(
        string @namespace,
        ICodeEmitter emitter,
        ITypeNameResolver typeResolver,
        ITemplateProvider templates)
    {
        Namespace = @namespace;
        Emitter = emitter;
        TypeResolver = typeResolver;
        Templates = templates;
    }

    // ═══════════════════════════════════════════════════════════════════
    // IIonCodeGenerator IMPLEMENTATION
    // ═══════════════════════════════════════════════════════════════════

    public virtual string FileHeader() => Emitter.FileHeader(Namespace);

    public abstract void GenerateProjectFile(string projectName, FileInfo outputFile);
    public abstract string GenerateGlobalTypes();

    public virtual string GenerateModule(IonModule module)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FileHeader());
        sb.AppendLine();

        foreach (var type in module.Definitions.Where(t => !t.IsUnionCase && !t.IsUnion))
        {
            var generated = GenerateType(type);
            if (!string.IsNullOrEmpty(generated))
            {
                sb.AppendLine(generated);
                sb.AppendLine();
            }
        }

        foreach (var service in module.Services)
        {
            sb.AppendLine(GenerateService(service));
            sb.AppendLine();
        }

        foreach (var union in module.Definitions.OfType<IonUnion>())
        {
            sb.AppendLine(GenerateUnion(union));
            sb.AppendLine();
        }

        return PostProcess(sb.ToString());
    }

    public virtual string GenerateTypes(IEnumerable<IonType> types)
    {
        var sb = new StringBuilder();
        var allTypes = types.ToList();

        foreach (var type in allTypes.Where(t => !t.IsUnionCase && !t.IsUnion))
        {
            var generated = GenerateType(type);
            if (!string.IsNullOrEmpty(generated))
            {
                sb.AppendLine(generated);
                sb.AppendLine();
            }
        }

        foreach (var union in allTypes.OfType<IonUnion>())
        {
            sb.AppendLine(GenerateUnion(union));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public virtual string GenerateServices(IonModule module)
    {
        var sb = new StringBuilder();
        foreach (var service in module.Services)
        {
            sb.AppendLine(GenerateService(service));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public virtual string GenerateAllFormatters(IEnumerable<IonType> types)
    {
        var candidates = types
            .Where(t => !t.IsBuiltin && !t.IsScalar && !t.IsVoid && !t.IsUnionCase && !t.IsUnion)
            .ToList();

        var sorted = TopoSortByDependencies(candidates);
        var sb = new StringBuilder();

        foreach (var t in sorted)
        {
            sb.AppendLine(GenerateFormatter(t));
            sb.AppendLine();
        }

        return PostProcess(sb.ToString());
    }

    public abstract string GenerateModuleInit(
        IEnumerable<IonType> types,
        IReadOnlyList<IonService> services,
        bool clientToo,
        bool serverToo);

    public abstract string GenerateAllServiceExecutors(IEnumerable<IonService> services);

    public abstract string GenerateAllServiceClientImpl(IEnumerable<IonService> services);

    // ═══════════════════════════════════════════════════════════════════
    // TYPE GENERATION
    // ═══════════════════════════════════════════════════════════════════

    protected virtual string? GenerateType(IonType type)
    {
        return type switch
        {
            IonEnum e => GenerateEnum(e),
            IonFlags f => GenerateFlags(f),
            IonGenericType => null, // Skip generic definitions
            _ when type.isTypedef => GenerateTypedef(type),
            _ => GenerateMessage(type)
        };
    }

    protected virtual string GenerateEnum(IonEnum e)
    {
        var members = e.members.Select(m => new EnumMember(
            m.name.Identifier,
            FormatEnumValue(m.constantValue, m.type)
        ));
        return Emitter.EnumDeclaration(e.name.Identifier, members);
    }

    protected virtual string GenerateFlags(IonFlags f)
    {
        var members = f.members.Select(m => new EnumMember(
            m.name.Identifier,
            FormatEnumValue(m.constantValue, m.type)
        ));
        return Emitter.FlagsDeclaration(
            f.name.Identifier,
            TypeResolver.ResolvePrimitive(f.baseType.name.Identifier),
            members
        );
    }

    protected virtual string GenerateTypedef(IonType type)
    {
        var underlying = type.fields.FirstOrDefault()?.type;
        var underlyingName = underlying != null ? TypeResolver.Resolve(underlying) : "object";
        return Emitter.TypedefDeclaration(type.name.Identifier, underlyingName);
    }

    protected virtual string GenerateMessage(IonType type)
    {
        var fields = type.fields.Select(f => new FieldDecl(
            f.name.Identifier,
            TypeResolver.Resolve(f.type)
        ));
        return Emitter.MessageDeclaration(type.name.Identifier, fields);
    }

    protected virtual string GenerateService(IonService service)
    {
        var methods = service.methods.Select(m => new MethodDecl(
            m.name.Identifier,
            ResolveReturnType(m),
            m.arguments.Select(a => new ParameterDecl(
                a.name.Identifier,
                ResolveArgumentType(a),
                a.mod == IonArgumentModifiers.Stream
            )).ToList(),
            m.IsStreamable ? MethodModifiers.Stream : MethodModifiers.Async,
            m.attributes.Select(FormatAttribute).ToList()
        )).ToList();

        return Emitter.ServiceInterfaceDeclaration(
            $"I{service.name.Identifier}",
            methods,
            "IIonService"
        );
    }

    // ═══════════════════════════════════════════════════════════════════
    // UNION GENERATION
    // ═══════════════════════════════════════════════════════════════════

    protected virtual string GenerateUnion(IonUnion union)
    {
        var sb = new StringBuilder();

        // Generate base interface/class
        var sharedFields = union.sharedFields?.Select(f => new FieldDecl(
            f.Name.Identifier,
            TypeResolver.Resolve(f.type)
        ));

        sb.AppendLine(Emitter.UnionBaseDeclaration(
            union.name.Identifier,
            union.types.Select(t => t.name.Identifier),
            sharedFields
        ));

        // Generate case types
        var index = 0;
        var casesForFormatters = new List<IonType>();

        foreach (var caseType in union.types)
        {
            if (caseType.IsUnionCase)
            {
                casesForFormatters.Add(caseType);
                var fields = caseType.fields.Select(f => new FieldDecl(
                    f.name.Identifier,
                    TypeResolver.Resolve(f.type)
                ));

                sb.AppendLine();
                sb.AppendLine(Emitter.UnionCaseDeclaration(
                    caseType.name.Identifier,
                    union.name.Identifier,
                    index,
                    fields
                ));
            }
            index++;
        }

        // Generate union formatter
        sb.AppendLine();
        sb.AppendLine(GenerateUnionFormatter(union));

        // Generate formatters for case types
        foreach (var caseType in casesForFormatters)
        {
            sb.AppendLine();
            sb.AppendLine(GenerateFormatter(caseType, isUnionCase: true));
        }

        return sb.ToString();
    }

    protected abstract string GenerateUnionFormatter(IonUnion union);

    // ═══════════════════════════════════════════════════════════════════
    // FORMATTER GENERATION
    // ═══════════════════════════════════════════════════════════════════

    protected virtual string GenerateFormatter(IonType type, bool isUnionCase = false)
    {
        return type switch
        {
            IonEnum e => GenerateEnumFormatter(e),
            IonFlags f => GenerateFlagsFormatter(f),
            _ => GenerateMessageFormatter(type, isUnionCase)
        };
    }

    protected abstract string GenerateEnumFormatter(IonEnum e);
    protected abstract string GenerateFlagsFormatter(IonFlags f);
    protected abstract string GenerateMessageFormatter(IonType type, bool isUnionCase);

    // ═══════════════════════════════════════════════════════════════════
    // FIELD READ/WRITE GENERATION
    // ═══════════════════════════════════════════════════════════════════

    protected abstract string GenerateReadField(IonField field);
    protected abstract string GenerateWriteField(IonField field, string valuePrefix);
    protected abstract string GenerateReadArgument(IonArgument arg);
    protected abstract string GenerateWriteArgument(IonArgument arg);

    // ═══════════════════════════════════════════════════════════════════
    // HELPER METHODS
    // ═══════════════════════════════════════════════════════════════════

    protected virtual string ResolveReturnType(IonMethod method)
    {
        if (method.returnType.IsVoid)
            return Emitter.AsyncReturnType(null);
        if (method.IsStreamable)
            return Emitter.StreamReturnType(TypeResolver.Resolve(method.returnType));
        return Emitter.AsyncReturnType(TypeResolver.Resolve(method.returnType));
    }

    protected virtual string ResolveArgumentType(IonArgument arg)
    {
        var baseType = TypeResolver.Resolve(arg.type);
        if (arg.mod == IonArgumentModifiers.Stream)
            return Emitter.StreamInputType(baseType);
        return baseType;
    }

    protected virtual string FormatEnumValue(string value, IonType? type)
    {
        var bits = type?.HasBitsAttribute == true ? type.Bits : (int?)null;
        return Emitter.FormatEnumValue(value, bits);
    }

    protected virtual string FormatAttribute(IonAttributeInstance attr)
    {
        if (attr.name.Identifier == "deprecated")
            return "Obsolete";
        var args = attr.arguments.Any()
            ? $"({string.Join(", ", attr.arguments)})"
            : "";
        return $"{attr.name.Identifier}{args}";
    }

    /// <summary>
    /// Топологическая сортировка типов по зависимостям.
    /// </summary>
    protected static IReadOnlyList<IonType> TopoSortByDependencies(IReadOnlyList<IonType> types)
    {
        var byName = types.ToDictionary(t => t.name.Identifier);
        var visited = new HashSet<string>();
        var temp = new HashSet<string>();
        var result = new List<IonType>();

        foreach (var t in types) Visit(t);
        return result;

        void Visit(IonType t)
        {
            var key = t.name.Identifier;
            if (visited.Contains(key)) return;
            if (!temp.Add(key)) return;

            foreach (var f in t.fields ?? [])
            {
                var ft = f.type;
                if (ft is null || ft.IsBuiltin || ft.IsScalar || ft.IsVoid) continue;
                if (byName.TryGetValue(ft.name.Identifier, out var dep))
                    Visit(dep);
            }

            temp.Remove(key);
            visited.Add(key);
            result.Add(t);
        }
    }

    /// <summary>
    /// Генерирует capture field list для конструктора.
    /// </summary>
    protected virtual string GenerateCaptureFields(IonType type)
        => string.Join(", ", type.fields.Select(f => FormatLocalVariableName(f.name.Identifier)));

    protected virtual string GenerateCaptureArgs(IonMethod method, params string[] additional)
        => string.Join(", ", method.arguments
            .Select(a => a.mod == IonArgumentModifiers.Stream
                ? "inputStreamCasted"
                : FormatLocalVariableName(a.name.Identifier))
            .Concat(additional));

    /// <summary>
    /// Форматирует имя локальной переменной (для read операций).
    /// </summary>
    protected abstract string FormatLocalVariableName(string name);

    /// <summary>
    /// Post-processing сгенерированного кода (замена плейсхолдеров и т.д.)
    /// </summary>
    protected virtual string PostProcess(string code) => code;
}

// ═══════════════════════════════════════════════════════════════════════════
// UPDATED INTERFACE
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Capabilities флаги для генератора.
/// </summary>
[Flags]
public enum GeneratorCapabilities
{
    None = 0,
    Types = 1,
    Formatters = 2,
    Client = 4,
    Server = 8,
    ProjectFile = 16,
    ModuleInit = 32,
    ClientProxy = 64,
    All = Types | Formatters | Client | Server | ProjectFile | ModuleInit
}
