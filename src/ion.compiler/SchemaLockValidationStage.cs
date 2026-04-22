namespace ion.compiler;

using ion.runtime;
using syntax;

/// <summary>
/// Validates the current schema against a previously-saved <see cref="IonSchemaLock"/>,
/// detecting breaking wire-format changes.
/// </summary>
public sealed class SchemaLockValidationStage : CompilationStage
{
    private readonly IonSchemaLock _lock;

    public SchemaLockValidationStage(CompilationContext context, IonSchemaLock schemaLock)
        : base(context)
    {
        _lock = schemaLock;
    }

    public override string StageName => "Schema Lock Validation";
    public override string StageDescription => "Checking for breaking wire-format changes against ion.lock.json";
    public override bool StopOnError => false;

    public override void DoProcess()
    {
        var currentDefinitions = BuildCurrentDefinitionMap();

        // 1. Check for removed definitions
        foreach (var (name, lockedDef) in _lock.Definitions)
        {
            if (!currentDefinitions.TryGetValue(name, out _))
            {
                Error(IonAnalyticCodes.ION0023_LockDefinitionRemoved, new IonSyntaxBase(),
                    name, lockedDef.Kind.ToString().ToLowerInvariant());
            }
        }

        // 2. Validate each current definition against the lock
        foreach (var (name, current) in currentDefinitions)
        {
            if (!_lock.Definitions.TryGetValue(name, out var locked))
                continue; // New definition — no constraints

            var syntaxBase = GetSyntaxBaseForDefinition(name);

            // Kind change
            var currentKind = GetDefinitionKind(current.type, current.service);
            if (currentKind != locked.Kind)
            {
                Error(IonAnalyticCodes.ION0024_LockDefinitionKindChanged, syntaxBase,
                    name, locked.Kind.ToString().ToLowerInvariant(), currentKind.ToString().ToLowerInvariant());
                continue;
            }

            switch (locked.Kind)
            {
                case IonLockedDefinitionKind.Msg:
                    ValidateMsg(name, locked, current.type!, syntaxBase);
                    break;
                case IonLockedDefinitionKind.Service:
                    ValidateService(name, locked, current.service!, syntaxBase);
                    break;
                case IonLockedDefinitionKind.Enum:
                case IonLockedDefinitionKind.Flags:
                    ValidateEnumOrFlags(name, locked, current.type!, syntaxBase);
                    break;
                case IonLockedDefinitionKind.Union:
                    ValidateUnion(name, locked, current.type as IonUnion, syntaxBase);
                    break;
            }
        }
    }

    private void ValidateMsg(string defName, IonLockedDefinition locked, IonType current, IonSyntaxBase syntaxBase)
    {
        if (locked.Fields is null) return;

        var currentFields = current.fields.ToList();

        // Check removed/reordered fields
        foreach (var lockedField in locked.Fields)
        {
            var currentIdx = currentFields.FindIndex(f => f.name.Identifier == lockedField.Name);

            if (currentIdx == -1)
            {
                // Field removed
                Error(IonAnalyticCodes.ION0020_LockFieldRemoved, syntaxBase,
                    lockedField.Name, lockedField.Index, defName);
                continue;
            }

            // Index changed (reordered)
            if (currentIdx != lockedField.Index)
            {
                Error(IonAnalyticCodes.ION0021_LockFieldReordered, syntaxBase,
                    lockedField.Name, defName, lockedField.Index, currentIdx);
            }

            // Type changed
            var currentTypeName = SchemaLockGenerator.GetCanonicalTypeName(currentFields[currentIdx].type);
            if (currentTypeName != lockedField.Type)
            {
                Error(IonAnalyticCodes.ION0022_LockFieldTypeChanged, syntaxBase,
                    lockedField.Name, defName, lockedField.Type, currentTypeName);
            }
        }

        // Check newly added fields
        var lockedFieldNames = locked.Fields.Select(f => f.Name).ToHashSet();
        for (var i = 0; i < currentFields.Count; i++)
        {
            var field = currentFields[i];
            if (lockedFieldNames.Contains(field.name.Identifier))
                continue;

            // New field — warn if not nullable
            if (!field.type.IsMaybe)
            {
                var typeName = SchemaLockGenerator.GetCanonicalTypeName(field.type);
                Warn(IonAnalyticCodes.ION0029_LockFieldAddedNonNullable, syntaxBase,
                    field.name.Identifier, defName, typeName);
            }
        }
    }

    private void ValidateService(string defName, IonLockedDefinition locked, IonService current,
        IonSyntaxBase syntaxBase)
    {
        if (locked.Methods is null) return;

        var currentMethods = current.methods.ToDictionary(m => m.name.Identifier);

        // Check removed methods
        foreach (var (methodName, _) in locked.Methods)
        {
            if (!currentMethods.ContainsKey(methodName))
            {
                Warn(IonAnalyticCodes.ION0025_LockMethodRemoved, syntaxBase, defName, methodName);
            }
        }

        // Check changed method signatures
        foreach (var (methodName, lockedMethod) in locked.Methods)
        {
            if (!currentMethods.TryGetValue(methodName, out var currentMethod))
                continue;

            var changes = new List<string>();

            // Return type
            var currentReturn = SchemaLockGenerator.GetCanonicalTypeName(currentMethod.returnType);
            if (currentReturn != lockedMethod.Returns)
                changes.Add($"return type changed from '{lockedMethod.Returns}' to '{currentReturn}'");

            // Arg count
            if (currentMethod.arguments.Count != lockedMethod.Args.Count)
            {
                changes.Add(
                    $"argument count changed from {lockedMethod.Args.Count} to {currentMethod.arguments.Count}");
            }
            else
            {
                // Check each arg
                for (var i = 0; i < lockedMethod.Args.Count; i++)
                {
                    var la = lockedMethod.Args[i];
                    var ca = currentMethod.arguments[i];
                    var caType = SchemaLockGenerator.GetCanonicalTypeName(ca.type);

                    if (caType != la.Type)
                        changes.Add($"arg '{la.Name}' type changed from '{la.Type}' to '{caType}'");
                }
            }

            if (changes.Count > 0)
            {
                Error(IonAnalyticCodes.ION0026_LockMethodSignatureChanged, syntaxBase,
                    defName, methodName, string.Join("; ", changes));
            }
        }
    }

