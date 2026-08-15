namespace ion.compiler;

using ion.runtime;
using syntax;

public class CompilationContext(IReadOnlyList<IonFileSyntax> files)
{
    public IReadOnlyList<IonFileSyntax> Files { get; } = files;
    public List<IonDiagnostic> Diagnostics { get; } = [];

    public bool HasErrors => Diagnostics.Any(d => d.Severity == IonDiagnosticSeverity.Error);


    public IonType Void =>
        ResolveBuiltinType(new IonUnderlyingTypeSyntax(new IonIdentifier("void"), [], false, false, false))!;


    public required IReadOnlyList<IonModule> GlobalModules { get; init; }

    /// <summary>
    /// The features this compilation was created with — the <c>"features"</c> array of
    /// <c>ion.config.json</c>, lowercased.
    /// </summary>
    /// <remarks>
    /// <see cref="GlobalModules"/> is derived from this and is not a substitute for it: a feature
    /// that contributes no definitions and no attributes leaves no trace there. Kept so
    /// <see cref="FeatureDirectiveStage"/> can answer "does this project enable x" for a
    /// <c>#feature</c> directive. Defaulted rather than <c>required</c> so the public constructor
    /// stays usable; every real entry point goes through <see cref="Create(IReadOnlyList{string},IReadOnlyList{IonFileSyntax})"/>.
    /// </remarks>
    public IReadOnlyList<string> Features { get; init; } = [];

    public List<IonModule> ProcessedModules { get; } = [];

    /// <summary>
    /// Every <c>mixin</c> declared anywhere in this compilation, by name.
    /// </summary>
    /// <remarks>
    /// A mixin is not a type, so it is deliberately absent from <see cref="ProcessedModules"/> and
    /// from <c>IonFileSyntax.Definitions</c>. Two things still need to be able to ask "is this name
    /// a mixin": <c>MixinExpansionStage</c>, to resolve a <c>with</c> clause and to reject a mixin
    /// written in type position (ION0066), and <c>RestoreUnresolvedTypeStage</c>, which must then
    /// stay quiet about the very same name instead of adding an ION0009 on top of it.
    /// <para>
    /// The type namespace is flat and global (there is no module namespacing yet), so a single
    /// dictionary across all files is exactly the resolution rule the rest of the compiler uses.
    /// Ordinal, like every other name lookup here.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, IonMixinSyntax> Mixins => _mixins;

    private readonly Dictionary<string, IonMixinSyntax> _mixins = new(StringComparer.Ordinal);

    /// <summary>
    /// The fields a message ends up with once its <c>with</c> clause is spliced in, keyed by the
    /// message's syntax node.
    /// </summary>
    /// <remarks>
    /// Filled by <c>MixinExpansionStage</c> and read by <c>TransformStage.CompileMessages</c>. The
    /// expansion is kept <em>beside</em> the syntax tree rather than written into
    /// <c>IonMessageSyntax.Fields</c>: every syntax walk in the compiler (<see cref="IonTypeSites"/>,
    /// <see cref="IonAttributeSites"/>, the unused-symbol pass) would otherwise see each mixin field
    /// once per including message and report one written mistake N times, and the LSP would start
    /// showing the author fields they did not write in their own file.
    /// <para>
    /// Reference keyed. Two structurally identical messages in two files are different declarations
    /// with different expansions, and <c>IonMessageSyntax</c> is a record whose value equality would
    /// happily conflate them.
    /// </para>
    /// </remarks>
    public Dictionary<IonMessageSyntax, IReadOnlyList<IonFieldSyntax>> ExpandedMessageFields { get; } =
        new(ReferenceComparer<IonMessageSyntax>.Instance);

    /// <summary>Registers a <c>mixin</c> declaration. The first one wins; a second is ION0002.</summary>
    public void RegisterMixin(IonMixinSyntax mixin) => _mixins.TryAdd(mixin.Name.Identifier, mixin);

    /// <summary>Whether <paramref name="name"/> names a declared mixin.</summary>
    public bool IsMixinName(string name) => _mixins.ContainsKey(name);

    /// <summary>Identity comparison for syntax nodes used as dictionary keys.</summary>
    private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceComparer<T> Instance = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// Modules loaded from external dependencies (via #import directives).
    /// These are available for type resolution when explicitly imported.
    /// </summary>
    public List<IonModule> ExternalModules { get; } = [];

