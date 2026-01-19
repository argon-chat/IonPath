namespace ion.compiler.CodeGen;

using Emitters;
using ion.runtime;
using ion.syntax;
using Templates;
using System.Text;

/// <summary>
/// Унифицированный TypeScript генератор, наследующий от CodeGeneratorBase.
/// </summary>
public sealed class TypeScriptCodeGenerator : CodeGeneratorBase
{
    public TypeScriptCodeGenerator(string @namespace)
        : base(
            @namespace,
            new TypeScriptEmitter(),
            new TypeScriptTypeNameResolver(),
            new TypeScriptTemplateProvider())
    {
    }

    public bool UseMaybeWrapper
    {
        get => ((TypeScriptTypeNameResolver)TypeResolver).UseMaybeWrapper;
        set => ((TypeScriptTypeNameResolver)TypeResolver).UseMaybeWrapper = value;
    }

    // ═══════════════════════════════════════════════════════════════════
    // NOT SUPPORTED FOR TYPESCRIPT
    // ═══════════════════════════════════════════════════════════════════

    public override void GenerateProjectFile(string projectName, FileInfo outputFile)
        => throw new NotSupportedException("TypeScript does not use project files");

    public override string GenerateGlobalTypes()
        => throw new NotSupportedException("TypeScript uses inline type declarations");

    public override string GenerateModuleInit(
        IEnumerable<IonType> types,
        IReadOnlyList<IonService> services,
        bool clientToo,
        bool serverToo)
        => throw new NotSupportedException("TypeScript registers formatters inline");

    public override string GenerateAllServiceExecutors(IEnumerable<IonService> services)
        => throw new NotSupportedException("TypeScript is client-only");

    // ═══════════════════════════════════════════════════════════════════
    // SERVICE CLIENT IMPL
    // ═══════════════════════════════════════════════════════════════════

