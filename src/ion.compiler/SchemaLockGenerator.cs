namespace ion.compiler;

using ion.runtime;
using ion.syntax;

/// <summary>
/// Generates an <see cref="IonSchemaLock"/> snapshot from compiled modules,
/// capturing the wire-level layout of every definition.
/// </summary>
public static class SchemaLockGenerator
{
    public static IonSchemaLock Generate(string moduleName, IReadOnlyList<IonModule> modules)
    {
        var definitions = new Dictionary<string, IonLockedDefinition>();

        foreach (var module in modules)
        {
            foreach (var def in module.Definitions)
            {
                if (def.IsBuiltin || def.isTypedef)
                    continue;

                var name = def.name.Identifier;

                if (definitions.ContainsKey(name))
                    continue;

                var locked = def switch
                {
                    IonUnion union    => LockUnion(union),
                    IonEnum @enum     => LockEnum(@enum),
                    IonFlags flags    => LockFlags(flags),
                    IonGenericType gt => LockMsg(gt),
                    _                 => LockMsg(def)
                };

                if (locked is not null)
                    definitions[name] = locked;
            }

            foreach (var service in module.Services)
            {
                var name = service.name.Identifier;
                if (definitions.ContainsKey(name))
                    continue;
                definitions[name] = LockService(service);
            }
        }

        return new IonSchemaLock
        {
            Module = moduleName,
            Definitions = definitions
        };
    }

    private static IonLockedDefinition LockMsg(IonType type)
    {
        var fields = type.fields
            .Select((f, i) => new IonLockedField
            {
                Index = i,
                Name = f.name.Identifier,
                Type = GetCanonicalTypeName(f.type)
            })
            .ToList();

        return new IonLockedDefinition
        {
            Kind = IonLockedDefinitionKind.Msg,
            NextIndex = fields.Count,
            Fields = fields
        };
    }

    private static IonLockedDefinition LockService(IonService service)
    {
        var methods = new Dictionary<string, IonLockedMethod>();

        foreach (var method in service.methods)
        {
            var args = method.arguments
                .Select((a, i) => new IonLockedMethodArg
                {
                    Index = i,
                    Name = a.name.Identifier,
                    Type = GetCanonicalTypeName(a.type),
                    Modifier = a.mod != IonArgumentModifiers.None ? a.mod.ToString().ToLowerInvariant() : null
                })
                .ToList();

            methods[method.name.Identifier] = new IonLockedMethod
            {
                Args = args,
                Returns = GetCanonicalTypeName(method.returnType),
                Modifiers = method.modifiers.Select(m => m.ToString().ToLowerInvariant()).ToList()
            };
        }

        return new IonLockedDefinition
        {
            Kind = IonLockedDefinitionKind.Service,
            Methods = methods
        };
    }

    private static IonLockedDefinition LockEnum(IonEnum @enum)
    {
        var members = new Dictionary<string, string>();
        foreach (var member in @enum.members)
            members[member.name.Identifier] = member.constantValue;

        return new IonLockedDefinition
        {
            Kind = IonLockedDefinitionKind.Enum,
            BaseType = @enum.baseType.name.Identifier,
            Members = members
        };
    }

    private static IonLockedDefinition LockFlags(IonFlags flags)
    {
        var members = new Dictionary<string, string>();
        foreach (var member in flags.members)
            members[member.name.Identifier] = member.constantValue;

        return new IonLockedDefinition
        {
            Kind = IonLockedDefinitionKind.Flags,
            BaseType = flags.baseType.name.Identifier,
            Members = members
        };
    }

    private static IonLockedDefinition LockUnion(IonUnion union)
    {
        var cases = union.types
            .Select((t, i) => new IonLockedUnionCase
            {
                Index = i,
                Name = t.name.Identifier,
                Type = t.IsUnionCase ? null : GetCanonicalTypeName(t)
            })
            .ToList();

        var sharedFields = union.sharedFields
            .Select((f, i) => new IonLockedField
            {
                Index = i,
                Name = f.name.Identifier,
                Type = GetCanonicalTypeName(f.type)
            })
            .ToList();

        return new IonLockedDefinition
        {
            Kind = IonLockedDefinitionKind.Union,
            Cases = cases,
            SharedFields = sharedFields.Count > 0 ? sharedFields : null,
            NextIndex = cases.Count
        };
    }

    internal static string GetCanonicalTypeName(IonType type)
    {
        if (type is IonGenericType gt && gt.TypeArguments.Count > 0)
        {
            var args = string.Join(", ", gt.TypeArguments.Select(GetCanonicalTypeName));
            return $"{gt.name.Identifier}<{args}>";
        }

        return type.name.Identifier;
    }
}