    /// <summary>
    /// Map of import declarations: file → list of (moduleName, typeNames).
    /// Populated during parsing, used during type resolution.
    /// </summary>
    public Dictionary<string, List<(string ModuleName, List<string> TypeNames)>> ImportDeclarations { get; } = new();

    public IonType? ResolveBuiltinType(IonUnderlyingTypeSyntax type) => GlobalModules
        .SelectMany(module => module.Definitions.Where(x => x.IsBuiltin))
        .FirstOrDefault(t => t.name.Identifier.Equals(type.Name.Identifier));

    public IonType? ResolveBuiltinType(IonUnresolvedType type) => GlobalModules
        .SelectMany(module => module.Definitions.Where(x => x.IsBuiltin))
        .FirstOrDefault(t => t.name.Identifier.Equals(type.name.Identifier));

    // null only when allowUnresolved == false
    /// <remarks>
    /// <para>
    /// Two things used to be lost here, and both had to be fixed before <c>Map</c> and <c>Set</c>
    /// could work at all.
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>A builtin generic never got its arguments.</b> The first line resolved builtins and
    /// returned immediately, so <c>Maybe&lt;i4&gt;</c> — and, once they existed,
    /// <c>Map&lt;string, User&gt;</c> and <c>Set&lt;i4&gt;</c> — lowered to the bare open generic
    /// with an empty <c>TypeArguments</c>. The instantiation arm was only ever reached by a
    /// project-declared generic, of which there are none: <c>ION0016</c> rejects the one syntax that
    /// could declare one. Builtins and project types now share the one <c>switch</c>.
    /// </item>
    /// <item>
    /// <b>Every argument was rebuilt as a bare name.</b>
    /// <c>new IonUnderlyingTypeSyntax(g.Name, [], false, false, false)</c> threw away the argument's
    /// own generic arguments, its modifier suffixes and its inline body, because
    /// <see cref="IonTypeParameterSyntax.Name"/> is only the argument's <em>head</em> name. That is
    /// why <c>Map&lt;string, User?&gt;</c> and <c>Map&lt;string, Array&lt;User&gt;&gt;</c> could not
    /// have resolved even after the grammar started producing them.
    /// <see cref="IonTypeParameterSyntax.Type"/> is the whole argument and is what is passed now;
    /// it also keeps the parser's positions and <c>SourceFile</c>, which the rebuild dropped
    /// (<c>WithPos</c> reads a thread-local that is only set during parsing) and which
    /// <c>g.EndPosition!.Value</c> would have thrown on for a synthesized node.
    /// </item>
    /// </list>
    /// </remarks>
    public IonType? ResolveTypeFor(IonSyntaxMember owner, IonUnderlyingTypeSyntax type, bool allowUnresolved)
    {
        var match = ResolveBuiltinType(type)
                    ?? ProcessedModules
                        .SelectMany(m => m.Definitions)
                        .FirstOrDefault(t => t.name.Identifier.Equals(type.Name.Identifier));

        switch (match)
        {
            case null:
                return allowUnresolved
                    ? WrapModifiers(new IonUnresolvedType(type.Name, [], owner), type)
                    : null;

            // Arity is not enforced here — GenericTypeValidationStage owns ION0060, and it has the
            // written syntax to point at. Whatever was written is instantiated as written so that
            // the rest of the file still lowers and still gets checked.
            case IonGenericType genericDef when type.generics.Count > 0:
            {
                var resolvedArgs = type.generics
                    .Select(g => ResolveTypeFor(owner, ArgumentSyntax(g, owner), allowUnresolved))
                    .ToList();

                if (!allowUnresolved && resolvedArgs.Any(x => x is null))
                    return null;

                var actualArgs = resolvedArgs
                    .Select(x => x ?? new IonUnresolvedType(new IonIdentifier("?"), [], owner))
                    .ToList();

                return WrapModifiers(genericDef with { TypeArguments = actualArgs }, type);
            }

            default:
                return WrapModifiers(match, type);
        }
    }

