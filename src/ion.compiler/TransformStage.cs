namespace ion.compiler;

using ion.syntax;
using runtime;

public class TransformStage(CompilationContext context) : CompilationStage(context)
{
    public override void DoProcess()
    {
        foreach (var syntax in context.Files) context.OnCompiler(TransformFile(syntax));
    }

    private IonModule TransformFile(IonFileSyntax file)
    {
        var attributes = CompileAttributes(file);
        var enums = CompileEnums(file);
        var typeDefs = CompileTypedefs(file);
        var messages = CompileMessages(file);
        var services = CompileService(file);

        return new IonModule()
        {
            Attributes = attributes,
            Name = file.Name,
            Path = file.file.FullName,
            Syntax = file,
            Imports = [],
            Features = [],
            Definitions = messages.Concat(typeDefs).ToList(),
            Services = services
        };
    }

    public IReadOnlyList<IonAttributeType> CompileAttributes(IonFileSyntax file)
    {
        var attributes = new List<IonAttributeType>();
        foreach (var syntax in file.attributeDefSyntaxes)
        {
            var args = new List<IonType>();
            foreach (var arg in syntax.Args)
            {
                var t = context.ResolveBuiltinType(arg.type);

                if (t is null)
                    Error(IonAnalyticCodes.ION0003_TypeNotFoundOrNotBuiltin, arg, arg.type.Name);
                else
                    args.Add(t);
            }

            var attr = new IonAttributeType(syntax.Name, args);

            attributes.Add(attr);
        }

        return attributes.AsReadOnly();
    }

    public IReadOnlyList<IonEnumType> CompileEnums(IonFileSyntax file)
    {
        var types = new List<IonEnumType>();
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

            types.Add(new IonEnumType(syntax.Name, baseType, CompileFlags(syntax), false, attributes));
        }

        return types.AsReadOnly();


        IReadOnlyDictionary<string, string> CompileFlags(IonEnumSyntax syntax)
        {
            var result = new Dictionary<string, string>();
            var usedValues = new HashSet<Int128>();
            var usedNames = new HashSet<string>(StringComparer.Ordinal);

            Int128? firstExplicit = null;
            Int128 nextValue = 0;

            foreach (var e in syntax.Entries)
            {
                var name = e.Name;
                var exprToken = e.ValueExpression;
                if (!usedNames.Add(name.Identifier))
                {
                    Error(IonAnalyticCodes.ION0006_DuplicateEnumName, e, name);
                    continue;
                }

                if (!string.IsNullOrEmpty(exprToken))
                {
                    if (!Int128.TryParse(exprToken, out var value))
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

                    result.Add(name.Identifier, value.ToString());
                    nextValue = value + 1;
                }
                else
                {
                    if (nextValue < firstExplicit)
                        nextValue = firstExplicit.Value;

                    while (!usedValues.Add(nextValue)) nextValue++;

                    result.Add(name.Identifier, nextValue.ToString());
                    nextValue++;
                }
            }

            return result;
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

    private IReadOnlyList<IonMethod> PrependMethods(IonServiceSyntax syntax) =>
        (from methodSyntax in syntax.Methods
            let combinedArgs = syntax.BaseArguments.Concat(methodSyntax.arguments).ToList()
            let parsedArgs = (from argSyntax in combinedArgs
                let type = context.ResolveTypeFor(argSyntax, argSyntax.type, true)
                let attrs = CompileAttributeInstancesFor(argSyntax)
                select new IonArgument(argSyntax.argName, type!, attrs)).ToList()
            let returnType = methodSyntax.returnType is not null
                ? context.ResolveTypeFor(methodSyntax, methodSyntax.returnType, true) ?? context.Void
                : context.Void
            let methodAttributes = CompileAttributeInstancesFor(methodSyntax)
            select new IonMethod(methodSyntax.methodName, parsedArgs, returnType, methodAttributes)).ToList();

    public List<IonService> CompileService(IonFileSyntax file) 
        => file.serviceSyntaxes.Select(serviceSyntax =>
        new IonService(serviceSyntax.serviceName, PrependMethods(serviceSyntax),
            CompileAttributeInstancesFor(serviceSyntax))).ToList();


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