    private void ValidateEnumOrFlags(string defName, IonLockedDefinition locked, IonType current,
        IonSyntaxBase syntaxBase)
    {
        if (locked.Members is null) return;

        var currentMembers = current switch
        {
            IonEnum e => e.members.ToDictionary(m => m.name.Identifier, m => m.constantValue),
            IonFlags f => f.members.ToDictionary(m => m.name.Identifier, m => m.constantValue),
            _ => new Dictionary<string, string>()
        };

        foreach (var (memberName, lockedValue) in locked.Members)
        {
            if (!currentMembers.TryGetValue(memberName, out var currentValue))
                continue; // Removal is a warning, handled elsewhere if desired

            if (currentValue != lockedValue)
            {
                Error(IonAnalyticCodes.ION0027_LockEnumValueChanged, syntaxBase,
                    locked.Kind.ToString().ToLowerInvariant(), defName, memberName, lockedValue, currentValue);
            }
        }
    }

    private void ValidateUnion(string defName, IonLockedDefinition locked, IonUnion? current,
        IonSyntaxBase syntaxBase)
    {
        if (locked.Cases is null || current is null) return;

        var currentCases = current.types.ToList();

        foreach (var lockedCase in locked.Cases)
        {
            var currentIdx = currentCases.FindIndex(t => t.name.Identifier == lockedCase.Name);

            if (currentIdx == -1)
            {
                Error(IonAnalyticCodes.ION0023_LockDefinitionRemoved, syntaxBase,
                    $"{defName}.{lockedCase.Name}", "union case");
                continue;
            }

            if (currentIdx != lockedCase.Index)
            {
                Error(IonAnalyticCodes.ION0028_LockUnionCaseReordered, syntaxBase,
                    defName, lockedCase.Name, lockedCase.Index, currentIdx);
            }
        }

        // Also validate shared fields if present
        if (locked.SharedFields is not null)
        {
            var currentShared = current.sharedFields;
            foreach (var lockedField in locked.SharedFields)
            {
                var match = currentShared.FirstOrDefault(f => f.name.Identifier == lockedField.Name);
                if (match is null)
                {
                    Error(IonAnalyticCodes.ION0020_LockFieldRemoved, syntaxBase,
                        lockedField.Name, lockedField.Index, defName);
                    continue;
                }

                var currentTypeName = SchemaLockGenerator.GetCanonicalTypeName(match.type);
                if (currentTypeName != lockedField.Type)
                {
                    Error(IonAnalyticCodes.ION0022_LockFieldTypeChanged, syntaxBase,
                        lockedField.Name, defName, lockedField.Type, currentTypeName);
                }
            }
        }
    }

    #region Helpers

    private record DefinitionEntry(IonType? type, IonService? service);

    private Dictionary<string, DefinitionEntry> BuildCurrentDefinitionMap()
    {
        var map = new Dictionary<string, DefinitionEntry>();

        foreach (var module in Context.ProcessedModules)
        {
            foreach (var def in module.Definitions)
            {
                if (def.IsBuiltin || def.isTypedef) continue;
                map.TryAdd(def.name.Identifier, new DefinitionEntry(def, null));
            }

            foreach (var svc in module.Services)
                map.TryAdd(svc.name.Identifier, new DefinitionEntry(null, svc));
        }

        return map;
    }

    private IonSyntaxBase GetSyntaxBaseForDefinition(string name)
    {
        foreach (var module in Context.ProcessedModules)
        {
            var def = module.Definitions.FirstOrDefault(d => d.name.Identifier == name);
            if (def is not null)
                return new IonSyntaxBase
                {
                    SourceFile = module.Syntax?.file,
                    StartPosition = def.name.StartPosition,
                    EndPosition = def.name.EndPosition
                };

            var svc = module.Services.FirstOrDefault(s => s.name.Identifier == name);
            if (svc is not null)
                return new IonSyntaxBase
                {
                    SourceFile = module.Syntax?.file,
                    StartPosition = svc.name.StartPosition,
                    EndPosition = svc.name.EndPosition
                };
        }

        return new IonSyntaxBase();
    }

    private static IonLockedDefinitionKind GetDefinitionKind(IonType? type, IonService? service)
    {
        if (service is not null) return IonLockedDefinitionKind.Service;
        return type switch
        {
            IonUnion => IonLockedDefinitionKind.Union,
            IonEnum => IonLockedDefinitionKind.Enum,
            IonFlags => IonLockedDefinitionKind.Flags,
            { isTypedef: true } => IonLockedDefinitionKind.Typedef,
            _ => IonLockedDefinitionKind.Msg
        };
    }

    #endregion
}