    /// <summary>
    /// One generic argument as a type reference: the whole written argument, or — only for a node
    /// somebody synthesized by hand, where the parser did not fill <c>Type</c> in — its head name.
    /// </summary>
    private static IonUnderlyingTypeSyntax ArgumentSyntax(IonTypeParameterSyntax generic, IonSyntaxMember owner)
    {
        if (generic.Type is { } written)
            return written;

        return new IonUnderlyingTypeSyntax(generic.Name, [], false, false, false)
        {
            StartPosition = generic.StartPosition,
            EndPosition = generic.EndPosition,
            SourceFile = owner.SourceFile
        };
    }

    /// <summary>
    /// Applies the written modifier suffixes to a resolved type: <c>Partial</c> innermost, then
    /// <c>Array</c>, then <c>Maybe</c>.
    /// </summary>
    /// <remarks>
    /// Public because attribute declarations need it too. <c>TransformStage.CompileAttributes</c>
    /// resolves a parameter's type with <see cref="ResolveBuiltinType(IonUnderlyingTypeSyntax)"/>,
    /// which looks only at the name — before this was reachable, <c>x: string?</c> and
    /// <c>x: i4[]</c> silently lowered to bare <c>string</c> / <c>i4</c>, which is why optional and
    /// array attribute parameters could not exist. The order here is also load-bearing for
    /// <c>IonAttributeBinder.IsOptional</c>: <c>Maybe</c> is always outermost, so a parameter is
    /// optional exactly when its resolved type is a <c>Maybe&lt;T&gt;</c>.
    /// </remarks>
    public IonType WrapModifiers(IonType inner, IonUnderlyingTypeSyntax type)
    {
        var result = inner;

        if (type.IsPartial)
            result = ResolveSpecialGeneric("Partial", result) ?? result;

        // The size of a `T[N]` rides on this wrapper — and only on this one. It is applied here
        // rather than anywhere else because this is the single place the `Array<T>` wrapper for a
        // written `[]` / `[N]` suffix is built, so `T[N]`, `T~[N]` and `T[N]?` all pick it up with
        // no extra plumbing: Partial stays inside the array and Maybe stays outside it, exactly as
        // the modifier order says. A size that is not at least 1 is ION0062 and is not carried into
        // the IR — an out-of-range N must not reach a generator or the lock.
        if (type.IsArray)
            result = ResolveSpecialGeneric("Array", result, type.ArraySize is >= 1 ? type.ArraySize : null) ?? result;

        if (type.IsOptional)
            result = ResolveSpecialGeneric("Maybe", result) ?? result;

        return result;
    }

    private IonType? ResolveSpecialGeneric(string wrapperName, IonType inner, int? fixedSize = null)
    {
        var wrapper = GlobalModules
            .SelectMany(m => m.Definitions)
            .OfType<IonGenericType>()
            .FirstOrDefault(t => t.name.Identifier == wrapperName);

        if (wrapper is null)
            return null;

        return wrapper with
        {
            TypeArguments = [inner],
            FixedSize = fixedSize
        };
    }


    public IonType? ResolveType(IonUnresolvedType unresolvedType)
    {
        var builtin = ResolveBuiltinType(unresolvedType);

        if (builtin is not null)
            return builtin;

        // Search local processed modules
        var local = ProcessedModules
            .SelectMany(module => module.Definitions)
            .FirstOrDefault(t => t.name.Identifier.Equals(unresolvedType.name.Identifier));

        if (local is not null)
            return local;

        // Search external modules (types available via #import)
        return ExternalModules
            .SelectMany(module => module.Definitions)
            .FirstOrDefault(t => t.name.Identifier.Equals(unresolvedType.name.Identifier));
    }

    /// <summary>
    /// Returns all type names available from a specific external module.
    /// </summary>
    public IReadOnlyList<string> GetExternalModuleTypeNames(string moduleName)
    {
        return ExternalModules
            .Where(m => m.SourceModule == moduleName)
            .SelectMany(m => m.Definitions)
            .Select(d => d.name.Identifier)
            .ToList();
    }


    public static CompilationContext Create(IReadOnlyList<string> features, IReadOnlyList<IonFileSyntax> files)
    {
        List<IonModule> targetIncludes = [];

        AddIf(targetIncludes, () => IonModule.GetStdModule.Value, () => features.Contains("std"));
        AddIf(targetIncludes, () => IonModule.GetOrleansModule.Value, () => features.Contains("orleans"));


        return new CompilationContext(files)
        {
            GlobalModules = [..targetIncludes],
            Features = [..features]
        };
    }

