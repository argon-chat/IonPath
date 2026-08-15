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
            FormatEnumValue(m.constantValue, m.type),
            m.Doc,
            AttributeEmission.DeprecationOf(m.attributes)
        ));
        var baseType = _rustResolver.ResolvePrimitive(e.baseType.name.Identifier);
        return Emitter.EnumDeclaration(e.name.Identifier, members, new EnumOptions(baseType), e.Doc,
            AttributeEmission.DeprecationOf(e.attributes));
    }

    /// <summary>
    /// Rust carries <c>@deprecated</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// A Rust <c>#[…]</c> has to be known to the compiler or supplied by a proc-macro, so
    /// <c>#[Cache(30, "users")]</c> is <c>error[E0658]: cannot find attribute</c>, not an ignored
    /// annotation the way an unknown C# attribute would simply fail to bind. Every other attribute
    /// is therefore dropped, and <c>#[deprecated]</c> is emitted structurally by
    /// <see cref="Emitters.RustEmitter"/> from <see cref="IonDeprecation"/> instead of going
    /// through this string path.
    /// </remarks>
    protected override string? FormatAttribute(IonAttributeInstance attr) => null;

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

            // `internal` — drop `pub` so the method is crate private. The client struct implements
            // no trait, so nothing obliges it to stay public: a peer service in the same crate can
            // still call it, while the crate's public API no longer carries it.
            if (method.IsInternal())
                template = template.Replace("pub async fn {methodName}(", "async fn {methodName}(");

            var returnTypeName = TypeResolver.Resolve(method.returnType);

            var ctx = new TemplateContext()
                .Set("serviceName", serviceName)
                .Set("methodName", methodNameRust)
                .Set("originalMethodName", method.name.Identifier)
                .Set("argsCount", argsCountUnary.ToString())
                .Set("writeArgs", writeArgs)
                .Set("args", methodArgs)
                .Set("methodDoc", MethodDoc(method));

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
            .Set("serviceDoc", ServiceDoc(service))
            .Set("methods", methodsBuilder.ToString());

        return classCtx.Apply(Templates.ServiceClientClassTemplate);
    }

    /// <summary>
    /// Rustdoc for the generated client struct, plus the service's <c>#[deprecated]</c>.
    /// </summary>
    /// <remarks>
    /// A deprecated <c>service</c> marks the client struct: it is the only Rust item the service
    /// becomes (there is no trait on this path), so it is where a use site can be told.
    /// </remarks>
    private string ServiceDoc(IonService service)
    {
        var doc = Emitter.DocComment(service.Doc);

        if (AttributeEmission.DeprecationOf(service.attributes) is { } deprecation)
            doc += $"{AttributeEmission.RustDeprecated(deprecation)}{Environment.NewLine}";

        return doc;
    }

    /// <summary>
    /// Rustdoc for a generated client method, indented to match the method body.
    /// Rust arguments are snake_cased, so the parameter names are converted too.
    /// </summary>
    private string MethodDoc(IonMethod method)
    {
        var parameters = method.arguments
            .Where(a => a.mod != IonArgumentModifiers.Stream)
            .Select(a => new DocParam(ToSnakeCase(a.name.Identifier), a.Doc))
            .ToList();

        var doc = Emitter.DocComment(method.Doc, Emitter.Indent(1), parameters);

        // `{methodDoc}` sits immediately before the indented `pub async fn`, so the attribute is
        // appended here rather than threaded through the templates — all five of which would
        // otherwise need their own placeholder.
        if (AttributeEmission.DeprecationOf(method.attributes) is { } deprecation)
            doc += $"{Emitter.Indent(1)}{AttributeEmission.RustDeprecated(deprecation)}{Environment.NewLine}";

        return doc;
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
                .Set("methodDoc", MethodDoc(method))
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
            .Set("methodDoc", MethodDoc(method))
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

    // `Map<K,V>`, `Set<T>` and `T[N]` need no arms of their own anywhere below. ion.rustcore
    // blanket-impls IonFormat for HashMap/HashSet and — via a const generic — for `[T; N]`, so the
    // default `<Type as IonFormat>::ion_read(d)?` arm already routes them through read_map /
    // read_set / read_fixed_array::<T>(d, N). The `IsArray` arms exist only because `Vec<T>`'s impl
    // is reached the same way; a FIXED array must NOT take them, since read_array is unsized and
    // would drop the length check, so each one is guarded on `FixedSize: null`.
    protected override string GenerateReadField(IonField field)
    {
        var varName = ToSnakeCase(field.name.Identifier);

        return field.type switch
        {
            IonGenericType { IsMaybe: true } maybe =>
                $"let {varName} = ion_rustcore::formatter::read_maybe::<{TypeResolver.Resolve(maybe.TypeArguments[0])}>(d)?;",
            IonGenericType { IsArray: true, FixedSize: null } array =>
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
            IonGenericType { IsArray: true, FixedSize: null } =>
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
            IonGenericType { IsArray: true, FixedSize: null } array =>
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
            IonGenericType { IsArray: true, FixedSize: null } =>
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

    /// <summary>
    /// The whole crate as one <c>lib.rs</c>, or <c>""</c> when a construct could not be emitted
    /// (see <see cref="GeneratePartials"/>) — writing a file that cannot compile is worse than
    /// writing none, and the diagnostics on <paramref name="ctx"/> say why.
    /// </summary>
    public string GenerateSingleFile(CompilationContext ctx)
    {
        var diagnosticsBefore = ctx.Diagnostics.Count;

        var sb = new StringBuilder();
        sb.AppendLine(FileHeader());

        // Crate level documentation (//!) collected from the `//!` blocks of every module.
        // ProcessedModules can list the same module twice (RestoreUnresolvedTypeStage re-adds
        // rebuilt modules), so distinct doc texts — not distinct modules — are what we want.
        var moduleDocs = CollectModuleDocs(ctx);
        if (moduleDocs is not null)
            sb.Append(Emitter.ModuleDocComment(moduleDocs));

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

        var allServices = ctx.ProcessedModules
            .SelectMany(m => m.Services)
            .DistinctBy(s => s.name.Identifier)
            .ToList();

        sb.AppendLine("// ═══════════════ Types ═══════════════");
        sb.AppendLine();
        sb.AppendLine(GenerateTypes(allTypes));

        // Patch structs for every `T~`. Rust items are order-independent, but keeping them
        // next to the types they patch is what a reader expects.
        var partials = GeneratePartials(allTypes, allServices, ctx);
        if (partials.Length > 0)
        {
            sb.AppendLine("// ═══════════════ Partials ═══════════════");
            sb.AppendLine();
            sb.AppendLine(partials);
        }

        // All formatters
        sb.AppendLine("// ═══════════════ Formatters ═══════════════");
        sb.AppendLine();
        sb.AppendLine(GenerateAllFormatters(allTypes));

        if (allServices.Count > 0)
        {
            sb.AppendLine("// ═══════════════ Service Clients ═══════════════");
            sb.AppendLine();
            sb.AppendLine(GenerateAllServiceClientImpl(allServices));
        }

        var refused = ctx.Diagnostics
            .Skip(diagnosticsBefore)
            .Any(d => d.Severity == IonDiagnosticSeverity.Error);

        return refused ? "" : sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Partial<T>  ("T~")
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// One <c>ion_rustcore::ion_partial!</c> invocation per message reached through a
    /// <c>Partial&lt;T&gt;</c>, or <c>""</c> when there are none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The macro expands to the patch struct, <c>impl IonPartialFields</c> (the field names and
    /// their order), <c>impl IonPartialSchema for T</c> — which is what makes
    /// <c>ion_rustcore::IonPartial&lt;T&gt;</c> name this struct — and <c>impl IonFormat</c>.
    /// </para>
    /// <para>
    /// Field idents are the Ion field names <em>verbatim</em>, not snake_cased like the message
    /// struct's: the macro turns each ident into the CBOR map key with <c>stringify!</c>, so
    /// snake_casing <c>displayName</c> here would silently rename the key and break interop with
    /// the C# and TypeScript runtimes. Hence the <c>#[allow(non_snake_case)]</c>.
    /// </para>
    /// </remarks>
    private string GeneratePartials(
        IReadOnlyList<IonType> types, IReadOnlyList<IonService> services, CompilationContext ctx)
    {
        var targets = CollectPartialTargets(types, services);
        if (targets.Count == 0)
            return "";

        var sb = new StringBuilder();

        foreach (var target in targets)
        {
            var name = target.name.Identifier;

            // `stringify!` keeps the r# of a raw identifier, so a keyword-named field would put
            // "r#type" on the wire. Refuse rather than emit a silently divergent encoding.
            var keyword = target.fields.FirstOrDefault(f => IsRustKeyword(f.name.Identifier));
            if (keyword is not null)
            {
                ctx.Diagnostics.Add(
                    IonCodeGenDiagnostics.PartialFieldIsRustKeyword(name, keyword.name.Identifier));
                continue;
            }

            sb.AppendLine("ion_rustcore::ion_partial! {");
            sb.Append(Emitter.DocComment($"Sparse patch over [`{name}`] (Ion `{name}~`).", Emitter.Indent(1)));
            sb.AppendLine($"{Emitter.Indent(1)}#[allow(non_snake_case)]");
            sb.AppendLine($"{Emitter.Indent(1)}pub struct {name}Patch for {name} {{");
            foreach (var field in target.fields)
                sb.AppendLine(
                    $"{Emitter.Indent(2)}{field.name.Identifier}: {TypeResolver.Resolve(field.type)},");
            sb.AppendLine($"{Emitter.Indent(1)}}}");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Every message reached through a <c>Partial&lt;T&gt;</c>, in first-seen order.</summary>
    private static List<IonType> CollectPartialTargets(
        IReadOnlyList<IonType> types, IReadOnlyList<IonService> services)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targets = new List<IonType>();

        foreach (var type in types)
        {
            foreach (var field in type.fields)
                Visit(field.type);

            if (type is IonUnion union)
                foreach (var field in union.types.SelectMany(c => c.fields))
                    Visit(field.type);
        }

        foreach (var method in services.SelectMany(s => s.methods))
        {
            foreach (var argument in method.arguments)
                Visit(argument.type);
            Visit(method.returnType);
        }

        return targets;

        void Visit(IonType type)
        {
            if (type is not IonGenericType generic)
                return;

            if (generic is { IsPartial: true, TypeArguments.Count: > 0 })
            {
                // Resolve first: a `T~` reached only through a wrapper (`T~[]`, `T~?`,
                // `Map<K, T~>`) still carries an IonUnresolvedType here. See IonPartialTargets.
                var target = IonPartialTargets.Resolve(generic.TypeArguments[0], types);
                if (target is not null and not (IonEnum or IonFlags or IonUnion or IonUnresolvedType)
                    && seen.Add(target.name.Identifier))
                    targets.Add(target);
            }

            foreach (var argument in generic.TypeArguments)
                Visit(argument);
        }
    }

    private static bool IsRustKeyword(string identifier) => EscapeRustKeyword(identifier) != identifier;

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
            // `ion_rustcore::IonDecimal` is `#[derive(Copy)]` over an i32 and an i128, so it goes
            // by value like the other scalars. Stated explicitly rather than left to the
            // `IsBuiltin`/`IsScalar` fallback below, so the generated signature does not depend on
            // which marker attributes the builtin happens to be declared with.
            "decimal" => false,
            _ => !type.IsBuiltin && !type.IsScalar
        };
    }

    protected override string FormatLocalVariableName(string name) => ToSnakeCase(name);
}
