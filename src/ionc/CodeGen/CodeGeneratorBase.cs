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

        foreach (var type in TypedefsFirst(module.Definitions.Where(t => !t.IsUnionCase && !t.IsUnion)))
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

        foreach (var type in TypedefsFirst(allTypes.Where(t => !t.IsUnionCase && !t.IsUnion)))
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
            // A typedef is erased before it reaches the wire, so nothing ever serializes one.
            // Emitting a formatter for it would produce a 1-element CBOR array — exactly the
            // wire overhead a transparent alias is defined not to have.
            .Where(t => !IsTypedefDeclaration(t))
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
            // The typedef arm must precede the IonGenericType arm: a generic definition is
            // skipped, so a generic sitting in front would swallow typedefs silently.
            _ when IsTypedefDeclaration(type) => GenerateTypedef(type),
            IonGenericType => null, // Skip generic definitions
            _ => GenerateMessage(type)
        };
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a typedef <em>declaration</em>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>isTypedef</c> alone is not enough on either side:
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="IonArray"/> propagates <c>isTypedef</c> from its element type, so an array
    /// of a typedef reports <c>isTypedef: true</c> and would be routed to
    /// <see cref="GenerateTypedef"/> and emitted as an alias of its own element.</item>
    /// <item>Every builtin in the std module is declared with <c>isTypedef: true</c> — see the
    /// <c>new("u4", [...], [], true)</c> entries in <c>IonModule.GetStdModule</c>, where the flag
    /// is the fourth positional argument. Requiring the single <c>Value</c> field that
    /// <c>TransformStage.CompileTypedefs</c> always emits separates a real alias from those.</item>
    /// </list>
    /// </remarks>
    protected static bool IsTypedefDeclaration(IonType type)
        => type is { isTypedef: true, fields.Count: > 0 } and not IonArray and not IonUnresolvedType;

    /// <summary>
    /// Orders a type list for emission: typedefs first, everything else in its original order.
    /// </summary>
    /// <remarks>
    /// Keeps an alias ahead of the declarations that mention it. <c>OrderBy</c> is stable, so the
    /// relative order inside each group is untouched.
    /// </remarks>
    protected static IEnumerable<IonType> TypedefsFirst(IEnumerable<IonType> types)
        => types.OrderBy(t => IsTypedefDeclaration(t) ? 0 : 1);

    protected virtual string GenerateEnum(IonEnum e)
    {
        var members = e.members.Select(m => new EnumMember(
            m.name.Identifier,
            FormatEnumValue(m.constantValue, m.type),
            m.Doc,
            AttributeEmission.DeprecationOf(m.attributes)
        ));
        return Emitter.EnumDeclaration(e.name.Identifier, members, null, e.Doc,
            AttributeEmission.DeprecationOf(e.attributes));
    }

    protected virtual string GenerateFlags(IonFlags f)
    {
        var members = f.members.Select(m => new EnumMember(
            m.name.Identifier,
            FormatEnumValue(m.constantValue, m.type),
            m.Doc,
            AttributeEmission.DeprecationOf(m.attributes)
        ));
        return Emitter.FlagsDeclaration(
            f.name.Identifier,
            TypeResolver.ResolvePrimitive(f.baseType.name.Identifier),
            members,
            f.Doc,
            AttributeEmission.DeprecationOf(f.attributes)
        );
    }

    protected virtual string GenerateTypedef(IonType type)
    {
        var underlying = type.fields.FirstOrDefault()?.type;
        var underlyingName = underlying != null ? TypeResolver.Resolve(underlying) : "object";
        return Emitter.TypedefDeclaration(type.name.Identifier, underlyingName, type.Doc);
    }

    protected virtual string GenerateMessage(IonType type)
    {
        var fields = type.fields.Select(f => new FieldDecl(
            f.name.Identifier,
            TypeResolver.Resolve(f.type),
            Doc: f.Doc,
            Deprecated: AttributeEmission.DeprecationOf(f.attributes)
        ));
        return Emitter.MessageDeclaration(type.name.Identifier, fields, type.Doc,
            AttributeEmission.DeprecationOf(type.attributes));
    }

    protected virtual string GenerateService(IonService service)
    {
        var methods = BuildMethodDecls(service);

        return Emitter.ServiceInterfaceDeclaration(
            $"I{service.name.Identifier}",
            methods,
            "IIonService",
            service.Doc,
            AttributeEmission.DeprecationOf(service.attributes)
        );
    }

    /// <summary>
    /// Builds the emitter-level method models for a service, carrying documentation
    /// from the semantic model onto the methods and their parameters.
    /// </summary>
    protected List<MethodDecl> BuildMethodDecls(IonService service)
        => service.methods.Select(m => new MethodDecl(
            m.name.Identifier,
            ResolveReturnType(m),
            m.arguments.Select(a => new ParameterDecl(
                a.name.Identifier,
                ResolveArgumentType(a),
                a.mod == IonArgumentModifiers.Stream,
                Doc: a.Doc
            )).ToList(),
            m.IsStreamable ? MethodModifiers.Stream : MethodModifiers.Async,
            FormatAttributes(m.attributes),
            m.Doc,
            AttributeEmission.DeprecationOf(m.attributes)
        )).ToList();

    /// <summary>
    /// Renders a declaration's attributes into the target language's annotation syntax, dropping
    /// the ones that language cannot express.
    /// </summary>
    protected List<string> FormatAttributes(IReadOnlyList<IonAttributeInstance> attributes)
        => attributes.Select(FormatAttribute).OfType<string>().ToList();

    // ═══════════════════════════════════════════════════════════════════
    // UNION GENERATION
    // ═══════════════════════════════════════════════════════════════════

    protected virtual string GenerateUnion(IonUnion union)
    {
        var sb = new StringBuilder();

        // Generate base interface/class
        var sharedFields = union.sharedFields?.Select(f => new FieldDecl(
            f.Name.Identifier,
            TypeResolver.Resolve(f.type),
            Doc: f.Doc
        ));

        sb.AppendLine(Emitter.UnionBaseDeclaration(
            union.name.Identifier,
            union.types.Select(t => t.name.Identifier),
            sharedFields,
            union.Doc,
            AttributeEmission.DeprecationOf(union.attributes)
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
                    TypeResolver.Resolve(f.type),
                    Doc: f.Doc,
                    Deprecated: AttributeEmission.DeprecationOf(f.attributes)
                ));

                sb.AppendLine();
                sb.AppendLine(Emitter.UnionCaseDeclaration(
                    caseType.name.Identifier,
                    union.name.Identifier,
                    index,
                    fields,
                    caseType.Doc,
                    AttributeEmission.DeprecationOf(caseType.attributes)
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

    /// <summary>
    /// One attribute use in the target language's annotation syntax, or <see langword="null"/> when
    /// that language has no way to express it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is the C# form, kept as the base behaviour for any future C#-family emitter.
    /// No emitter currently renders <c>MethodDecl.Attributes</c>: the only subclass left, Rust,
    /// overrides this to <see langword="null"/> because it cannot carry a
    /// general annotation (see <see cref="AttributeEmission"/>), and expresses
    /// <c>@deprecated</c> through its own native form instead. The shipping C# output does not
    /// come through here at all — <c>IonCSharpGenerator</c> builds its attributes directly via
    /// <see cref="AttributeEmission.CSharpAttributes"/>.
    /// </para>
    /// <para>
    /// This used to <c>string.Join</c> the argument values raw. With the current
    /// <see cref="IonAttributeInstance.arguments"/> contract — always exactly as long as the
    /// declaration's parameter list, with an omitted trailing optional present as an explicit
    /// <see langword="null"/> — that produced <c>Cache(30, users, )</c>: an unquoted string and an
    /// empty slot. <see cref="AttributeEmission.CSharpAttributes"/> owns the quoting, the trailing
    /// null trimming and the std name mapping, so every generator agrees.
    /// </para>
    /// </remarks>
    protected virtual string? FormatAttribute(IonAttributeInstance attr)
        => AttributeEmission.CSharpAttributes([attr]).FirstOrDefault();

    /// <summary>
    /// Collects the module ('//!') documentation for a whole compilation into one text.
    /// <para>
    /// <see cref="CompilationContext.ProcessedModules"/> can list the same module more than
    /// once (rebuilt modules are appended by the restore stage), so this deduplicates by doc
    /// text rather than by module instance. Returns <c>null</c> when nothing is documented.
    /// </para>
    /// </summary>
    protected static string? CollectModuleDocs(CompilationContext context)
    {
        var docs = context.ProcessedModules
            .Select(m => m.Doc)
            .Where(d => !DocCommentFormatter.IsEmpty(d))
            .Select(d => d!)
            .Distinct()
            .ToList();

        return docs.Count == 0 ? null : string.Join("\n\n", docs);
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
