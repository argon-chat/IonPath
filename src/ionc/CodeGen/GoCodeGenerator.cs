namespace ion.compiler.CodeGen;

using Emitters;
using ion.runtime;
using ion.syntax;
using Templates;
using System.Text;

/// <summary>
/// Go generator inheriting from CodeGeneratorBase.
/// Generates code for ion.server.go and ion.webcore.go SDK.
/// </summary>
public sealed class GoCodeGenerator : CodeGeneratorBase
{
    private readonly string _packageName;

    public GoCodeGenerator(string @namespace)
        : base(
            @namespace,
            new GoEmitter(),
            new GoTypeNameResolver(),
            new GoTemplateProvider())
    {
        _packageName = @namespace.ToLowerInvariant().Replace(".", "").Replace("-", "");
    }

    public bool UseMaybeWrapper
    {
        get => ((GoTypeNameResolver)TypeResolver).UseMaybeWrapper;
        set => ((GoTypeNameResolver)TypeResolver).UseMaybeWrapper = value;
    }

    // ═══════════════════════════════════════════════════════════════════
    // SINGLE FILE GENERATION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates all Go code in a single file: types, formatters, services, routers, clients.
    /// </summary>
    public string GenerateSingleFile(
        CompilationContext context,
        bool includeServer,
        bool includeClient)
    {
        var sb = new StringBuilder();

        // Header and imports
        sb.AppendLine(FileHeader());
        sb.AppendLine();
        sb.AppendLine("import (");
        sb.AppendLine("\t\"context\"");
        sb.AppendLine("\t\"fmt\"");
        sb.AppendLine();
        sb.AppendLine("\tcbor \"github.com/argon-chat/cbor.go\"");
        if (includeServer)
            sb.AppendLine("\tionserver \"github.com/argon-chat/ion.server.go\"");
        sb.AppendLine("\tionwebcore \"github.com/argon-chat/ion.webcore.go\"");
        sb.AppendLine("\t\"github.com/google/uuid\"");
        sb.AppendLine(")");
        sb.AppendLine();

        // Suppress unused import warnings
        sb.AppendLine("// Suppress unused import warnings");
        sb.AppendLine("var (");
        sb.AppendLine("\t_ = context.Background");
        sb.AppendLine("\t_ = fmt.Sprintf");
        sb.AppendLine("\t_ = cbor.NewCborWriter");
        if (includeServer)
            sb.AppendLine("\t_ = ionserver.New");
        sb.AppendLine("\t_ = ionwebcore.Read[int]");
        sb.AppendLine("\t_ = uuid.Nil");
        sb.AppendLine(")");
        sb.AppendLine();

        // Collect all types and services
        var allTypes = context.ProcessedModules
            .SelectMany(m => m.Definitions)
            .DistinctBy(t => t.name.Identifier)
            .ToList();

        var allServices = context.ProcessedModules
            .SelectMany(m => m.Services)
            .DistinctBy(s => s.name.Identifier)
            .ToList();

        // TYPES
        sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
        sb.AppendLine("// TYPES");
        sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
        sb.AppendLine();

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

        // SERVICE INTERFACES
        if (allServices.Any())
        {
            sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
            sb.AppendLine("// SERVICE INTERFACES");
            sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
            sb.AppendLine();

            foreach (var service in allServices)
            {
                sb.AppendLine(GenerateService(service));
                sb.AppendLine();
            }
        }

        // FORMATTERS
        sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
        sb.AppendLine("// FORMATTERS");
        sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
        sb.AppendLine();

        var formatterTypes = allTypes
            .Where(t => !t.IsBuiltin && !t.IsScalar && !t.IsVoid && !t.IsUnionCase && !t.IsUnion)
            .ToList();

        var sorted = TopoSortByDependencies(formatterTypes);

        // Generate single init() with all formatter registrations
        sb.AppendLine("func init() {");
        foreach (var t in sorted)
        {
            sb.AppendLine(GenerateFormatterRegistration(t));
            sb.AppendLine();
        }
        foreach (var union in allTypes.OfType<IonUnion>())
        {
            sb.AppendLine(GenerateUnionFormatterRegistration(union));
            sb.AppendLine();
        }
        sb.AppendLine("}");
        sb.AppendLine();

        // SERVER ROUTERS
        if (includeServer && allServices.Any())
        {
            sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
            sb.AppendLine("// SERVICE ROUTERS");
            sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
            sb.AppendLine();

            foreach (var service in allServices)
            {
                sb.AppendLine(GenerateServiceRouter(service));
                sb.AppendLine();

                if (service.methods.Any(m => m.IsStreamable))
                {
                    sb.AppendLine(GenerateStreamRouter(service));
                    sb.AppendLine();
                }
            }

            // REGISTRATION HELPERS
            sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
            sb.AppendLine("// REGISTRATION HELPERS");
            sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
            sb.AppendLine();

            foreach (var service in allServices)
            {
                sb.AppendLine(GenerateRegistrationHelper(service));
                sb.AppendLine();
            }

            // SERVICE REGISTRY - unified registration point
            sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
            sb.AppendLine("// SERVICE REGISTRY");
            sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine(GenerateServiceRegistry(allServices));
            sb.AppendLine();
        }

        // CLIENT IMPLEMENTATIONS
        if (includeClient && allServices.Any())
        {
            sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
            sb.AppendLine("// SERVICE CLIENTS");
            sb.AppendLine("// ═══════════════════════════════════════════════════════════════════");
            sb.AppendLine();

            foreach (var service in allServices)
            {
                sb.AppendLine(GenerateServiceClient(service));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // PROJECT FILE
    // ═══════════════════════════════════════════════════════════════════

    public override void GenerateProjectFile(string projectName, FileInfo outputFile)
    {
        var goMod = $"""
            module {projectName}

            go 1.21

            require (
            	github.com/argon-chat/cbor.go v0.1.0
            	github.com/argon-chat/ion.server.go v0.1.0
            	github.com/argon-chat/ion.webcore.go v0.1.0
            	github.com/google/uuid v1.6.0
            )
            """;

        File.WriteAllText(outputFile.FullName, goMod);
    }

    public override string GenerateGlobalTypes() => "";

    public override string GenerateModuleInit(
        IEnumerable<IonType> types,
        IReadOnlyList<IonService> services,
        bool clientToo,
        bool serverToo) => "";

    public override string GenerateAllServiceExecutors(IEnumerable<IonService> services) => "";

    public override string GenerateAllServiceClientImpl(IEnumerable<IonService> services) => "";

    /// <summary>
    /// Override to not use IIonService base interface (Go doesn't have it).
    /// </summary>
    protected override string GenerateService(IonService service)
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

        // Go doesn't have IIonService, pass null for baseInterface
        return Emitter.ServiceInterfaceDeclaration(
            $"I{service.name.Identifier}",
            methods,
            null
        );
    }

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE ROUTER GENERATION
    // ═══════════════════════════════════════════════════════════════════

    private string GenerateServiceRouter(IonService service)
    {
        var serviceName = service.name.Identifier;
        var branches = new StringBuilder();

        foreach (var method in service.methods.Where(m => !m.IsStreamable))
        {
            branches.AppendLine(GenerateRouterCase(method, serviceName));
        }

        var ctx = new TemplateContext()
            .Set("serviceName", serviceName)
            .Set("routerBranches", branches.ToString());

        return ctx.Apply(Templates.ServiceExecutorClassTemplate);
    }

    private string GenerateRouterCase(IonMethod method, string serviceName)
    {
        var methodName = method.name.Identifier;
        var readArgs = new StringBuilder();
        var captureArgs = new List<string>();

        // ctx first in Go convention
        captureArgs.Add("context.Background()");

        foreach (var arg in method.arguments.Where(a => a.mod != IonArgumentModifiers.Stream))
        {
            var varName = FormatLocalVariableName(arg.name.Identifier);
            var typeName = TypeResolver.Resolve(arg.type);

            readArgs.AppendLine($"\t\t{varName}, err := ionwebcore.Read[{typeName}](reader)");
            readArgs.AppendLine("\t\tif err != nil {");
            readArgs.AppendLine("\t\t\treturn err");
            readArgs.AppendLine("\t\t}");

            captureArgs.Add(varName);
        }

        var template = method.returnType.IsVoid
            ? Templates.ServiceExecutorMethodVoidTemplate
            : Templates.ServiceExecutorMethodTemplate;

        var ctx = new TemplateContext()
            .Set("methodName", methodName)
            .Set("readArgs", readArgs.ToString().TrimEnd())
            .Set("captureArgs", string.Join(", ", captureArgs));

        if (!method.returnType.IsVoid)
        {
            ctx.Set("returnType", TypeResolver.Resolve(method.returnType));
        }

        return ctx.Apply(template);
    }

    private string GenerateStreamRouter(IonService service)
    {
        var serviceName = service.name.Identifier;
        var streamMethods = service.methods.Where(m => m.IsStreamable).ToList();

        var inputAllowedCases = new StringBuilder();
        var streamBranches = new StringBuilder();

        foreach (var method in streamMethods)
        {
            var methodName = method.name.Identifier;
            var hasInputStream = method.arguments.Any(a => a.mod == IonArgumentModifiers.Stream);

            if (hasInputStream)
            {
                inputAllowedCases.AppendLine($"\tcase \"{methodName}\":");
                inputAllowedCases.AppendLine("\t\treturn true");
            }

            streamBranches.AppendLine($"\tcase \"{methodName}\":");
            streamBranches.AppendLine($"\t\treturn r.{FormatLocalVariableName(methodName)}Stream(ctx, initialArgs, inputStream)");
        }

        var ctx = new TemplateContext()
            .Set("serviceName", serviceName)
            .Set("inputStreamAllowedCases", inputAllowedCases.ToString())
            .Set("streamRouterBranches", streamBranches.ToString());

        return ctx.Apply(Templates.ServiceExecutorStreamRouterTemplate);
    }

    private string GenerateRegistrationHelper(IonService service)
    {
        var serviceName = service.name.Identifier;
        var hasStreaming = service.methods.Any(m => m.IsStreamable);

        var sb = new StringBuilder();
        sb.AppendLine($"// Register{serviceName} registers I{serviceName} with the Ion server.");
        sb.AppendLine($"// Usage: Register{serviceName}(ionServer, &My{serviceName}Impl{{}})");
        sb.AppendLine($"func Register{serviceName}(server *ionserver.IonServer, impl I{serviceName}) {{");
        sb.AppendLine($"\trouter := New{serviceName}Router(impl)");
        
        if (hasStreaming)
        {
            sb.AppendLine($"\tstreamRouter := New{serviceName}StreamRouter(impl)");
            sb.AppendLine($"\tserver.RegisterServiceByName(\"I{serviceName}\", impl, router, streamRouter)");
        }
        else
        {
            sb.AppendLine($"\tserver.RegisterServiceByName(\"I{serviceName}\", impl, router, nil)");
        }
        
        sb.AppendLine("}");

        return sb.ToString();
    }

    private string GenerateServiceRegistry(List<IonService> services)
    {
        var sb = new StringBuilder();
        
        // ServiceRegistry struct
        sb.AppendLine("// ServiceRegistry holds all service implementations for registration.");
        sb.AppendLine("type ServiceRegistry struct {");
        foreach (var service in services)
        {
            var serviceName = service.name.Identifier;
            sb.AppendLine($"\t{serviceName} I{serviceName}");
        }
        sb.AppendLine("}");
        sb.AppendLine();

        // RegisterAll function
        sb.AppendLine("// RegisterAll registers all services from the registry with the Ion server.");
        sb.AppendLine("// Usage:");
        sb.AppendLine("//   registry := &ServiceRegistry{");
        foreach (var service in services)
        {
            var serviceName = service.name.Identifier;
            sb.AppendLine($"//       {serviceName}: &My{serviceName}Impl{{}},");
        }
        sb.AppendLine("//   }");
        sb.AppendLine("//   registry.RegisterAll(ionServer)");
        sb.AppendLine("func (r *ServiceRegistry) RegisterAll(server *ionserver.IonServer) {");
        foreach (var service in services)
        {
            var serviceName = service.name.Identifier;
            sb.AppendLine($"\tif r.{serviceName} != nil {{");
            sb.AppendLine($"\t\tRegister{serviceName}(server, r.{serviceName})");
            sb.AppendLine("\t}");
        }
        sb.AppendLine("}");

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE CLIENT GENERATION
    // ═══════════════════════════════════════════════════════════════════

    private string GenerateServiceClient(IonService service)
    {
        var serviceName = service.name.Identifier;
        var methods = new StringBuilder();

        foreach (var method in service.methods)
        {
            methods.AppendLine(GenerateClientMethod(method, serviceName));
        }

        var ctx = new TemplateContext()
            .Set("serviceName", serviceName)
            .Set("methods", methods.ToString());

        return ctx.Apply(Templates.ServiceClientClassTemplate);
    }

    private string GenerateClientMethod(IonMethod method, string serviceName)
    {
        var methodName = method.name.Identifier;
        var argsCount = method.arguments.Count(a => a.mod != IonArgumentModifiers.Stream);

        var methodArgs = method.arguments
            .Where(a => a.mod != IonArgumentModifiers.Stream)
            .Select(a => $"{FormatLocalVariableName(a.name.Identifier)} {TypeResolver.Resolve(a.type)}");

        var writeArgs = new StringBuilder();
        foreach (var arg in method.arguments.Where(a => a.mod != IonArgumentModifiers.Stream))
        {
            var varName = FormatLocalVariableName(arg.name.Identifier);
            writeArgs.AppendLine($"\t_ = ionwebcore.Write(writer, {varName})");
        }

        var template = method.IsStreamable
            ? Templates.ServiceClientMethodStreamTemplate
            : method.returnType.IsVoid
                ? Templates.ServiceClientMethodVoidTemplate
                : Templates.ServiceClientMethodTemplate;

        var ctx = new TemplateContext()
            .Set("serviceName", serviceName)
            .Set("methodName", methodName)
            .Set("argsCount", argsCount.ToString())
            .Set("args", string.Join(", ", methodArgs))
            .Set("writeArgs", writeArgs.ToString().TrimEnd());

        if (!method.returnType.IsVoid)
        {
            ctx.Set("returnType", TypeResolver.Resolve(method.returnType));
        }

        return ctx.Apply(template);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FORMATTER GENERATION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates formatter registration code (without init() wrapper).
    /// </summary>
    private string GenerateFormatterRegistration(IonType type)
    {
        return type switch
        {
            IonEnum e => GenerateEnumFormatterRegistration(e),
            IonFlags f => GenerateFlagsFormatterRegistration(f),
            _ => GenerateMessageFormatterRegistration(type)
        };
    }

    private string GenerateEnumFormatterRegistration(IonEnum e)
    {
        var ctx = new TemplateContext()
            .Set("typeName", e.name.Identifier)
            .Set("baseTypeName", TypeResolver.ResolvePrimitive(e.baseType.name.Identifier));

        return ctx.Apply(Templates.FormatterEnumTemplate);
    }

    private string GenerateFlagsFormatterRegistration(IonFlags f)
    {
        var ctx = new TemplateContext()
            .Set("typeName", f.name.Identifier)
            .Set("baseTypeName", TypeResolver.ResolvePrimitive(f.baseType.name.Identifier));

        return ctx.Apply(Templates.FormatterFlagsTemplate);
    }

    private string GenerateMessageFormatterRegistration(IonType type)
    {
        var readFields = new StringBuilder();
        var writeFields = new StringBuilder();
        var ctorArgs = new List<string>();

        foreach (var field in type.fields)
        {
            var varName = FormatLocalVariableName(field.name.Identifier);
            var typeName = TypeResolver.Resolve(field.type);
            var fieldName = char.ToUpperInvariant(field.name.Identifier[0]) + field.name.Identifier[1..];

            // 3 tabs for code inside the anonymous func
            readFields.AppendLine($"\t\t\t{varName}, err := ionwebcore.Read[{typeName}](r)");
            readFields.AppendLine("\t\t\tif err != nil {");
            readFields.AppendLine($"\t\t\t\treturn {type.name.Identifier}{{}}, err");
            readFields.AppendLine("\t\t\t}");

            writeFields.AppendLine($"\t\t\tif err := ionwebcore.Write(w, v.{fieldName}); err != nil {{");
            writeFields.AppendLine("\t\t\t\treturn err");
            writeFields.AppendLine("\t\t\t}");

            ctorArgs.Add($"{fieldName}: {varName}");
        }

        var ctx = new TemplateContext()
            .Set("typeName", type.name.Identifier)
            .Set("readFields", readFields.ToString().TrimEnd())
            .Set("writeFields", writeFields.ToString().TrimEnd())
            .Set("ctorArgs", string.Join(", ", ctorArgs))
            .Set("fieldsCount", type.fields.Count.ToString());

        return ctx.Apply(Templates.FormatterTemplate);
    }

    private string GenerateUnionFormatterRegistration(IonUnion union)
    {
        var readCases = new StringBuilder();
        var writeCases = new StringBuilder();
        var index = 0;

        foreach (var caseType in union.types)
        {
            var caseCtx = new TemplateContext()
                .Set("caseIndex", index.ToString())
                .Set("caseTypeName", caseType.name.Identifier);

            readCases.AppendLine(caseCtx.Apply(Templates.FormatterUnionReadCaseTemplate));
            writeCases.AppendLine(caseCtx.Apply(Templates.FormatterUnionWriteCaseTemplate));
            index++;
        }

        var ctx = new TemplateContext()
            .Set("unionName", union.name.Identifier)
            .Set("readCases", readCases.ToString())
            .Set("writeCases", writeCases.ToString());

        return ctx.Apply(Templates.FormatterUnionTemplate);
    }

    // Keep base class implementations for compatibility
    protected override string GenerateEnumFormatter(IonEnum e) => GenerateEnumFormatterRegistration(e);
    protected override string GenerateFlagsFormatter(IonFlags f) => GenerateFlagsFormatterRegistration(f);
    protected override string GenerateMessageFormatter(IonType type, bool isUnionCase) => GenerateMessageFormatterRegistration(type);
    protected override string GenerateUnionFormatter(IonUnion union) => GenerateUnionFormatterRegistration(union);

    // ═══════════════════════════════════════════════════════════════════
    // FIELD READ/WRITE
    // ═══════════════════════════════════════════════════════════════════

    protected override string GenerateReadField(IonField field)
    {
        var varName = FormatLocalVariableName(field.name.Identifier);
        var typeName = TypeResolver.Resolve(field.type);
        return $"{varName}, err := ionwebcore.Read[{typeName}](reader)\n\t\tif err != nil {{ return err }}";
    }

    protected override string GenerateWriteField(IonField field, string valuePrefix)
    {
        var fieldName = char.ToUpperInvariant(field.name.Identifier[0]) + field.name.Identifier[1..];
        return $"if err := ionwebcore.Write(writer, {valuePrefix}{fieldName}); err != nil {{ return err }}";
    }

    protected override string GenerateReadArgument(IonArgument arg)
    {
        var varName = FormatLocalVariableName(arg.name.Identifier);
        var typeName = TypeResolver.Resolve(arg.type);
        return $"{varName}, err := ionwebcore.Read[{typeName}](reader)\n\t\tif err != nil {{ return err }}";
    }

    protected override string GenerateWriteArgument(IonArgument arg)
    {
        var varName = FormatLocalVariableName(arg.name.Identifier);
        return $"_ = ionwebcore.Write(writer, {varName})";
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    protected override string FormatLocalVariableName(string name)
        => char.ToLowerInvariant(name[0]) + name[1..];

    public override string FileHeader() => $"""
        // Code generated by ionc. DO NOT EDIT.
        // source: ion protocol definition

        package {_packageName}
        """;
}
