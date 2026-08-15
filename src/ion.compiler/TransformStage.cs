namespace ion.compiler;

using ion.syntax;
using runtime;
using System.Globalization;
using System.Numerics;

public class TransformStage(CompilationContext context) : CompilationStage(context)
{
    public override string StageName => "Syntax Transform";
    public override string StageDescription => "Converting syntax tree to intermediate representation";
    public override bool StopOnError => false; // Continue even with errors, skip invalid nodes

    public override void DoProcess()
    {
        foreach (var syntax in Context.Files) 
            Context.OnPrepare(PrepareModule(syntax));

        foreach (var syntax in Context.Files)
            Context.OnCompiler(syntax, x => GenerateAttributes(syntax, x));

        foreach (var syntax in Context.Files) 
            Context.OnCompiler(syntax, x => TransformFile(syntax, x));
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
            Services = [],
            Doc = GetModuleDoc(file)
        };
    }

    #region Documentation plumbing

    /// <summary>
    /// Reads the doc comment attached to a syntax node.
    /// </summary>
    /// <remarks>
    /// Takes <see cref="IonSyntaxBase"/> rather than <see cref="IonSyntaxMember"/> on purpose:
    /// <see cref="IonFlagEntrySyntax"/> (enum / flags members) is being migrated in ion.syntax to
    /// derive from <see cref="IonSyntaxMember"/>. This accessor yields null for entries today and
    /// starts returning real documentation the moment that change lands — no edit needed here.
    /// </remarks>
    private static string? DocOf(IonSyntaxBase? node) => (node as IonSyntaxMember)?.Comments;

    // TODO: collapse to a direct `file.ModuleDoc` read once ion.syntax exposes the property.
    // Resolved reflectively so ion.compiler keeps building while that change is in flight;
    // the lookup is cached statically and evaluated once per file.
    private static readonly System.Reflection.PropertyInfo? ModuleDocProperty =
        typeof(IonFileSyntax).GetProperty("ModuleDoc",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

    /// <summary>
    /// File level '//!' documentation for the module, or null when absent.
    /// </summary>
    private static string? GetModuleDoc(IonFileSyntax file) => ModuleDocProperty?.GetValue(file) as string;

    #endregion

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

    /// <summary>
    /// Lowers the <c>attribute @name(params…) on targets…;</c> declarations of one file.
    /// </summary>
    /// <remarks>
    /// This is also where the declaration-side diagnostics live — ION0003 / ION0004 on a parameter
    /// type, ION0038 on an unknown <c>on</c> keyword, ION0039 on a required parameter written after
    /// an optional one — because this is the only place the parameter types are resolved. Use-site
    /// checking is <see cref="AttributeValidationStage"/>'s.
    /// </remarks>
    public IReadOnlyList<IonAttributeType> CompileAttributes(IonFileSyntax file)
    {
        var attributes = new List<IonAttributeType>();

        foreach (var syntax in file.attributeDefSyntaxes)
        {
            // One IonArgument per written parameter, even when its type was rejected: the use-site
            // binder matches arguments by position, so dropping a bad parameter would silently
            // renumber every parameter after it and turn one declaration error into an arity error
            // at every call site.
            //
            // The attribute list was a hardcoded `[]`, so `attribute @a(@deprecated x: i4);` parsed,
            // validated against the parameter target and then evaporated.
            var args = syntax.Args
                .Select(arg => new IonArgument(arg.argName, ParameterType(syntax, arg),
                        CompileAttributeInstancesFor(arg), arg.modifiers)
                    { Doc = arg.Comments })
                .ToList();

            RejectRequiredAfterOptional(syntax, args);

            attributes.Add(new IonAttributeType(syntax.Name, args, Targets(syntax)) { Doc = syntax.Comments });
        }

        return attributes.AsReadOnly();
    }

    /// <summary>
    /// Resolves one attribute parameter's declared type, keeping the <c>?</c> and <c>[]</c> suffixes
    /// that <c>ResolveBuiltinType</c> alone discards.
    /// </summary>
    /// <remarks>
    /// The <c>?</c> is what makes a parameter omittable — there is no <c>= default</c> syntax — so
    /// dropping it, as this did before, made every parameter required.
    /// </remarks>
    private IonType ParameterType(IonAttributeDefSyntax owner, IonArgumentSyntax parameter)
    {
        // An inline `msg { … }` here is ION0068 (an attribute parameter has no field name to hoist
        // against, and must be a builtin anyway). Resolving it would add an ION0003 about the
        // unlexable `$inline` placeholder, which names a symbol the author never typed.
        if (parameter.type.IsInline)
            return new IonUnresolvedType(parameter.type.Name, [], owner);

        var resolved = context.ResolveBuiltinType(parameter.type);

        if (resolved is null)
        {
            Error(IonAnalyticCodes.ION0003_TypeNotFoundOrNotBuiltin, parameter, parameter.type.Name);
            return new IonUnresolvedType(parameter.type.Name, [], owner);
        }

        // ION0004's first outing. `void` and `bytes` are builtins with no literal form, `Maybe` /
        // `Array` / `Partial` named bare are wrappers with nothing to wrap, and `~` over any of them
        // is meaningless in a position that only ever holds a constant — each would have produced a
        // parameter no use site could satisfy.
        if (parameter.type.IsPartial || !IonAttributeBinder.IsAllowedParameterType(resolved))
        {
            Error(IonAnalyticCodes.ION0004_TypeNotAllowedInAttributeArguments, parameter, AsWritten(parameter.type));
            return new IonUnresolvedType(parameter.type.Name, [], owner);
        }

        return context.WrapModifiers(resolved, parameter.type);
    }

    private static string AsWritten(IonUnderlyingTypeSyntax type) => IonTypeSites.AsWritten(type);

    /// <summary>
    /// Rejects <c>attribute @a(x: string?, y: i4);</c>: an argument list can only be truncated from
    /// the end, so a required parameter behind an optional one can never be reached.
    /// </summary>
    /// <remarks>
    /// Reported once, on the first offender. The fix is a single edit — move the optional to the
    /// end — so listing every parameter after it would be the same instruction repeated.
    /// </remarks>
    private void RejectRequiredAfterOptional(IonAttributeDefSyntax syntax, List<IonArgument> args)
    {
        IonArgument? optional = null;

        for (var i = 0; i < args.Count; i++)
        {
            if (IonAttributeBinder.IsOptional(args[i]))
            {
                optional ??= args[i];
                continue;
            }

            if (optional is null)
                continue;

            Error(IonAnalyticCodes.ION0039_AttributeRequiredParameterAfterOptional, syntax.Args[i],
                syntax.Name.Identifier, args[i].name.Identifier, optional.name.Identifier);
            return;
        }
    }

    /// <summary>
    /// Lowers the <c>on</c> clause, or <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> means "allowed anywhere" and is deliberately distinct from an empty
    /// list, which would forbid every position. A clause whose keywords are <em>all</em> unknown
    /// therefore also degrades to <see langword="null"/>: the typo is already reported once here,
    /// and turning it into a target list of nothing would add an ION0038 to every single use.
    /// </remarks>
    private IReadOnlyList<IonAttributeTarget>? Targets(IonAttributeDefSyntax syntax)
    {
        if (syntax.Targets is null)
            return null;

        var targets = new List<IonAttributeTarget>();

        foreach (var written in syntax.Targets)
        {
            if (!IonAttributeTargets.TryParse(written.Identifier, out var target))
            {
                Error(IonAnalyticCodes.ION0038_UnknownAttributeTarget, written,
                    written.Identifier, syntax.Name.Identifier, string.Join(", ", IonAttributeTargets.Keywords));
                continue;
            }

            if (!targets.Contains(target))
                targets.Add(target);
        }

        return targets.Count == 0 ? null : targets;
    }

    public IEnumerable<IonFlags> CompileFlags(IonFileSyntax file)
        => file.flagsSyntaxes.Select(CompileFlags).OfType<IonFlags>();

    /// <summary>
    /// Lowers one <c>flags</c> declaration, or <see langword="null"/> when its base type is unusable.
    /// </summary>
    /// <remarks>
    /// The base type used to be resolved with a null-forgiving <c>!</c> and no diagnostic, so
    /// <c>flags F : Bogus { … }</c> compiled completely clean and produced an <see cref="IonFlags"/>
    /// with a <see langword="null"/> <c>baseType</c> that every generator then dereferenced. Enums
    /// already reported ION0003 here; flags now behave the same, and both additionally reject a
    /// base that resolves but cannot hold a member value.
    /// </remarks>
    public IonFlags? CompileFlags(IonFlagsSyntax syntax)
    {
        if (ResolveEnumBaseType(syntax.Type, "flags", syntax.Name) is not { } baseType)
            return null;

        var constants = new List<IonConstant>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var usedBits = new List<BigInteger>();

        // Seeded at 1, not 0. `0 << 1` is 0, so the "find the next free bit" loop below used to spin
        // forever on the first entry without an explicit value — i.e. on every `flags P { Read,
        // Write }`. The old `|| nextValue == 0` guard was an attempt to skip the zero value that
        // could never terminate, because shifting zero is what it was trying to escape.
        BigInteger nextValue = 1;

        foreach (var entry in syntax.Entries)
        {
            var (name, valueExpression) = entry;

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
                while (valueHasOverlap(nextValue, usedBits))
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

            // The attribute list was a hardcoded `[]`. `@deprecated Read` on a flags member parsed,
            // validated against the enumMember target and then evaporated, while the emission path
            // in all four generators sat waiting for it.
            constants.Add(new IonConstant(
                name,
                baseType,
                value.ToString(),
                CompileAttributeInstancesFor(entry)
            ) { Doc = DocOf(entry) });
        }

        // Attributes on a `flags` declaration used to be dropped on the floor — the list below was
        // a hardcoded `[]`, so `@deprecated flags Perms : u4 { … }` parsed, validated and then
        // vanished. Enums, messages and unions all carried theirs; this was the odd one out.
        return new IonFlags(syntax.Name, CompileAttributeInstancesFor(syntax), constants, baseType)
            { Doc = syntax.Comments };

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

    /// <summary>
    /// Resolves the base type of an <c>enum</c> or <c>flags</c>, or <see langword="null"/> when it
    /// cannot serve as one.
    /// </summary>
    /// <remarks>
    /// Two failures, deliberately distinct. ION0003 is a name that resolves to no builtin at all
    /// (<c>flags F : Bogus</c>). ION0004 is a name that resolves fine but to a builtin that cannot
    /// hold a member value (<c>enum E : string</c>) — the members are numbered by the loops below,
    /// so the declared member type and the value written into <c>IonConstant.constantValue</c> would
    /// disagree, and no target could emit the result.
    /// <para>
    /// Both used to be silent for <c>flags</c>, and the second was silent for <c>enum</c> too.
    /// </para>
    /// </remarks>
    private IonType? ResolveEnumBaseType(IonUnderlyingTypeSyntax written, string kind, IonIdentifier name)
    {
        // ION0068 already rejected the inline body; see ParameterType for why this does not also
        // report the `$inline` placeholder as a missing type.
        if (written.IsInline)
            return null;

        var baseType = context.ResolveBuiltinType(written);

        if (baseType is null)
        {
            Error(IonAnalyticCodes.ION0003_TypeNotFoundOrNotBuiltin, written, written.Name);
            return null;
        }

        if (IonModule.IsIntegralBuiltin(baseType))
            return baseType;

        Error(IonAnalyticCodes.ION0004_EnumBaseTypeNotIntegral, written,
            written.Name.Identifier, kind, name.Identifier, string.Join(", ", IonModule.IntegralBuiltins));
        return null;
    }

    public IReadOnlyList<IonEnum> CompileEnums(IonFileSyntax file)
    {
        var types = new List<IonEnum>();
        foreach (var syntax in file.enumSyntaxes)
        {
            if (ResolveEnumBaseType(syntax.Type, "enum", syntax.Name) is not { } baseType)
                continue;

            types.Add(new IonEnum(syntax.Name, CompileAttributeInstancesFor(syntax),
                CompileMembers(syntax, baseType), baseType) { Doc = syntax.Comments });
        }

        return types.AsReadOnly();


        IReadOnlyList<IonConstant> CompileMembers(IonEnumSyntax syntax, IonType baseType)
        {
            var constants = new List<IonConstant>();
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            var usedValues = new HashSet<Int128>();

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

                // `nameToken`, not `new IonIdentifier(name)`: the reconstructed identifier carried no
                // source position, so every diagnostic and every hover that reached an enum member
                // through the IR pointed at 0:0. And, as on the flags side, the attribute list was a
                // hardcoded `[]`, which dropped `@deprecated` on an enum member on the floor.
                constants.Add(new IonConstant(
                    nameToken,
                    baseType,
                    value.ToString(),
                    CompileAttributeInstancesFor(e)
                ) { Doc = DocOf(e) });
            }

            return constants;
        }
    }

    /// <summary>
    /// Lowers <c>typedef Name = Underlying;</c> to an <see cref="IonType"/> marked
    /// <see cref="IonType.isTypedef"/> whose single field, named <c>Value</c>, carries the
    /// underlying type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>fields[0] == underlying</c> shape is the contract every consumer reads:
    /// <c>CodeGeneratorBase.GenerateTypedef</c>, <c>IonCSharpGenerator.GenerateTypedef</c> and
    /// <c>IonTypeScriptGenerator.GenerateTypedef</c> all take <c>fields.First().type</c>.
    /// </para>
    /// <para>
    /// A typedef is a <em>transparent alias</em>, not a newtype: nothing about it reaches the
    /// wire. The IonType produced here exists only so the alias can still be *named* in generated
    /// code (a C# <c>global using</c>, a TS/Rust/Go type alias). Every use of the alias is
    /// rewritten to the underlying type by <see cref="RestoreUnresolvedTypeStage"/>, which is the
    /// single place alias semantics live.
    /// </para>
    /// <para>
    /// The underlying type is resolved with <c>allowUnresolved: true</c>, exactly like message
    /// fields: definitions declared later in the same file are not in
    /// <see cref="CompilationContext.ProcessedModules"/> yet and are patched up by the restore
    /// stage.
    /// </para>
    /// </remarks>
    public IReadOnlyList<IonType> CompileTypedefs(IonFileSyntax file)
    {
        var types = new List<IonType>();

        foreach (var syntax in file.typedefSyntaxes)
        {
            var declared = syntax.TypeName;
            var name = declared.Name.Identifier;

            // `typedef Foo<T> = Bar<T>;` parses, but nothing downstream can instantiate it, so it
            // would silently vanish. Say so instead.
            if (declared.generics.Count > 0)
            {
                Error(IonAnalyticCodes.ION0016_GenericTypedefNotSupported, declared.Name, name,
                    string.Join(", ", declared.generics.Select(g => g.Name.Identifier)));
                continue;
            }

            // `?`, `[]` and `~` on the *name* side parse but no consumer has ever read those
            // flags, so they were silently dropped.
            var modifier = declared switch
            {
                { IsOptional: true } => "?",
                { IsArray: true } => "[]",
                { IsPartial: true } => "~",
                _ => null
            };

            if (modifier is not null)
            {
                Error(IonAnalyticCodes.ION0015_TypedefNameModifier, declared.Name, name, modifier);
                continue;
            }

            if (syntax.BaseType is null)
            {
                Error(IonAnalyticCodes.ION0014_TypedefWithoutUnderlyingType, declared.Name, name);
                continue;
            }

            var underlying = context.ResolveTypeFor(syntax, syntax.BaseType, true)!;

            types.Add(new IonType(declared.Name, CompileAttributeInstancesFor(syntax),
                [new IonField(new IonIdentifier("Value"), underlying, [])], isTypedef: true)
            {
                Doc = syntax.Comments
            });
        }

        return types.AsReadOnly();
    }

    /// <remarks>
    /// A <c>mixin</c> produces no <see cref="IonType"/> and is deliberately absent from this list.
    /// That single omission is what makes every downstream "enumerate the definitions" loop correct
    /// for free: <see cref="SchemaLockGenerator"/> writes no lock entry for it, the four generators
    /// emit no declaration for it, and <c>CircularTypeReferenceStage</c> has no node for it. What
    /// they all see instead is the including message, already carrying the mixin's fields inline.
    /// </remarks>
    public IReadOnlyList<IonType> CompileMessages(IonFileSyntax file) =>
        (from syntax in file.messageSyntaxes
            let attributes = CompileAttributeInstancesFor(syntax)
            select new IonType(syntax.Name, attributes, PrepareFields(syntax))
                { Doc = syntax.Comments }).ToList().AsReadOnly();

    /// <summary>
    /// The message's fields, with its <c>with</c> clause already spliced in.
    /// </summary>
    /// <remarks>
    /// <c>MixinExpansionStage</c> computes the list — mixin fields in <c>with</c> order, then the
    /// message's own — and leaves it beside the syntax tree rather than inside it. The fallback to
    /// <c>syntax.Fields</c> is for a message with no <c>with</c> clause and for any caller that
    /// reaches this without the stage having run (the public <c>CompileMessages</c> is used by
    /// tooling as well as by the pipeline).
    /// <para>
    /// A mixin field's <see cref="IonFieldSyntax"/> node is shared with every other message that
    /// includes the same mixin, but a distinct <see cref="IonField"/> is materialised here per
    /// message, so one message's mutable <c>Doc</c> cannot leak into another's — the same
    /// arrangement as a service's base arguments in <see cref="PrependMethods"/>.
    /// </para>
    /// </remarks>
    private IReadOnlyList<IonField> PrepareFields(IonMessageSyntax syntax) =>
        (from field in FieldsOf(syntax)
            let fieldType = context.ResolveTypeFor(syntax, field.Type, true)
            select new IonField(field.Name, fieldType!, CompileAttributeInstancesFor(field))
                { Doc = field.Comments }).ToList().AsReadOnly();

    private IReadOnlyList<IonFieldSyntax> FieldsOf(IonMessageSyntax syntax) =>
        context.ExpandedMessageFields.TryGetValue(syntax, out var expanded) ? expanded : syntax.Fields;

    private IReadOnlyList<IonField> PrependFields(IonUnionSyntax union, IonUnionTypeCaseSyntax syntax) =>
        // NOTE: `union.baseFields` are shared *syntax* nodes across every case, but a fresh IonField
        // is materialised per case, so the (mutable) Doc of one case's copy cannot leak into another.
        (from field in union.baseFields.Concat(syntax.arguments)
            let fieldType = context.ResolveTypeFor(syntax, field.type, true)
            select new IonField(field.argName, fieldType!, CompileAttributeInstancesFor(field))
                { Doc = field.Comments }).ToList().AsReadOnly();

    private IReadOnlyList<IonMethod> PrependMethods(IonServiceSyntax syntax) =>
        (from methodSyntax in syntax.Methods.OfType<IonMethodSyntax>() // ← Skip InvalidMethodSyntax
            let combinedArgs = syntax.BaseArguments.Concat(methodSyntax.arguments).ToList()
            // NOTE: `syntax.BaseArguments` are shared syntax nodes prepended to *every* method, but
            // this projection allocates a distinct IonArgument per method (existing behaviour), so
            // the base-argument docs are copied per method rather than shared. Keep it that way —
            // Doc is mutable, and sharing instances would let one method's edit leak into siblings.
            let parsedArgs = (from argSyntax in combinedArgs
                let type = context.ResolveTypeFor(argSyntax, argSyntax.type, true)
                let attrs = CompileAttributeInstancesFor(argSyntax)
                select new IonArgument(argSyntax.argName, type!, attrs, argSyntax.modifiers)
                    { Doc = argSyntax.Comments }).ToList()
            // An inline `msg { … }` return type is ION0068 — a method's return type is the one
            // rejected position with a perfectly good owner and still no name to derive from.
            // Resolving it would put the unlexable `$inline` placeholder into `IonMethod.returnType`
            // and from there into `ion.lock.json` as the method's `Returns`. `void` is what a method
            // with no usable return type already lowers to.
            let returnType = methodSyntax.returnType is { IsInline: false }
                ? context.ResolveTypeFor(methodSyntax, methodSyntax.returnType, true) ?? context.Void
                : context.Void
            let methodAttributes = CompileAttributeInstancesFor(methodSyntax)
            select new IonMethod(methodSyntax.methodName, parsedArgs, returnType, methodSyntax.modifiers,
                methodAttributes) { Doc = methodSyntax.Comments }).ToList();

    public List<IonService> CompileService(IonFileSyntax file)
        => file.serviceSyntaxes.Select(serviceSyntax =>
            new IonService(serviceSyntax.serviceName, PrependMethods(serviceSyntax),
                CompileAttributeInstancesFor(serviceSyntax)) { Doc = serviceSyntax.Comments }).ToList();

    /// <remarks>
    /// The shared-field attribute list was a hardcoded <c>[]</c>, so an attribute written on a
    /// union's shared field vanished — even though the very same field, copied into each case by
    /// <see cref="PrependFields"/>, kept its attributes there. The two lists now agree.
    /// <para>
    /// <see cref="IonUnionAttributeInstance"/> is appended unconditionally and can no longer collide
    /// with an authored one: <c>@union</c> is rejected at the use site (ION0038) and filtered out of
    /// the IR by <see cref="CompileAttributeInstancesFor"/>, so this stays the only marker on the
    /// type and <c>.Single(a =&gt; a.IsUnion)</c> holds.
    /// </para>
    /// </remarks>
    public List<IonUnion> CompileUnions(IonFileSyntax file) =>
        file.unionSyntaxes
            .Select(x => new IonUnion(x.unionName, PrependUnionTypes(x),
                x.baseFields
                    .Select(fq => new IonArgument(fq.argName, context.ResolveTypeFor(x, fq.type, true)!,
                        CompileAttributeInstancesFor(fq), fq.modifiers) { Doc = fq.Comments })
                    .ToList(),
                [..CompileAttributeInstancesFor(x), new IonUnionAttributeInstance()])
                { Doc = x.Comments }).ToList();

    private List<IonType> PrependUnionTypes(IonUnionSyntax syntax)
    {
        // An inline `msg { … }` written as a case name is ION0068 (`InlineTypeHoistingStage`): a
        // case with no name at all. It is dropped before anything reads it, because a case is
        // lowered from its written name and that name is the unlexable `$inline` placeholder —
        // which would land in `IonUnion.types`, in `ion.lock.json` as a case literally called
        // `$inline`, and in whatever four generators emit for it. Filtered here rather than in the
        // lock writer so the placeholder never enters the IR in the first place, and filtered
        // before the ION0012 check below so that diagnostic cannot quote it either.
        var cases = syntax.cases.Where(x => !x.caseName.IsInline).ToList();

        if (syntax.baseFields.Count != 0 && cases.Any(x => x.IsTypeRef))
        {
            var ec = cases.First(x => x.IsTypeRef);
            Error(IonAnalyticCodes.ION0012_UnionSharedFieldsWithReferencedCase, syntax, syntax.unionName.Identifier,
                ec.caseName.Name.Identifier);
            return [];
        }

        return cases.Select(x => PrependUnionType(syntax, x)).ToList();
    }

    private IonType PrependUnionType(IonUnionSyntax syntax, IonUnionTypeCaseSyntax @case) =>
        @case.IsTypeRef
            // A `case Foo` reference resolves to the *declaration* of Foo, an instance shared with
            // whoever else references it. Its Doc belongs to that declaration — do not overwrite it
            // with the doc written at the union case site.
            ? context.ResolveTypeFor(syntax, @case.caseName, true)!
            : new IonType(@case.caseName.Name,
                [..CompileAttributeInstancesFor(@case), new IonUnionCaseAttributeInstance()],
                PrependFields(syntax, @case)) { Doc = @case.Comments };

    /// <summary>
    /// Lowers the attributes written on one member, skipping anything undeclared and anything the
    /// compiler reserves for itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reports nothing. ION0005 used to be raised here, which double-counted: a service's base
    /// arguments are prepended to every method by <see cref="PrependMethods"/>, so one bad attribute
    /// on one base argument produced one error per method. <see cref="AttributeValidationStage"/>
    /// walks the syntax tree instead, sees each written attribute exactly once, and owns every
    /// attribute diagnostic — including the argument problems this silently tolerates so that
    /// lowering can finish and the rest of the file still gets checked.
    /// </para>
    /// <para>
    /// <see cref="IonReservedAttributes"/> are dropped for the same reason the rest of this method
    /// tolerates bad arguments: the diagnostic is ION0038's, raised once by the validation stage, and
    /// the lowering must not act on the use. Dropping them keeps the invariant the IR relies on —
    /// a union carries exactly one <see cref="IonUnionAttributeInstance"/>, the one
    /// <see cref="CompileUnions"/> synthesizes — and, more importantly, stops a hand written
    /// <c>@builtin</c> from turning a <c>msg</c> into something <c>IonType.IsBuiltin</c> answers true
    /// for while the compile is still running other stages over it.
    /// </para>
    /// </remarks>
    private IReadOnlyList<IonAttributeInstance> CompileAttributeInstancesFor(IonSyntaxMember member) =>
        member.Attributes
            .Where(a => !IonReservedAttributes.IsReserved(a.Name.Identifier))
            .Select(context.ResolveAttributeInstance)
            .OfType<IonAttributeInstance>()
            .ToList();
}