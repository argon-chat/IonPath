namespace ion.compiler.CodeGen;

using Emitters;
using ion.runtime;
using ion.syntax;
using Templates;
using System.Text;

/// <summary>
/// Rust code generator — client only, no streaming (v1).
/// </summary>
public sealed class RustCodeGenerator : CodeGeneratorBase
{
    private readonly RustTypeNameResolver _rustResolver;

    public RustCodeGenerator(string @namespace)
        : base(
            @namespace,
            new RustEmitter(),
            new RustTypeNameResolver(),
            new RustTemplateProvider())
    {
        _rustResolver = (RustTypeNameResolver)TypeResolver;
    }

    // ═══════════════════════════════════════════════════════════════════
    // PROJECT FILE
    // ═══════════════════════════════════════════════════════════════════

    public override void GenerateProjectFile(string projectName, FileInfo outputFile)
        => GenerateProjectFile(projectName, outputFile, null);

    public void GenerateProjectFile(string projectName, FileInfo outputFile, string? rustcorePath)
    {
        var crateName = projectName.ToLowerInvariant().Replace(".", "-").Replace(" ", "-");
        var rustcoreDep = rustcorePath != null
            ? $$"""{ path = "{{rustcorePath}}" }"""
            : "\"0.1\"";
        var content = $$"""
            [package]
            name = "{{crateName}}"
            version = "0.1.0"
            edition = "2021"

            [dependencies]
            ion-rustcore = {{rustcoreDep}}
            minicbor = { version = "2", features = ["std", "half"] }
            uuid = { version = "1", features = ["v4"] }
            chrono = "0.4"
            async-trait = "0.1"
            futures-util = "0.3"
            tokio = { version = "1", features = ["rt", "macros"] }
            """;

        outputFile.Directory?.Create();
        File.WriteAllText(outputFile.FullName, content);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GLOBAL TYPES (not needed for Rust)
    // ═══════════════════════════════════════════════════════════════════

    public override string GenerateGlobalTypes() => "";

    // ═══════════════════════════════════════════════════════════════════
    // MODULE INIT — Rust doesn't have [ModuleInitializer],
    // but we can generate a pub fn register() for explicit calls.
    // ═══════════════════════════════════════════════════════════════════

    public override string GenerateModuleInit(
        IEnumerable<IonType> types,
        IReadOnlyList<IonService> services,
        bool clientToo,
        bool serverToo)
    {
        // Rust uses trait impls (IonFormat), no explicit registration needed.
        return "";
    }

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE EXECUTORS (Not supported — client only)
    // ═══════════════════════════════════════════════════════════════════

    public override string GenerateAllServiceExecutors(IEnumerable<IonService> services)
        => throw new NotSupportedException("Rust target is client-only");

    // ═══════════════════════════════════════════════════════════════════
    // ENUM OVERRIDE — pass correct repr type
    // ═══════════════════════════════════════════════════════════════════

    protected override string GenerateEnum(IonEnum e)
    {
        var members = e.members.Select(m => new EnumMember(
            m.name.Identifier,
            FormatEnumValue(m.constantValue, m.type)
        ));
        var baseType = _rustResolver.ResolvePrimitive(e.baseType.name.Identifier);
        return Emitter.EnumDeclaration(e.name.Identifier, members, new EnumOptions(baseType));
    }

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE CLIENT IMPL
    // ═══════════════════════════════════════════════════════════════════

    public override string GenerateAllServiceClientImpl(IEnumerable<IonService> services)
    {
        var sb = new StringBuilder();

        foreach (var service in services)
        {
            sb.AppendLine(GenerateServiceClientImpl(service));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string GenerateServiceClientImpl(IonService service)
    {
        var serviceName = service.name.Identifier;
        var methodsBuilder = new StringBuilder();

        foreach (var method in service.methods)
        {
            var methodNameRust = ToSnakeCase(method.name.Identifier);

            if (method.IsStreamable)
            {
                methodsBuilder.AppendLine(GenerateStreamingMethod(serviceName, method, methodNameRust));
                continue;
            }

            var argsCountUnary = method.arguments.Count(a => a.mod != IonArgumentModifiers.Stream);

            // Write args
            var writeArgs = string.Join($"\n{Emitter.Indent(2)}",
                method.arguments
                    .Where(a => a.mod != IonArgumentModifiers.Stream)
                    .Select(GenerateWriteArgument));

            // Method args
            var methodArgs = string.Join(", ",
                method.arguments
                    .Where(a => a.mod != IonArgumentModifiers.Stream)
                    .Select(a => $"{ToSnakeCase(a.name.Identifier)}: {(IsRefType(a.type) ? "&" : "")}{TypeResolver.Resolve(a.type)}"));

            // Select template
            string template;
            if (method.returnType.IsVoid)
                template = Templates.ServiceClientMethodVoidTemplate;
            else if (method.returnType.IsMaybe && Templates.ServiceClientMethodNullableTemplate != null)
                template = Templates.ServiceClientMethodNullableTemplate;
            else
                template = Templates.ServiceClientMethodTemplate;

            var returnTypeName = TypeResolver.Resolve(method.returnType);

            var ctx = new TemplateContext()
                .Set("serviceName", serviceName)
                .Set("methodName", methodNameRust)
                .Set("originalMethodName", method.name.Identifier)
                .Set("argsCount", argsCountUnary.ToString())
                .Set("writeArgs", writeArgs)
                .Set("args", methodArgs);

            if (!method.returnType.IsVoid)
            {
                ctx.Set("returnType", returnTypeName);

                if (method.returnType is IonGenericType { IsMaybe: true } maybeRet)
                {
                    var innerType = maybeRet.TypeArguments[0];
                    ctx.Set("returnTypeInner", TypeResolver.Resolve(innerType));
                }
            }

            methodsBuilder.AppendLine(ctx.Apply(template));
        }

        var classCtx = new TemplateContext()
            .Set("serviceName", serviceName)
            .Set("methods", methodsBuilder.ToString());

        return classCtx.Apply(Templates.ServiceClientClassTemplate);
    }

    private string GenerateStreamingMethod(string serviceName, IonMethod method, string methodNameRust)
    {
        var hasInputStream = method.arguments.Any(a => a.mod == IonArgumentModifiers.Stream);
        var nonStreamArgs = method.arguments.Where(a => a.mod != IonArgumentModifiers.Stream).ToList();
        var argsCount = nonStreamArgs.Count;

        var writeArgs = string.Join($"\n{Emitter.Indent(2)}",
            nonStreamArgs.Select(GenerateWriteArgument));

        var methodArgs = string.Join(", ",
            nonStreamArgs.Select(a => $"{ToSnakeCase(a.name.Identifier)}: {(IsRefType(a.type) ? "&" : "")}{TypeResolver.Resolve(a.type)}"));

        var returnTypeName = TypeResolver.Resolve(method.returnType);

        if (hasInputStream)
        {
            var streamArg = method.arguments.First(a => a.mod == IonArgumentModifiers.Stream);
            var inputTypeName = TypeResolver.Resolve(streamArg.type);

            return new TemplateContext()
                .Set("serviceName", serviceName)
                .Set("methodName", methodNameRust)
                .Set("originalMethodName", method.name.Identifier)
                .Set("argsCount", argsCount.ToString())
                .Set("writeArgs", writeArgs)
                .Set("args", methodArgs)
                .Set("returnType", returnTypeName)
                .Set("inputType", inputTypeName)
                .Apply(((RustTemplateProvider)Templates).ServiceClientMethodDuplexStreamTemplate);
        }

        return new TemplateContext()
            .Set("serviceName", serviceName)
            .Set("methodName", methodNameRust)
            .Set("originalMethodName", method.name.Identifier)
            .Set("argsCount", argsCount.ToString())
            .Set("writeArgs", writeArgs)
            .Set("args", methodArgs)
            .Set("returnType", returnTypeName)
            .Apply(Templates.ServiceClientMethodStreamTemplate);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FORMATTER GENERATION
    // ═══════════════════════════════════════════════════════════════════

    protected override string GenerateEnumFormatter(IonEnum e)
    {
        var baseType = _rustResolver.ResolvePrimitive(e.baseType.name.Identifier);
        var readExpr = $"{baseType}::ion_read(d)?";

        // Generate variant check
        var variantChecks = new StringBuilder();
        foreach (var m in e.members)
            variantChecks.AppendLine($"            | x if x == Self::{m.name.Identifier} as {baseType} => Ok(unsafe {{ std::mem::transmute(x) }}),");

        var enumVariantCheck = $$"""
                match value {
        {{variantChecks}}            _ => Err(()),
                }
        """;

        var ctx = new TemplateContext()
            .Set("typeName", e.name.Identifier)
            .Set("baseTypeName", baseType)
            .Set("readExpr", readExpr)
            .Set("enumVariantCheck", enumVariantCheck);

        return ctx.Apply(Templates.FormatterEnumTemplate);
    }

    protected override string GenerateFlagsFormatter(IonFlags f)
    {
        var baseType = _rustResolver.ResolvePrimitive(f.baseType.name.Identifier);
        var readExpr = $"{baseType}::ion_read(d)?";

        var ctx = new TemplateContext()
            .Set("typeName", f.name.Identifier)
            .Set("baseTypeName", baseType)
            .Set("readExpr", readExpr);

        return ctx.Apply(Templates.FormatterFlagsTemplate);
    }

    protected override string GenerateMessageFormatter(IonType type, bool isUnionCase)
    {
        var readFields = string.Join($"\n{Emitter.Indent(2)}",
            type.fields.Select(f => GenerateReadField(f)));
        var writeFields = string.Join($"\n{Emitter.Indent(2)}",
            type.fields.Select(f => GenerateWriteField(f, "self.")));
        var ctorArgs = string.Join(", ", type.fields.Select(f => ToSnakeCase(f.name.Identifier)));

        var template = isUnionCase ? Templates.FormatterUnionCaseTemplate : Templates.FormatterTemplate;

        var ctx = new TemplateContext()
            .Set("typeName", type.name.Identifier)
            .Set("readFields", readFields)
            .Set("writeFields", writeFields)
            .Set("ctorArgs", ctorArgs)
            .Set("fieldsCount", type.fields.Count.ToString());

        return ctx.Apply(template);
    }

    protected override string GenerateUnionFormatter(IonUnion union)
    {
        var readCases = new StringBuilder();
        var writeCases = new StringBuilder();
        var index = 0;

        foreach (var caseType in union.types)
        {
            var caseCtx = new TemplateContext()
                .Set("caseIndex", index.ToString())
                .Set("caseTypeName", caseType.name.Identifier)
                .Set("unionName", union.name.Identifier);

            readCases.Append(caseCtx.Apply(Templates.FormatterUnionReadCaseTemplate));
            writeCases.Append(caseCtx.Apply(Templates.FormatterUnionWriteCaseTemplate));
            index++;
        }

        var ctx = new TemplateContext()
            .Set("unionName", union.name.Identifier)
            .Set("readCases", readCases.ToString())
            .Set("writeCases", writeCases.ToString());

        return ctx.Apply(Templates.FormatterUnionTemplate);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FIELD READ/WRITE
    // ═══════════════════════════════════════════════════════════════════

    protected override string GenerateReadField(IonField field)
    {
        var varName = ToSnakeCase(field.name.Identifier);

        return field.type switch
        {
            IonGenericType { IsMaybe: true } maybe =>
                $"let {varName} = ion_rustcore::formatter::read_maybe::<{TypeResolver.Resolve(maybe.TypeArguments[0])}>(d)?;",
            IonGenericType { IsArray: true } array =>
                $"let {varName} = ion_rustcore::formatter::read_array::<{TypeResolver.Resolve(array.TypeArguments[0])}>(d)?;",
            _ =>
                $"let {varName} = <{TypeResolver.Resolve(field.type)} as IonFormat>::ion_read(d)?;"
        };
    }

    protected override string GenerateWriteField(IonField field, string valuePrefix)
    {
        var fieldAccess = $"{valuePrefix}{ToSnakeCase(field.name.Identifier)}";

        return field.type switch
        {
            IonGenericType { IsMaybe: true } =>
                $"ion_rustcore::formatter::write_maybe(e, &{fieldAccess})?;",
            IonGenericType { IsArray: true } =>
                $"ion_rustcore::formatter::write_array(e, &{fieldAccess})?;",
            _ when IsRefType(field.type) =>
                $"{fieldAccess}.ion_write(e)?;",
            _ =>
                $"{fieldAccess}.ion_write(e)?;"
        };
    }

    protected override string GenerateReadArgument(IonArgument arg)
    {
        var varName = ToSnakeCase(arg.name.Identifier);

        return arg.type switch
        {
            IonGenericType { IsMaybe: true } maybe =>
                $"let {varName} = ion_rustcore::formatter::read_maybe::<{TypeResolver.Resolve(maybe.TypeArguments[0])}>(d)?;",
            IonGenericType { IsArray: true } array =>
                $"let {varName} = ion_rustcore::formatter::read_array::<{TypeResolver.Resolve(array.TypeArguments[0])}>(d)?;",
            _ =>
                $"let {varName} = <{TypeResolver.Resolve(arg.type)} as IonFormat>::ion_read(d)?;"
        };
    }

    protected override string GenerateWriteArgument(IonArgument arg)
    {
        var varName = ToSnakeCase(arg.name.Identifier);

        return arg.type switch
        {
            IonGenericType { IsMaybe: true } =>
                $"ion_rustcore::formatter::write_maybe(&mut e, &{varName})?;",
            IonGenericType { IsArray: true } =>
                $"ion_rustcore::formatter::write_array(&mut e, &{varName})?;",
            _ when IsRefType(arg.type) =>
                $"{varName}.ion_write(&mut e)?;",
            _ =>
                $"{varName}.ion_write(&mut e)?;"
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // SINGLE FILE GENERATION (for Rust we generate a single lib.rs)
    // ═══════════════════════════════════════════════════════════════════

    public string GenerateSingleFile(CompilationContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FileHeader());
        sb.AppendLine();
        sb.AppendLine("use ion_rustcore::formatter::IonFormat;");
        sb.AppendLine("use ion_rustcore::{Decoder, Encoder, IonError};");
        sb.AppendLine("pub use futures_util::StreamExt;");
        sb.AppendLine();

        // All types
        var allTypes = ctx.ProcessedModules
            .SelectMany(m => m.Definitions)
            .Where(t => !t.IsBuiltin && !t.IsScalar && !t.IsVoid)
            .DistinctBy(t => t.name.Identifier)
            .ToList();

        sb.AppendLine("// ═══════════════ Types ═══════════════");
        sb.AppendLine();
        sb.AppendLine(GenerateTypes(allTypes));

        // All formatters
        sb.AppendLine("// ═══════════════ Formatters ═══════════════");
        sb.AppendLine();
        sb.AppendLine(GenerateAllFormatters(allTypes));

        // All service clients
        var allServices = ctx.ProcessedModules
            .SelectMany(m => m.Services)
            .DistinctBy(s => s.name.Identifier)
            .ToList();

        if (allServices.Count > 0)
        {
            sb.AppendLine("// ═══════════════ Service Clients ═══════════════");
            sb.AppendLine();
            sb.AppendLine(GenerateAllServiceClientImpl(allServices));
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(name[i - 1]))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        var result = sb.ToString();
        return EscapeRustKeyword(result);
    }

    private static string EscapeRustKeyword(string id) => id switch
    {
        "as" or "break" or "const" or "continue" or "crate" or "do" or "else" or
        "enum" or "extern" or "false" or "fn" or "for" or "if" or "impl" or "in" or
        "let" or "loop" or "match" or "mod" or "move" or "mut" or "pub" or "ref" or
        "return" or "self" or "static" or "struct" or "super" or "trait" or "true" or
        "type" or "unsafe" or "use" or "where" or "while" or "async" or "await" or
        "dyn" or "abstract" or "become" or "box" or "final" or "macro" or "override" or
        "priv" or "typeof" or "unsized" or "virtual" or "yield" or "try"
            => $"r#{id}",
        _ => id
    };

    /// <summary>
    /// Determines if a type should be passed by reference (&amp;) in Rust.
    /// </summary>
    private static bool IsRefType(IonType type)
    {
        if (type is IonGenericType) return true;
        return type.name.Identifier switch
        {
            "string" or "guid" or "bytes" or "datetime" or "dateonly" or "timeonly" or "duration" => true,
            _ => !type.IsBuiltin && !type.IsScalar
        };
    }

    protected override string FormatLocalVariableName(string name) => ToSnakeCase(name);
}
