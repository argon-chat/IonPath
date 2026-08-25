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
        if (!LockDocumentIsUsable())
            return;

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

    /// <summary>
    /// Decides whether the lock document in hand can be validated against at all.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when nothing further should be checked. A merely <em>outdated</em>
    /// document returns <see langword="true"/> after reporting: everything the older shape does
    /// record is still worth checking, and a genuine break in the same commit must be visible in
    /// the same report — not discovered after the author has already re-baselined past it.
    /// </returns>
    private bool LockDocumentIsUsable()
    {
        if (_lock.LoadError is { } reason)
        {
            Error(IonAnalyticCodes.ION0069_LockFileUnreadable, new IonSyntaxBase(), reason);
            return false;
        }

        if (_lock.Version == IonSchemaLock.CurrentVersion)
            return true;

        if (_lock.Version > IonSchemaLock.CurrentVersion)
        {
            Error(IonAnalyticCodes.ION0069_LockFileVersionUnsupported, new IonSyntaxBase(),
                _lock.Version, IonSchemaLock.CurrentVersion);
            return false;
        }

        Error(IonAnalyticCodes.ION0069_LockFileVersionOutdated, new IonSyntaxBase(),
            _lock.Version, IonSchemaLock.CurrentVersion, WhatTheOlderShapeCannotExpress(_lock.Version));
        return true;
    }

    /// <summary>
    /// One sentence naming what a lock document at <paramref name="version"/> is missing, for the
    /// ION0069 message. Keep a clause here for every version bump.
    /// </summary>
    private static string WhatTheOlderShapeCannotExpress(int version) => version switch
    {
        <= 1 => "Version 2 records each union case's payload field list; version 1 records only a " +
                "case's index and name, so a field inserted into, moved within or retyped inside a " +
                "case is invisible to it.",
        _    => "It predates checks this toolchain performs."
    };

    private void ValidateMsg(string defName, IonLockedDefinition locked, IonType current, IonSyntaxBase syntaxBase)
    {
        if (locked.Fields is null) return;

        CompareFieldList(locked.Fields, current.fields, addedScanFrom: 0,
            onRemoved: f => Error(IonAnalyticCodes.ION0020_LockFieldRemoved, syntaxBase,
                f.Name, f.Index, defName),
            onMoved: (f, idx) => Error(IonAnalyticCodes.ION0021_LockFieldReordered, syntaxBase,
                f.Name, defName, f.Index, idx),
            onRetyped: (f, type) => Error(IonAnalyticCodes.ION0022_LockFieldTypeChanged, syntaxBase,
                f.Name, defName, f.Type, type),
            onAdded: (field, type) => Warn(IonAnalyticCodes.ION0029_LockFieldAddedNonNullable,
                At(syntaxBase, field.name), field.name.Identifier, defName, type));
    }

    /// <summary>
    /// Diffs one positional field list against its locked counterpart.
    /// </summary>
    /// <param name="locked">
    /// The locked fields, at their <em>encoded</em> indices. For a union case that means indices
    /// offset by the union's shared field count — see <see cref="IonLockedUnionCase.Fields"/>.
    /// </param>
    /// <param name="current">
    /// The whole encoded field list as it stands now, shared prefix included, so that an index
    /// found here is directly comparable with a locked index.
    /// </param>
    /// <param name="addedScanFrom">
    /// Where the "is this field new" scan starts. A union case's current field list begins with the
    /// union's shared fields, which are locked elsewhere and must not be reported as additions to
    /// the case; 0 for a message.
    /// </param>
    /// <param name="onRemoved">A locked field with no counterpart under that name any more.</param>
    /// <param name="onMoved">A locked field found at a different index; the index is the new one.</param>
    /// <param name="onRetyped">A locked field whose canonical type differs; the string is the new one.</param>
    /// <param name="onAdded">A non-nullable field with no locked counterpart; the string is its type.</param>
    /// <remarks>
    /// Shared by messages and union case payloads because they are the same thing on the wire: a
    /// positional CBOR array whose element order is the only field identity there is. The diagnostic
    /// codes differ only in wording, so they arrive as callbacks rather than being branched on here.
    /// </remarks>
    private static void CompareFieldList(
        IReadOnlyList<IonLockedField> locked,
        IReadOnlyList<IonField> current,
        int addedScanFrom,
        Action<IonLockedField> onRemoved,
        Action<IonLockedField, int> onMoved,
        Action<IonLockedField, string> onRetyped,
        Action<IonField, string> onAdded)
    {
        foreach (var lockedField in locked)
        {
            var currentIdx = IndexOfField(current, lockedField.Name);

            if (currentIdx == -1)
            {
                onRemoved(lockedField);
                continue;
            }

            if (currentIdx != lockedField.Index)
                onMoved(lockedField, currentIdx);

            var currentTypeName = SchemaLockGenerator.GetCanonicalTypeName(current[currentIdx].type);
            if (currentTypeName != lockedField.Type)
                onRetyped(lockedField, currentTypeName);
        }

        var lockedFieldNames = locked.Select(f => f.Name).ToHashSet();

        for (var i = addedScanFrom; i < current.Count; i++)
        {
            var field = current[i];
            if (lockedFieldNames.Contains(field.name.Identifier))
                continue;

            // Only a non-nullable addition is worth saying anything about: an appended `T?` is the
            // one edit a positional array is designed to absorb.
            if (!field.type.IsMaybe)
                onAdded(field, SchemaLockGenerator.GetCanonicalTypeName(field.type));
        }
    }

    private static int IndexOfField(IReadOnlyList<IonField> fields, string name)
    {
        for (var i = 0; i < fields.Count; i++)
            if (fields[i].name.Identifier == name)
                return i;
        return -1;
    }

    private void ValidateService(string defName, IonLockedDefinition locked, IonService current,
        IonSyntaxBase syntaxBase)
    {
        if (locked.Methods is null) return;

        var currentMethods = current.methods.ToDictionary(m => m.name.Identifier);

        // Removed methods, and the renames hiding among them.
        //
        // Both stay *warnings*, deliberately. Dispatch is by method name — the generated router
        // compares the incoming name against each method it knows and rejects anything else — so a
        // call to a method that is gone fails loudly at the call boundary with an unknown-method
        // error. Nothing is mis-decoded and no value is silently wrong, which is the line this stage
        // draws between error and warning. It is still worth saying, because the failure lands on a
        // caller that was never rebuilt and may be owned by somebody else.
        var renames = PairRenamedMethods(locked.Methods, current.methods);

        foreach (var (methodName, _) in locked.Methods)
        {
            if (currentMethods.ContainsKey(methodName))
                continue;

            if (renames.TryGetValue(methodName, out var newMethod))
            {
                Warn(IonAnalyticCodes.ION0025_LockMethodRenamed, At(syntaxBase, newMethod.name),
                    defName, methodName, newMethod.name.Identifier);
                continue;
            }

            Warn(IonAnalyticCodes.ION0025_LockMethodRemoved, syntaxBase, defName, methodName);
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

    /// <summary>
    /// Matches methods that vanished from the lock against methods that appeared, by signature.
    /// </summary>
    /// <remarks>
    /// Only an unambiguous pair counts: the signature must belong to exactly one departed method and
    /// exactly one arrived one. Anything less certain falls back to reporting a removal, which is
    /// the same severity and the same advice — the pairing only changes the wording, so guessing
    /// buys nothing and being wrong would send the author looking for the wrong edit.
    /// </remarks>
    private static Dictionary<string, IonMethod> PairRenamedMethods(
        IReadOnlyDictionary<string, IonLockedMethod> locked, IReadOnlyList<IonMethod> current)
    {
        var currentNames = current.Select(m => m.name.Identifier).ToHashSet(StringComparer.Ordinal);

        var departed = locked.Where(p => !currentNames.Contains(p.Key))
            .GroupBy(p => SignatureOf(p.Value), StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Key, StringComparer.Ordinal);

        var arrived = current.Where(m => !locked.ContainsKey(m.name.Identifier))
            .GroupBy(SignatureOf, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);

        var pairs = new Dictionary<string, IonMethod>(StringComparer.Ordinal);

        foreach (var (signature, oldName) in departed)
            if (arrived.TryGetValue(signature, out var newMethod))
                pairs[oldName] = newMethod;

        return pairs;
    }

    /// <summary>
    /// A method's signature as one string, in the two forms that have to compare equal: from the
    /// lock document, and from the current IR.
    /// </summary>
    private static string SignatureOf(IonLockedMethod method) =>
        $"{method.Returns}({string.Join(", ", method.Args.Select(a => $"{a.Modifier} {a.Name}: {a.Type}"))})" +
        $"[{string.Join(",", method.Modifiers)}]";

    private static string SignatureOf(IonMethod method) =>
        $"{SchemaLockGenerator.GetCanonicalTypeName(method.returnType)}(" +
        string.Join(", ", method.arguments.Select(a =>
            $"{(a.mod != IonArgumentModifiers.None ? a.mod.ToString().ToLowerInvariant() : null)} " +
            $"{a.name.Identifier}: {SchemaLockGenerator.GetCanonicalTypeName(a.type)}")) +
        $")[{string.Join(",", method.modifiers.Select(m => m.ToString().ToLowerInvariant()))}]";

    /// <summary>
    /// Enum / flags: base type, then the member set — added, removed, renamed, renumbered.
    /// </summary>
    /// <remarks>
    /// Only renumbering used to be checked, and the member set was left entirely open with a comment
    /// saying removal was "handled elsewhere if desired". It was not handled anywhere. The three
    /// edits that were free are the three the compatibility suite showed the wire cannot take: an
    /// added member is a value older readers reject (ION0070), a removed member is a value newer
    /// readers reject (ION0023), and a base type change re-encodes every value (ION0022).
    /// </remarks>
    private void ValidateEnumOrFlags(string defName, IonLockedDefinition locked, IonType current,
        IonSyntaxBase syntaxBase)
    {
        var kind = locked.Kind.ToString().ToLowerInvariant();

        var currentBase = current switch
        {
            IonEnum e  => e.baseType.name.Identifier,
            IonFlags f => f.baseType.name.Identifier,
            _          => null
        };

        if (locked.BaseType is { } lockedBase && currentBase is not null && currentBase != lockedBase)
        {
            Error(IonAnalyticCodes.ION0022_LockEnumBaseTypeChanged, syntaxBase,
                kind, defName, lockedBase, currentBase);
        }

        if (locked.Members is null) return;

        var members = current switch
        {
            IonEnum e  => e.members,
            IonFlags f => f.members,
            _          => []
        };

        var currentMembers = new Dictionary<string, IonConstant>(StringComparer.Ordinal);
        foreach (var member in members)
            currentMembers.TryAdd(member.name.Identifier, member);

        // A rename keeps the value and changes the name, which is the exact opposite of ION0027's
        // renumber and has the exact opposite consequence: nothing on the wire moves, and every
        // generated name does. Pair the two halves up before either is reported as a removal or an
        // addition, or a single rename would be reported as one of each — an error the author has
        // no way to act on, describing a break that did not happen.
        var lockedByValue = InvertUnique(locked.Members);
        var currentByValue = InvertUnique(currentMembers.ToDictionary(p => p.Key, p => p.Value.constantValue));

        var renamedFrom = new HashSet<string>(StringComparer.Ordinal);
        var renamedTo = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (memberName, lockedValue) in locked.Members)
        {
            if (currentMembers.ContainsKey(memberName)) continue;
            if (!currentByValue.TryGetValue(lockedValue, out var newName)) continue;
            if (locked.Members.ContainsKey(newName)) continue;

            renamedFrom.Add(memberName);
            renamedTo.Add(newName);

            Warn(IonAnalyticCodes.ION0025_LockEnumMemberRenamed, At(syntaxBase, currentMembers[newName].name),
                kind, defName, memberName, newName, lockedValue);
        }

        foreach (var (memberName, lockedValue) in locked.Members)
        {
            if (renamedFrom.Contains(memberName)) continue;

            if (!currentMembers.TryGetValue(memberName, out var currentMember))
            {
                Error(IonAnalyticCodes.ION0023_LockEnumMemberRemoved, syntaxBase,
                    $"{defName}.{memberName}", kind, lockedValue);
                continue;
            }

            if (currentMember.constantValue != lockedValue)
            {
                Error(IonAnalyticCodes.ION0027_LockEnumValueChanged, At(syntaxBase, currentMember.name),
                    kind, defName, memberName, lockedValue, currentMember.constantValue);
            }
        }

        foreach (var member in members)
        {
            var memberName = member.name.Identifier;
            if (locked.Members.ContainsKey(memberName) || renamedTo.Contains(memberName)) continue;

            // A value that used to belong to a member which has since been removed is reported as
            // the removal (ION0023), not twice; `lockedByValue` is what makes that distinguishable.
            if (lockedByValue.ContainsKey(member.constantValue)) continue;

            Error(IonAnalyticCodes.ION0070_LockEnumMemberAdded, At(syntaxBase, member.name),
                kind, defName, memberName, member.constantValue);
        }
    }

    /// <summary>
    /// Inverts a name → value map, dropping any value claimed by more than one name.
    /// </summary>
    /// <remarks>
    /// Ambiguity is dropped rather than resolved: a duplicated value is ION0008's to report, and a
    /// value with two names cannot identify a rename. Dropping it degrades a rename to the
    /// removal + addition pair, which is noisier but never wrong.
    /// </remarks>
    private static Dictionary<string, string> InvertUnique(IReadOnlyDictionary<string, string> byName)
    {
        var byValue = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, value) in byName)
            if (!byValue.TryAdd(value, name))
                ambiguous.Add(value);

        foreach (var value in ambiguous)
            byValue.Remove(value);

        return byValue;
    }

    /// <summary>
    /// Union: the discriminator (case set and ordering) and, since lock version 2, what each case
    /// actually decodes to.
    /// </summary>
    /// <remarks>
    /// The case list alone was never the contract. A union encodes as a case index followed by that
    /// case's payload, so pinning <c>{index, name}</c> pinned the selector and left the selected
    /// shape free to change — <c>A(x: i4, z: i4)</c> to <c>A(x: i4, y: i4, z: i4)</c> passed clean
    /// and silently decoded <c>y</c> into <c>z</c>. Case payloads are ordinary positional field
    /// lists, so they go through the same <see cref="CompareFieldList"/> a message does.
    /// </remarks>
    private void ValidateUnion(string defName, IonLockedDefinition locked, IonUnion? current,
        IonSyntaxBase syntaxBase)
    {
        if (locked.Cases is null || current is null) return;

        var currentCases = current.types.ToList();
        var lockedCaseNames = locked.Cases.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var currentCaseNames = currentCases.Select(t => t.name.Identifier).ToHashSet(StringComparer.Ordinal);

        foreach (var lockedCase in locked.Cases)
        {
            var currentIdx = currentCases.FindIndex(t => t.name.Identifier == lockedCase.Name);

            if (currentIdx == -1)
            {
                Error(IonAnalyticCodes.ION0023_LockDefinitionRemoved, syntaxBase,
                    $"{defName}.{lockedCase.Name}", "union case");
                continue;
            }

            var currentCase = currentCases[currentIdx];

            if (currentIdx != lockedCase.Index)
            {
                Error(IonAnalyticCodes.ION0028_LockUnionCaseReordered, At(syntaxBase, currentCase.name),
                    defName, lockedCase.Name, lockedCase.Index, currentIdx);
            }

            ValidateUnionCasePayload(defName, lockedCase, currentCase, current.sharedFields.Count, syntaxBase);
        }

        // Indexed rather than `foreach` + `IndexOf`: IonType is a record, so `IndexOf` compares by
        // value and would answer with the first structurally identical case rather than this one.
        for (var idx = 0; idx < currentCases.Count; idx++)
        {
            var currentCase = currentCases[idx];
            var name = currentCase.name.Identifier;
            if (lockedCaseNames.Contains(name)) continue;

            // A case renamed in place is already ION0023 above ("U.A was removed"); the arm at that
            // index is the same arm, so reporting the new name as a *new* case on top of it would
            // describe two edits where there was one. Only a case with no locked counterpart at its
            // index is genuinely new.
            var displacedLockedCase = locked.Cases.FirstOrDefault(c => c.Index == idx);
            if (displacedLockedCase is not null && !currentCaseNames.Contains(displacedLockedCase.Name))
                continue;

            Error(IonAnalyticCodes.ION0070_LockUnionCaseAdded, At(syntaxBase, currentCase.name),
                defName, name, idx);
        }

        // Also validate shared fields if present
        if (locked.SharedFields is not null)
        {
            var currentShared = current.sharedFields;
            foreach (var lockedField in locked.SharedFields)
            {
                var currentIdx = currentShared.FindIndex(f => f.name.Identifier == lockedField.Name);
                if (currentIdx == -1)
                {
                    Error(IonAnalyticCodes.ION0020_LockFieldRemoved, syntaxBase,
                        lockedField.Name, lockedField.Index, defName);
                    continue;
                }

                var match = currentShared[currentIdx];

                // A shared field is copied to the front of every case's payload, so moving one
                // re-indexes every case at once. Reported here as the single edit it is, rather
                // than only downstream as one ION0021 per field of every case.
                if (currentIdx != lockedField.Index)
                {
                    Error(IonAnalyticCodes.ION0021_LockFieldReordered, syntaxBase,
                        lockedField.Name, defName, lockedField.Index, currentIdx);
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

    /// <summary>
    /// What one union case decodes to: a type reference, or an inline positional payload.
    /// </summary>
    private void ValidateUnionCasePayload(string defName, IonLockedUnionCase lockedCase, IonType currentCase,
        int sharedCount, IonSyntaxBase syntaxBase)
    {
        var caseName = lockedCase.Name;
        var casePosition = At(syntaxBase, currentCase.name);
        var currentIsInline = currentCase.IsUnionCase;
        var currentType = currentIsInline ? null : SchemaLockGenerator.GetCanonicalTypeName(currentCase);

        if (lockedCase.Type != currentType)
        {
            Error(IonAnalyticCodes.ION0022_LockUnionCasePayloadChanged, casePosition,
                defName, caseName, DescribePayload(lockedCase.Type), DescribePayload(currentType));
            return;
        }

        // A v1 document reaches here with no field list at all. That is not "the case has no
        // payload" — it is "this document cannot say", which is why ION0069 already failed the
        // build. Silence here rather than a vacuous pass on every case.
        if (!currentIsInline || lockedCase.Fields is null)
            return;

        CompareFieldList(lockedCase.Fields, currentCase.fields, addedScanFrom: sharedCount,
            onRemoved: f => Error(IonAnalyticCodes.ION0020_LockUnionCaseFieldRemoved, casePosition,
                f.Name, f.Index, defName, caseName),
            onMoved: (f, idx) => Error(IonAnalyticCodes.ION0021_LockUnionCaseFieldReordered, casePosition,
                f.Name, defName, caseName, f.Index, idx),
            onRetyped: (f, type) => Error(IonAnalyticCodes.ION0022_LockUnionCaseFieldTypeChanged, casePosition,
                f.Name, defName, caseName, f.Type, type),
            onAdded: (field, type) => Warn(IonAnalyticCodes.ION0029_LockUnionCaseFieldAddedNonNullable,
                At(syntaxBase, field.name), field.name.Identifier, defName, caseName, type));
    }

    private static string DescribePayload(string? type) =>
        type is null ? "an inline payload" : $"type reference '{type}'";

    #region Helpers

    private record DefinitionEntry(IonType? type, IonService? service);

    private Dictionary<string, DefinitionEntry> BuildCurrentDefinitionMap()
    {
        var map = new Dictionary<string, DefinitionEntry>();

        foreach (var module in Context.ProcessedModules)
        {
            foreach (var def in module.Definitions)
            {
                // Typedefs never enter the lock (see SchemaLockGenerator), so they must not be
                // looked for in it either — erasure means their use sites carry the change.
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

    /// <summary>
    /// Narrows a definition-level position down to one member of it.
    /// </summary>
    /// <remarks>
    /// Every diagnostic here has to carry a position, and most of these are about one member rather
    /// than the whole declaration — the added enum member, the case whose payload moved. The
    /// declaration's <paramref name="definition"/> position is the fallback and supplies the file,
    /// which the IR identifier does not carry; an identifier with no position of its own (a
    /// synthesized node) keeps the declaration's, which is still inside the right file.
    /// <para>
    /// Only reachable for something that exists in the <em>current</em> source. Anything the lock
    /// records and the schema no longer has — a removed field, a deleted member — has no position
    /// to narrow to and is reported against the declaration, or against nothing at all when the
    /// declaration itself is what went away.
    /// </para>
    /// </remarks>
    private static IonSyntaxBase At(IonSyntaxBase definition, IonIdentifier member)
    {
        if (member.StartPosition == default)
            return definition;

        return new IonSyntaxBase
        {
            SourceFile = member.SourceFile ?? definition.SourceFile,
            StartPosition = member.StartPosition,
            EndPosition = member.EndPosition
        };
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