    public static CompilationContext Create(IReadOnlyList<string> features, IReadOnlyList<IonFileSyntax> files, IReadOnlyList<IonModule> externalModules)
    {
        var ctx = Create(features, files);

        foreach (var mod in externalModules)
            ctx.ExternalModules.Add(mod);

        return ctx;
    }

    public void OnPrepare(IonModule module)
    {
        ProcessedModules.Add(module);
    }

    public void OnCompiler(IonFileSyntax syntax, Action<IonModule> selector)
    {
        var mod = ProcessedModules.First(x => x.Path.Equals(syntax.file.FullName));

        selector(mod);
    }

    private static void AddIf(IList<IonModule> modules, Func<IonModule> moduleSelector, Func<bool> predicate)
    {
        if (predicate())
            modules.Add(moduleSelector());
    }


    public IonAttributeType? ResolveAttributeType(string syntaxName)
    {
        var stdAttr = GlobalModules
            .SelectMany(module => module.Attributes)
            .FirstOrDefault(t => t.name.Identifier.Equals(syntaxName));

        if (stdAttr is not null) return stdAttr;

        return ProcessedModules
            .SelectMany(module => module.Attributes)
            .FirstOrDefault(t => t.name.Identifier.Equals(syntaxName));
    }

    /// <summary>
    /// Lowers one attribute use to its IR instance, or <see langword="null"/> when the attribute is
    /// not declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silent by design: nothing here reports. An undeclared attribute is ION0005 and everything the
    /// binder finds is ION0032–ION0037, all raised by <see cref="AttributeValidationStage"/>, which
    /// walks the same uses with the target in hand. Reporting from both would double every message.
    /// </para>
    /// <para>
    /// The loop this replaced read <c>attr.arguments[i].name.Identifier</c> — the parameter's
    /// <em>name</em> — as the type to parse the argument as, then threw
    /// <see cref="InvalidOperationException"/> for the "unsupported std type" that name was not. It
    /// therefore crashed the compiler on the second argument of every multi-argument attribute, and
    /// on the first argument of any attribute whose parameter was not called after a builtin.
    /// </para>
    /// </remarks>
    public IonAttributeInstance? ResolveAttributeInstance(IonAttributeSyntax syntax)
    {
        var declaration = ResolveAttributeType(syntax.Name.Identifier);

        return declaration is null
            ? null
            : IonAttributeBinder.Materialize(IonAttributeBinder.Bind(declaration, syntax));
    }
}

public abstract class CompilationStage(CompilationContext context)
{
    protected CompilationContext Context => context;

    public void Error(string code, string message, IonSyntaxBase @base) =>
        context.Diagnostics.Add(new(code, IonDiagnosticSeverity.Error, message, @base));

    public void Warning(string code, string message, IonSyntaxBase @base) =>
        context.Diagnostics.Add(new(code, IonDiagnosticSeverity.Warning, message, @base));

    public void Info(string code, string message, IonSyntaxBase @base) =>
        context.Diagnostics.Add(new(code, IonDiagnosticSeverity.Info, message, @base));


    public void Error(IonAnalyticCode code, IonSyntaxBase @base, params object[] args) =>
        context.Diagnostics.Add(new(code.code, IonDiagnosticSeverity.Error, string.Format(code.template, args), @base));

    public void Warn(IonAnalyticCode code, IonSyntaxBase @base, params object[] args) =>
        context.Diagnostics.Add(
            new(code.code, IonDiagnosticSeverity.Warning, string.Format(code.template, args), @base));

    public void Info(IonAnalyticCode code, IonSyntaxBase @base, params object[] args) =>
        context.Diagnostics.Add(new(code.code, IonDiagnosticSeverity.Info, string.Format(code.template, args), @base));

    /// <summary>
    /// Execute the compilation stage.
    /// </summary>
    public abstract void DoProcess();

    /// <summary>
    /// Gets the stage name for display purposes.
    /// </summary>
    public virtual string StageName => GetType().Name.Replace("Stage", "");

    /// <summary>
    /// Gets the stage description.
    /// </summary>
    public virtual string StageDescription => $"Running {StageName}";

    /// <summary>
    /// Whether the pipeline should stop if this stage produces errors.
    /// </summary>
    public virtual bool StopOnError => true;
}