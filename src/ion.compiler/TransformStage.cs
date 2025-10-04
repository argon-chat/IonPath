namespace ion.compiler;

using ion.syntax;
using runtime;
using System.Globalization;
using System.Numerics;

public class TransformStage(CompilationContext context) : CompilationStage(context)
{
    public override void DoProcess()
    {
        foreach (var syntax in context.Files) 
            context.OnPrepare(PrepareModule(syntax));

        foreach (var syntax in context.Files)
            context.OnCompiler(syntax, x => GenerateAttributes(syntax, x));

        foreach (var syntax in context.Files) 
            context.OnCompiler(syntax, x => TransformFile(syntax, x));
    }

    private IonModule PrepareModule(IonFileSyntax file)
    {
        return new IonModule()
        {
            Attributes = [],
            Name = file.Name,
            Path = file.file.FullName,
            Syntax = file,
            Imports = [],
            Features = [],
            Definitions = [],
            Services = []
        };
    }

    private void GenerateAttributes(IonFileSyntax file, IonModule module)
    {
        var attributes = CompileAttributes(file);

        module.Attributes.AddRange(attributes);
    }

    private void TransformFile(IonFileSyntax file, IonModule module)
    {
        var enums = CompileEnums(file);
        var typeDefs = CompileTypedefs(file);
        var messages = CompileMessages(file);
        var services = CompileService(file);
        var flags = CompileFlags(file);
        var unions = CompileUnions(file);

        module.Definitions.AddRange(messages.Concat(typeDefs).Concat(enums).Concat(flags).Concat(unions).ToList());
        module.Services.AddRange(services);
    }

    public IReadOnlyList<IonAttributeType> CompileAttributes(IonFileSyntax file)
    {
        var attributes = new List<IonAttributeType>();
        foreach (var syntax in file.attributeDefSyntaxes)
        {
            var args = new List<IonArgument>();
            foreach (var arg in syntax.Args)
            {
                var t = context.ResolveBuiltinType(arg.type);

                if (t is null)
                    Error(IonAnalyticCodes.ION0003_TypeNotFoundOrNotBuiltin, arg, arg.type.Name);
                else
                    args.Add(new IonArgument(arg.argName, t, [], arg.modifiers));
            }

            var attr = new IonAttributeType(syntax.Name, args);

            attributes.Add(attr);
        }

        return attributes.AsReadOnly();
    }

    public IEnumerable<IonFlags> CompileFlags(IonFileSyntax file)
        => file.flagsSyntaxes.Select(CompileFlags);

    public IonFlags CompileFlags(IonFlagsSyntax syntax)
    {
        var constants = new List<IonConstant>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var usedBits = new List<BigInteger>();

        var baseType = context.ResolveBuiltinType(syntax.Type)!;

        BigInteger nextValue = 0;

        foreach (var (name, valueExpression) in syntax.Entries)
        {
            if (!usedNames.Add(name.Identifier))
            {
                Error(IonAnalyticCodes.ION0006_DuplicateEnumName, name, name.Identifier);
                continue;
            }

            BigInteger value;

            if (valueExpression.HasValue)
            {
                var expr = valueExpression.Value;

                var evalResult = EvaluateConstantExpression(expr);
                if (evalResult is null)
                {
                    Error(IonAnalyticCodes.ION0007_InvalidEnumValue, expr, expr.ToString());
                    continue;
                }

                value = evalResult.Value;
            }
            else
            {
                while (valueHasOverlap(nextValue, usedBits) || nextValue == 0)
                {
                    nextValue <<= 1;
                }

                value = nextValue;
                nextValue <<= 1;
            }

            foreach (var existing in usedBits.Where(existing => (existing & value) != 0))
            {
                Error(IonAnalyticCodes.ION0011_EnumBitwiseOverlap, name,
                    name.Identifier,
                    syntax.Name.Identifier,
                    existing,
                    value.ToString());
                break;
            }

            usedBits.Add(value);

            constants.Add(new IonConstant(
                name,
                baseType,
                value.ToString(),
                []
            ));
        }

        return new IonFlags(syntax.Name, [], constants, baseType);

        static bool valueHasOverlap(BigInteger value, List<BigInteger> existing)
            => existing.Any(e => (e & value) != 0);
    }

