namespace ion.compiler;

using ion.runtime;
using syntax;
using System.Globalization;
using System.Numerics;
using Pidgin;

public class CompilationContext(IReadOnlyList<IonFileSyntax> files)
{
    public IReadOnlyList<IonFileSyntax> Files { get; } = files;
    public List<IonDiagnostic> Diagnostics { get; } = [];

    public bool HasErrors => Diagnostics.Any(d => d.Severity == IonDiagnosticSeverity.Error);


    public IonType Void =>
        ResolveBuiltinType(new IonUnderlyingTypeSyntax(new IonIdentifier("void"), [], false, false))!;


    public required IReadOnlyList<IonModule> GlobalModules { get; init; }
    public List<IonModule> ProcessedModules { get; } = [];

    public IonType? ResolveBuiltinType(IonUnderlyingTypeSyntax type) => GlobalModules
        .SelectMany(module => module.Definitions.Where(x => x.IsBuiltin))
        .FirstOrDefault(t => t.name.Identifier.Equals(type.Name.Identifier));

    public IonType? ResolveBuiltinType(IonUnresolvedType type) => GlobalModules
        .SelectMany(module => module.Definitions.Where(x => x.IsBuiltin))
        .FirstOrDefault(t => t.name.Identifier.Equals(type.name.Identifier));

    // null only when allowUnresolved == false
    public IonType? ResolveTypeFor(IonSyntaxMember owner, IonUnderlyingTypeSyntax type, bool allowUnresolved)
    {
        var resolved = ResolveBuiltinType(type);
        if (resolved is not null)
            return WrapModifiers(resolved, type);

        var match = ProcessedModules
            .SelectMany(m => m.Definitions)
            .FirstOrDefault(t => t.name.Identifier.Equals(type.Name.Identifier));

        switch (match)
        {
            case null:
                return allowUnresolved
                    ? WrapModifiers(new IonUnresolvedType(type.Name, [], owner), type)
                    : null;
            case IonGenericType genericDef:
            {
                var resolvedArgs = type.generics
                    .Select(g => ResolveTypeFor(owner,
                        new IonUnderlyingTypeSyntax(g.Name, [], false, false).WithPos(g.StartPosition,
                            g.EndPosition!.Value), allowUnresolved))
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

    private IonType WrapModifiers(IonType inner, IonUnderlyingTypeSyntax type)
    {
        var result = inner;

        if (type.IsArray)
            result = ResolveSpecialGeneric("Array", result) ?? result;

        if (type.IsOptional)
            result = ResolveSpecialGeneric("Maybe", result) ?? result;

        return result;
    }

    private IonType? ResolveSpecialGeneric(string wrapperName, IonType inner)
    {
        var wrapper = GlobalModules
            .SelectMany(m => m.Definitions)
            .OfType<IonGenericType>()
            .FirstOrDefault(t => t.name.Identifier == wrapperName);

        if (wrapper is null)
            return null;

        return wrapper with
        {
            TypeArguments = [inner]
        };
    }


    public IonType? ResolveType(IonUnresolvedType unresolvedType)
    {
        var builtin = ResolveBuiltinType(unresolvedType);

        if (builtin is not null)
            return builtin;

        return ProcessedModules
            .SelectMany(module => module.Definitions)
            .FirstOrDefault(t => t.name.Identifier.Equals(unresolvedType.name.Identifier));
    }


    public static CompilationContext Create(IReadOnlyList<string> features, IReadOnlyList<IonFileSyntax> files)
    {
        List<IonModule> targetIncludes = [];

        AddIf(targetIncludes, () => IonModule.GetStdModule.Value, () => features.Contains("std"));
        AddIf(targetIncludes, () => IonModule.GetVectorModule.Value, () => features.Contains("vector"));
        AddIf(targetIncludes, () => IonModule.GetOrleansModule.Value, () => features.Contains("orleans"));


        return new CompilationContext(files)
        {
            GlobalModules = [..targetIncludes]
        };
    }

    public void OnCompiler(IonModule module)
    {
        ProcessedModules.Add(module);
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

    public IonAttributeInstance? ResolveAttributeInstance(IonAttributeSyntax syntax)
    {
        var attr = ResolveAttributeType(syntax.Name.Identifier);

        if (attr is null) return null;

        switch (attr.name.Identifier)
        {
            case "builtin":
                return new IonBuiltinAttributeInstance();
            case "scalar":
                return new IonScalarAttributeInstance();
            case "tag":
                return new IonTagAttributeInstance(int.Parse(syntax.Args.First()));
        }

        var parsedArgs = new List<object>();

        for (var i = 0; i < syntax.Args.Count; i++)
        {
            var rawArg = syntax.Args[i];
            if (i >= attr.arguments.Count)
                throw new InvalidOperationException($"Too many arguments for attribute '{attr.name.Identifier}'");

            var expectedType = attr.arguments[i].name.Identifier;

            if (!StdTypeParsers.TryGetValue(expectedType, out var parser))
                throw new InvalidOperationException($"Unsupported std type: {expectedType}");

            try
            {
                var parsed = parser(rawArg);
                parsedArgs.Add(parsed);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to parse argument '{rawArg}' as '{expectedType}': {ex.Message}", ex);
            }
        }

        return new IonAttributeInstance(attr.name, parsedArgs);
    }

    private static readonly Dictionary<string, Func<string, object>> StdTypeParsers = new()
    {
        ["bool"] = s => bool.Parse(s),

        ["i1"] = s => sbyte.Parse(s),
        ["i2"] = s => short.Parse(s),
        ["i4"] = s => int.Parse(s),
        ["i8"] = s => long.Parse(s),
        ["i16"] = s => BigInteger.Parse(s),

        ["u1"] = s => byte.Parse(s),
        ["u2"] = s => ushort.Parse(s),
        ["u4"] = s => uint.Parse(s),
        ["u8"] = s => ulong.Parse(s),
        ["u16"] = s => BigInteger.Parse(s),

        ["f2"] = s => Half.Parse(s, CultureInfo.InvariantCulture),
        ["f4"] = s => float.Parse(s, CultureInfo.InvariantCulture),
        ["f8"] = s => double.Parse(s, CultureInfo.InvariantCulture),

        ["bigint"] = s => BigInteger.Parse(s),
        ["guid"] = s => Guid.Parse(s),
        ["string"] = s => s,
        ["datetime"] = s => DateTime.Parse(s, CultureInfo.InvariantCulture),
        ["dateonly"] = s => DateOnly.Parse(s, CultureInfo.InvariantCulture),
        ["timeonly"] = s => TimeOnly.Parse(s, CultureInfo.InvariantCulture),
        ["uri"] = s => new Uri(s),
        ["duration"] = s => TimeSpan.Parse(s, CultureInfo.InvariantCulture),
    };
}

public abstract class CompilationStage(CompilationContext context)
{
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

    public abstract void DoProcess();
}