    public override string GenerateAllServiceClientImpl(IEnumerable<IonService> services)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FileHeader());
        sb.AppendLine();

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
            var methodName = method.name.Identifier;
            var argsCount = method.arguments.Count(a => a.mod != IonArgumentModifiers.Stream);

            // Write args
            var writeArgs = string.Join($"\n{Emitter.Indent(2)}",
                method.arguments.Where(a => a.mod != IonArgumentModifiers.Stream).Select(GenerateWriteArgument));

            // Method args
            var methodArgs = string.Join(", ", method.arguments.Select(GenerateClientMethodArg));

            // Select template
            var template = method.IsStreamable
                ? Templates.ServiceClientMethodStreamTemplate
                : method.returnType.IsVoid
                    ? Templates.ServiceClientMethodVoidTemplate
                    : Templates.ServiceClientMethodTemplate;

            // Stream call
            var inputStreamArg = method.arguments.FirstOrDefault(a => a.mod == IonArgumentModifiers.Stream);
            var returnTypeName = TypeResolver.Resolve(method.returnType);
            var streamCall = inputStreamArg != null
                ? $"ws.callServerStreamingFullDuplex<{returnTypeName}, {TypeResolver.Resolve(inputStreamArg.type)}>(\"{returnTypeName}\", writer.data, inputStream, \"{TypeResolver.Resolve(inputStreamArg.type)}\", this.signal)"
                : $"ws.callServerStreaming<{returnTypeName}>(\"{returnTypeName}\", writer.data, this.signal)";

            var ctx = new TemplateContext()
                .Set("serviceName", serviceName)
                .Set("methodName", methodName)
                .Set("argsCount", argsCount.ToString())
                .Set("writeArgs", writeArgs)
                .Set("args", methodArgs)
                .Set("streamCall", streamCall);

            if (!method.returnType.IsVoid)
                ctx.Set("returnType", returnTypeName);

            methodsBuilder.AppendLine(ctx.Apply(template));
        }

        var classCtx = new TemplateContext()
            .Set("serviceName", serviceName)
            .Set("methods", methodsBuilder.ToString());

        return classCtx.Apply(Templates.ServiceClientClassTemplate);
    }

    private string GenerateClientMethodArg(IonArgument arg)
    {
        if (arg.mod == IonArgumentModifiers.Stream)
            return $"inputStream: AsyncIterable<{TypeResolver.Resolve(arg.type)}>";
        return $"{arg.name.Identifier}: {TypeResolver.Resolve(arg.type)}";
    }

    // ═══════════════════════════════════════════════════════════════════
    // CLIENT PROXY
    // ═══════════════════════════════════════════════════════════════════

    public string GenerateClientProxy(List<IonService> services)
    {
        if (Templates.ClientProxyTemplate == null)
            return "";

        var serviceTypes = new StringBuilder();
        var serviceChecks = new StringBuilder();

        foreach (var service in services)
        {
            var srvName = service.name.Identifier;
            serviceTypes.AppendLine($"    {srvName}: I{srvName};");
            serviceChecks.AppendLine(
                $"        if (propKey === \"{srvName}\") return IonFormatterStorage.createExecutor(\"{srvName}\", ctx, controller.signal);");
        }

        var ctx = new TemplateContext()
            .Set("serviceTypes", serviceTypes.ToString())
            .Set("serviceChecks", serviceChecks.ToString());

        return ctx.Apply(Templates.ClientProxyTemplate);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FORMATTER GENERATION
    // ═══════════════════════════════════════════════════════════════════

    public override string GenerateAllFormatters(IEnumerable<IonType> types)
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

        return sb.ToString();
    }

    protected override string GenerateEnumFormatter(IonEnum e)
    {
        var ctx = new TemplateContext()
            .Set("typeName", e.name.Identifier)
            .Set("baseTypeName", e.baseType.name.Identifier)
            .Set("readExpr", TypeResolver.ResolveFormatterRef(e.baseType))
            .Set("writeExpr", $"{TypeResolver.ResolveFormatterRef(e.baseType)}.write(writer, casted);");

        return ctx.Apply(Templates.FormatterEnumTemplate);
    }

    protected override string GenerateFlagsFormatter(IonFlags f)
    {
        var ctx = new TemplateContext()
            .Set("typeName", f.name.Identifier)
            .Set("baseTypeName", f.baseType.name.Identifier)
            .Set("readExpr", TypeResolver.ResolveFormatterRef(f.baseType))
            .Set("writeExpr", $"{TypeResolver.ResolveFormatterRef(f.baseType)}.write(writer, casted);");

        return ctx.Apply(Templates.FormatterFlagsTemplate);
    }

    protected override string GenerateMessageFormatter(IonType type, bool isUnionCase)
    {
        var readFields = string.Join($"\n{Emitter.Indent(2)}",
            type.fields.Select(GenerateReadField));
        var writeFields = string.Join($"\n{Emitter.Indent(2)}",
            type.fields.Select(f => GenerateWriteField(f, "value.")));
        var ctorArgs = string.Join(", ", type.fields.Select(f => f.name.Identifier));

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

    // ═══════════════════════════════════════════════════════════════════
    // FIELD READ/WRITE
    // ═══════════════════════════════════════════════════════════════════

    protected override string GenerateReadField(IonField field)
    {
        var varName = field.name.Identifier;

        return field.type switch
        {
            { IsArray: true } when field.type is IonGenericType gt =>
                $"const {varName} = IonFormatterStorage.readArray<{TypeResolver.Resolve(gt.TypeArguments[0])}>(reader, '{TypeResolver.Resolve(gt.TypeArguments[0])}');",
            { IsMaybe: true } when field.type is IonGenericType gt =>
                UseMaybeWrapper
                    ? $"const {varName} = IonFormatterStorage.readMaybe<{TypeResolver.Resolve(gt.TypeArguments[0])}>(reader, '{TypeResolver.Resolve(gt.TypeArguments[0])}');"
                    : $"const {varName} = IonFormatterStorage.readNullable<{TypeResolver.Resolve(gt.TypeArguments[0])}>(reader, '{TypeResolver.Resolve(gt.TypeArguments[0])}');",
            _ => $"const {varName} = {TypeResolver.ResolveFormatterRef(field.type)}.read(reader);"
        };
    }

    protected override string GenerateWriteField(IonField field, string valuePrefix)
    {
        var fieldAccess = $"{valuePrefix}{field.name.Identifier}";

        return field.type switch
        {
            { IsArray: true } when field.type is IonGenericType gt =>
                $"IonFormatterStorage.writeArray<{TypeResolver.Resolve(gt.TypeArguments[0])}>(writer, {fieldAccess}, '{TypeResolver.Resolve(gt.TypeArguments[0])}');",
            { IsMaybe: true } when field.type is IonGenericType gt =>
                UseMaybeWrapper
                    ? $"IonFormatterStorage.writeMaybe<{TypeResolver.Resolve(gt.TypeArguments[0])}>(writer, {fieldAccess}, '{TypeResolver.Resolve(gt.TypeArguments[0])}');"
                    : $"IonFormatterStorage.writeNullable<{TypeResolver.Resolve(gt.TypeArguments[0])}>(writer, {fieldAccess}, '{TypeResolver.Resolve(gt.TypeArguments[0])}');",
            _ => $"{TypeResolver.ResolveFormatterRef(field.type)}.write(writer, {fieldAccess});"
        };
    }

    protected override string GenerateReadArgument(IonArgument arg)
    {
        var varName = arg.name.Identifier;

        return arg.type switch
        {
            { IsArray: true } when arg.type is IonGenericType gt =>
                $"const {varName} = IonFormatterStorage.readArray<{TypeResolver.Resolve(gt.TypeArguments[0])}>(reader, '{TypeResolver.Resolve(gt.TypeArguments[0])}');",
            { IsMaybe: true } when arg.type is IonGenericType gt =>
                UseMaybeWrapper
                    ? $"const {varName} = IonFormatterStorage.readMaybe<{TypeResolver.Resolve(gt.TypeArguments[0])}>(reader, '{TypeResolver.Resolve(gt.TypeArguments[0])}');"
                    : $"const {varName} = IonFormatterStorage.readNullable<{TypeResolver.Resolve(gt.TypeArguments[0])}>(reader, '{TypeResolver.Resolve(gt.TypeArguments[0])}');",
            _ => $"const {varName} = {TypeResolver.ResolveFormatterRef(arg.type)}.read(reader);"
        };
    }

    protected override string GenerateWriteArgument(IonArgument arg)
    {
        var varName = arg.name.Identifier;

        return arg.type switch
        {
            { IsArray: true } when arg.type is IonGenericType gt =>
                $"IonFormatterStorage.writeArray<{TypeResolver.Resolve(gt.TypeArguments[0])}>(writer, {varName}, '{TypeResolver.Resolve(gt.TypeArguments[0])}');",
            { IsMaybe: true } when arg.type is IonGenericType gt =>
                UseMaybeWrapper
                    ? $"IonFormatterStorage.writeMaybe<{TypeResolver.Resolve(gt.TypeArguments[0])}>(writer, {varName}, '{TypeResolver.Resolve(gt.TypeArguments[0])}');"
                    : $"IonFormatterStorage.writeNullable<{TypeResolver.Resolve(gt.TypeArguments[0])}>(writer, {varName}, '{TypeResolver.Resolve(gt.TypeArguments[0])}');",
            _ => $"{TypeResolver.ResolveFormatterRef(arg.type)}.write(writer, {varName});"
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    protected override string FormatLocalVariableName(string name) => name;
}