    public BigInteger? EvaluateConstantExpression(IonExpression expr)
    {
        var raw = expr.value.Trim();

        var parts = raw.Split("<<", StringSplitOptions.TrimEntries);

        return parts.Length switch
        {
            1 => ParseBigInteger(parts[0]),
            2 when ParseBigInteger(parts[0]) is { } left && ParseBigInteger(parts[1]) is { } right &&
                   right >= 0 => left << (int)right,
            _ => null
        };

        static BigInteger? ParseBigInteger(string s)
        {
            s = s.Trim();

            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return BigInteger.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                    ? hex
                    : null;

            if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return ConvertBinary(s[2..]);
                }
                catch
                {
                    return null;
                }
            }

            return BigInteger.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : null;
        }

        static BigInteger ConvertBinary(string binary)
        {
            BigInteger result = 0;
            foreach (var c in binary)
            {
                result <<= 1;
                if (c == '1') result |= 1;
                else if (c != '0') throw new FormatException("Invalid binary digit");
            }

            return result;
        }
    }

    public IReadOnlyList<IonEnum> CompileEnums(IonFileSyntax file)
    {
        var types = new List<IonEnum>();
        foreach (var syntax in file.enumSyntaxes)
        {
            var baseType = context.ResolveBuiltinType(syntax.Type);

            if (baseType is null)
            {
                Error(IonAnalyticCodes.ION0003_TypeNotFoundOrNotBuiltin, syntax.Type, syntax.Type.Name);
                continue;
            }

            var attributes = new List<IonAttributeInstance>();

            foreach (var attribute in syntax.Attributes)
            {
                var attr = context.ResolveAttributeInstance(attribute);

                if (attr is null)
                {
                    Error(IonAnalyticCodes.ION0005_AttributeNotFoundOrMissingDependency, attribute, attribute.Name);
                    continue;
                }

                attributes.Add(attr);
            }

            types.Add(new IonEnum(syntax.Name, attributes, CompileFlags(syntax), baseType));
        }

        return types.AsReadOnly();


        IReadOnlyList<IonConstant> CompileFlags(IonEnumSyntax syntax)
        {
            var constants = new List<IonConstant>();
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            var usedValues = new HashSet<Int128>();

            var baseType = context.ResolveBuiltinType(syntax.Type)!;

            Int128 nextValue = 0;
            Int128? firstExplicit = null;

            foreach (var e in syntax.Entries)
            {
                var (nameToken, exprToken) = e;
                var name = nameToken.Identifier;

                if (!usedNames.Add(name))
                {
                    Error(IonAnalyticCodes.ION0006_DuplicateEnumName, nameToken, name);
                    continue;
                }

                Int128 value;

                if (exprToken.HasValue)
                {
                    if (!Int128.TryParse(exprToken.Value.value, out value))
                    {
                        Error(IonAnalyticCodes.ION0007_InvalidEnumValue, e, exprToken);
                        continue;
                    }

                    firstExplicit ??= value;

                    if (!usedValues.Add(value))
                    {
                        Error(IonAnalyticCodes.ION0008_DuplicateEnumValue, e, value.ToString());
                        continue;
                    }
                }
                else
                {
                    if (nextValue < firstExplicit)
                        nextValue = firstExplicit.Value;

                    while (!usedValues.Add(nextValue))
                    {
                        nextValue++;
                    }

                    value = nextValue;
                    nextValue++;
                }

                constants.Add(new IonConstant(
                    new IonIdentifier(name),
                    baseType,
                    value.ToString(),
                    []
                ));
            }

            return constants;
        }
    }

    public IReadOnlyList<IonType> CompileTypedefs(IonFileSyntax file) => [];

    public IReadOnlyList<IonType> CompileMessages(IonFileSyntax file) =>
        (from syntax in file.messageSyntaxes
            let attributes = CompileAttributeInstancesFor(syntax)
            select new IonType(syntax.Name, attributes, PrependFields(syntax))).ToList().AsReadOnly();

    private IReadOnlyList<IonField> PrependFields(IonMessageSyntax syntax) =>
        (from field in syntax.Fields
            let fieldType = context.ResolveTypeFor(syntax, field.Type, true)
            select new IonField(field.Name, fieldType!, CompileAttributeInstancesFor(field))).ToList().AsReadOnly();

    private IReadOnlyList<IonField> PrependFields(IonUnionSyntax union, IonUnionTypeCaseSyntax syntax) =>
        (from field in union.baseFields.Concat(syntax.arguments)
            let fieldType = context.ResolveTypeFor(syntax, field.type, true)
            select new IonField(field.argName, fieldType!, CompileAttributeInstancesFor(field))).ToList().AsReadOnly();

    private IReadOnlyList<IonMethod> PrependMethods(IonServiceSyntax syntax) =>
        (from methodSyntax in syntax.Methods
            let combinedArgs = syntax.BaseArguments.Concat(methodSyntax.arguments).ToList()
            let parsedArgs = (from argSyntax in combinedArgs
                let type = context.ResolveTypeFor(argSyntax, argSyntax.type, true)
                let attrs = CompileAttributeInstancesFor(argSyntax)
                select new IonArgument(argSyntax.argName, type!, attrs, argSyntax.modifiers)).ToList()
            let returnType = methodSyntax.returnType is not null
                ? context.ResolveTypeFor(methodSyntax, methodSyntax.returnType, true) ?? context.Void
                : context.Void
            let methodAttributes = CompileAttributeInstancesFor(methodSyntax)
            select new IonMethod(methodSyntax.methodName, parsedArgs, returnType, methodSyntax.modifiers,
                methodAttributes)).ToList();

    public List<IonService> CompileService(IonFileSyntax file)
        => file.serviceSyntaxes.Select(serviceSyntax =>
            new IonService(serviceSyntax.serviceName, PrependMethods(serviceSyntax),
                CompileAttributeInstancesFor(serviceSyntax))).ToList();

    public List<IonUnion> CompileUnions(IonFileSyntax file) =>
        file.unionSyntaxes
            .Select(x => new IonUnion(x.unionName, PrependUnionTypes(x),
                x.baseFields
                    .Select(fq => new IonArgument(fq.argName, context.ResolveTypeFor(x, fq.type, true)!, [], fq.modifiers))
                    .ToList(),
                [..CompileAttributeInstancesFor(x), new IonUnionAttributeInstance()])).ToList();

    private List<IonType> PrependUnionTypes(IonUnionSyntax syntax)
    {
        if (syntax.baseFields.Count != 0 && syntax.cases.Any(x => x.IsTypeRef))
        {
            var ec = syntax.cases.First(x => x.IsTypeRef);
            Error(IonAnalyticCodes.ION0012_UnionSharedFieldsWithReferencedCase, syntax, syntax.unionName.Identifier,
                ec.caseName.Name.Identifier);
            return [];
        }

        return syntax.cases.Select(x => PrependUnionType(syntax, x)).ToList();
    }

    private IonType PrependUnionType(IonUnionSyntax syntax, IonUnionTypeCaseSyntax @case) =>
        @case.IsTypeRef
            ? context.ResolveTypeFor(syntax, @case.caseName, true)!
            : new IonType(@case.caseName.Name,
                [..CompileAttributeInstancesFor(@case), new IonUnionCaseAttributeInstance()],
                PrependFields(syntax, @case));

    private IReadOnlyList<IonAttributeInstance> CompileAttributeInstancesFor(IonSyntaxMember member)
    {
        var attributes = new List<IonAttributeInstance>();

        foreach (var attribute in member.Attributes)
        {
            var attr = context.ResolveAttributeInstance(attribute);

            if (attr is null)
            {
                Error(IonAnalyticCodes.ION0005_AttributeNotFoundOrMissingDependency, attribute, attribute.Name);
                continue;
            }

            attributes.Add(attr);
        }

        return attributes;
    }
}