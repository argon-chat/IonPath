namespace ion.compiler;

using syntax;

/// <summary>
/// Gives every inline anonymous type — <c>shipping: msg { address: string; };</c> — a real name and
/// lifts it out to a top level <c>msg</c>, rewriting the field to reference it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The naming rule.</strong> <c>{Owner}{PascalCasedFieldName}</c>.
/// <c>msg Order { shipping: msg { … }; }</c> yields <c>OrderShipping</c>. The owner is the chain of
/// names from the top level declaration down to the immediate container, so a method argument on
/// <c>service Api { Get(id: msg { … }) }</c> yields <c>ApiGetId</c>, and a nested inline type
/// extends the chain it is written in: the <c>at</c> of
/// <c>history: msg { at: msg { … }; }[]</c> on <c>Order</c> is <c>OrderHistoryAt</c>.
/// </para>
/// <para>
/// <strong>Why rewrite the tree instead of teaching everyone about inline bodies.</strong> After
/// this stage there are no inline bodies left in a well-formed file, so duplicate detection, the
/// type-site walks, unused-symbol detection, deprecation, the schema lock and all four generators
/// see an ordinary <c>msg</c> and needed no changes at all. The one thing they do see that the
/// author did not write is the derived name.
/// </para>
/// <para>
/// <strong>Which is why a collision is an error.</strong> A derived name goes into the same flat,
/// global type namespace as everything else — Ion has no module namespacing yet, and this feature
/// makes that gap materially worse, because a field name now claims a type name. Until namespacing
/// lands, ION0067 is the entire thing keeping that safe: it is a hard error, never a silent rename,
/// because a rename would quietly change a name in <c>ion.lock.json</c> and in four generated
/// languages that nobody asked to change.
/// </para>
/// <para>
/// <strong>Where an inline type is refused.</strong> The grammar accepts <c>msg { … }</c> in every
/// type position, but the naming rule needs a field name, and several positions have none: a generic
/// argument, a typedef's name side or underlying type, an enum or flags base type, a method's return
/// type, an attribute parameter, a union case name. Those are ION0068 rather than a name invented by
/// the compiler — an unrequested type called <c>Result</c> is a name the author then owns forever,
/// because it is in the lock.
/// </para>
/// <para>
/// <strong>Position in the pipeline.</strong> First of the tree-shaping stages: before duplicate
/// detection (so the hoisted messages are ordinary declarations by the time it runs) and before
/// <see cref="MixinExpansionStage"/>, so that an inline type written in a <c>mixin</c> is named
/// after the mixin and hoisted exactly once, rather than once per message that includes it.
/// </para>
/// </remarks>
public sealed class InlineTypeHoistingStage(CompilationContext context) : CompilationStage(context)
{
    public override string StageName => "Inline Type Hoisting";
    public override string StageDescription => "Naming and lifting inline anonymous 'msg { … }' types";

    /// <summary>Collect every bad inline type, don't stop at the first.</summary>
    public override bool StopOnError => false;

    /// <summary>Names already spoken for, mapped to a phrase naming what holds them.</summary>
    /// <remarks>
    /// Ordinal, matching <c>CompilationContext.ResolveTypeFor</c> / <c>ResolveBuiltinType</c>, which
    /// compare with <c>string.Equals</c>. A derived name that differs only in case from a
    /// declaration is not a resolution hazard and is left to ION0002, which is the check that is
    /// case-insensitive on purpose.
    /// </remarks>
    private readonly Dictionary<string, string> _claimed = new(StringComparer.Ordinal);

    /// <summary>Derived names already produced, mapped to the site that produced them.</summary>
    private readonly Dictionary<string, string> _hoisted = new(StringComparer.Ordinal);

    public override void DoProcess()
    {
        CollectClaimedNames();

        foreach (var file in Context.Files)
            HoistFile(file);
    }

    // ── Name ownership ─────────────────────────────────────────────────

    private void CollectClaimedNames()
    {
        foreach (var module in Context.GlobalModules)
        foreach (var definition in module.Definitions.Where(d => d.IsBuiltin))
            _claimed.TryAdd(definition.name.Identifier, $"the builtin type '{definition.name.Identifier}' from module '{module.Name}'");

        foreach (var file in Context.Files)
        {
            foreach (var definition in file.Definitions)
                if (NameOf(definition) is { } name)
                    _claimed.TryAdd(name, IonTypeSites.Describe(definition));

            foreach (var mixin in file.mixinSyntaxes)
                _claimed.TryAdd(mixin.Name.Identifier, IonTypeSites.Describe(mixin));
        }
    }

    /// <summary>Mirrors <c>DuplicateSymbolValidationStage.Declaration</c> — the same namespace.</summary>
    private static string? NameOf(IonSyntaxMember definition) => definition switch
    {
        IonMessageSyntax m => m.Name.Identifier,
        IonEnumSyntax e => e.Name.Identifier,
        IonFlagsSyntax f => f.Name.Identifier,
        IonUnionSyntax u => u.unionName.Identifier,
        IonTypedefSyntax t => t.TypeName.Name.Identifier,
        IonServiceSyntax s => s.serviceName.Identifier,
        IonAttributeDefSyntax a => a.Name.Identifier,
        _ => null
    };

    // ── Traversal ──────────────────────────────────────────────────────

    private void HoistFile(IonFileSyntax file)
    {
        // Snapshotted: the loop appends hoisted messages to the same list. They never need a second
        // pass — a hoisted body is fully processed, innermost first, before it is appended.
        foreach (var msg in file.messageSyntaxes.ToList())
            HoistFields(file, msg.Fields, msg.Name.Identifier, IonTypeSites.Describe(msg));

        foreach (var mixin in file.mixinSyntaxes)
            HoistFields(file, mixin.Fields, mixin.Name.Identifier, IonTypeSites.Describe(mixin));

        foreach (var union in file.unionSyntaxes)
        {
            var unionName = union.unionName.Identifier;
            HoistArguments(file, union.baseFields, unionName, IonTypeSites.Describe(union));

            foreach (var @case in union.cases)
            {
                // Both spellings of a case name go through the same rejection. `msg { … }` and
                // `msg { … }(a: i4)` are one mistake — a case with no name — and the argument list
                // is not what makes it one. Branching on `IsTypeRef` here meant the second form was
                // never even looked at: it went to `RejectInlineInArguments`, which inspects the
                // case name's *generic arguments* and never its own body, so the file compiled and
                // the unlexable placeholder reached the IR and `ion.lock.json` as a case literally
                // named `$inline`. `RejectInline` falls through to the argument walk itself, so the
                // generic arguments of a well-formed case name are still reached.
                RejectInline(@case.caseName, "a union case",
                    "Declare it as a named 'msg' and write 'case <Name>'.");

                // A rejected case name yields nothing to derive an owner path from — `U$inlineData`
                // would be a synthesized name in the lock — and `TransformStage` drops the case
                // outright, so nothing written inside its argument list can reach the IR either.
                if (@case.caseName.IsInline)
                    continue;

                HoistArguments(file, @case.arguments, unionName + Pascal(@case.caseName.Name.Identifier),
                    IonTypeSites.Describe(@case));
            }
        }

        foreach (var service in file.serviceSyntaxes)
        {
            var serviceName = service.serviceName.Identifier;
            HoistArguments(file, service.BaseArguments, serviceName, IonTypeSites.Describe(service));

            foreach (var method in service.Methods)
            {
                HoistArguments(file, method.arguments, serviceName + Pascal(method.methodName.Identifier),
                    IonTypeSites.Describe(method));

                if (method.returnType is not null)
                    RejectInline(method.returnType, "the return type of a method",
                        "Declare it as a named 'msg' and return that name.");
            }
        }

        foreach (var typedef in file.typedefSyntaxes)
        {
            RejectInline(typedef.TypeName, "the name of a typedef",
                "A typedef's name side introduces a name; write 'typedef <Name> = <type>;'.");

            if (typedef.BaseType is not null)
                RejectInline(typedef.BaseType, "the underlying type of a typedef",
                    "Write 'msg <Name> { … }' directly — an alias for an anonymous type has nothing to alias.");
        }

        foreach (var @enum in file.enumSyntaxes)
            RejectInline(@enum.Type, "the base type of an enum",
                "An enum base type must be an integral builtin.");

        foreach (var flags in file.flagsSyntaxes)
            RejectInline(flags.Type, "the base type of a flags declaration",
                "A flags base type must be an integral builtin.");

        foreach (var attribute in file.attributeDefSyntaxes)
        foreach (var parameter in attribute.Args)
            RejectInline(parameter.type, "the type of an attribute parameter",
                "An attribute parameter must be a builtin type.");
    }

    private void HoistFields(IonFileSyntax file, List<IonFieldSyntax> fields, string ownerPath, string ownerDescription)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var rewritten = Hoist(file, field.Type, ownerPath, field.Name.Identifier,
                $"the field '{field.Name.Identifier}' of {ownerDescription}");

            if (!ReferenceEquals(rewritten, field.Type))
                fields[i] = field with { Type = rewritten };
        }
    }

    private void HoistArguments(IonFileSyntax file, List<IonArgumentSyntax> arguments, string ownerPath,
        string ownerDescription)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var rewritten = Hoist(file, argument.type, ownerPath, argument.argName.Identifier,
                $"the argument '{argument.argName.Identifier}' of {ownerDescription}");

            if (!ReferenceEquals(rewritten, argument.type))
                arguments[i] = argument with { type = rewritten };
        }
    }

    // ── Hoisting ───────────────────────────────────────────────────────

    /// <summary>
    /// Lifts <paramref name="written"/> if it is an inline body, and returns the reference to put in
    /// its place — the same node when there was nothing to lift.
    /// </summary>
    private IonUnderlyingTypeSyntax Hoist(IonFileSyntax file, IonUnderlyingTypeSyntax written, string ownerPath,
        string memberName, string siteDescription)
    {
        // A generic argument is not a hoistable position at any depth, and the outer type may be a
        // perfectly good `Map<string, msg { … }>` — so this is checked whether or not the type
        // itself is inline.
        RejectInlineInArguments(written);

        if (written.InlineBody is not { } body)
            return written;

        var derived = ownerPath + Pascal(memberName);

        // Innermost first, so `OrderHistoryAt` is claimed and appended before the `OrderHistory`
        // that references it, and so the chain is built off the derived name rather than the
        // original owner.
        HoistFields(file, body.Fields, derived, $"msg '{derived}'");

        if (!Claim(derived, body, siteDescription))
            // Already reported (ION0067), and the site is left exactly as the author wrote it.
            //
            // It used to be rewritten to the derived name anyway, on the reasoning that this kept
            // the mistake to one diagnostic. That only holds when the holder of the name happens to
            // be a *type*. When it is not, the rewrite hands every later stage a name the author
            // never typed, at the span they wrote as `msg { … }`: a `service` or an `attribute` is
            // not in the type namespace, so ION0009 followed ("did you mean 'Order'?"); a `mixin`
            // is, but only as a mixin, so ION0066 followed; and when the name belonged to the
            // *including* message the rewrite made that message own itself, so ION0030 followed
            // with an invented `AS → AS` cycle and advice about a field nobody wrote.
            //
            // Leaving the body in place costs nothing: the `$inline` placeholder is unlexable and
            // already ignored by `RestoreUnresolvedTypeStage` (no ION0009), by
            // `GenericTypeValidationStage` (no ION0060/ION0061) and by `TransformStage`, and it
            // matches no mixin name, so ION0066 cannot fire on it either.
            return written;

        var reference = written with
        {
            Name = new IonIdentifier(derived)
            {
                StartPosition = body.StartPosition,
                EndPosition = body.EndPosition,
                SourceFile = body.SourceFile
            },
            InlineBody = null
        };

        // Doc and attributes written on the inline body belong to the type it becomes. Those on the
        // *field* stay on the field, which is what `fields[i] = field with { Type = … }` preserves.
        var hoistedType = new IonMessageSyntax(reference.Name, body.Fields)
        {
            Comments = body.Comments,
            StartPosition = body.StartPosition,
            EndPosition = body.EndPosition,
            SourceFile = body.SourceFile
        }.WithAttributes(body.Attributes);

        file.messageSyntaxes.Add(hoistedType);

        return reference;
    }

    /// <summary>Takes ownership of <paramref name="derived"/>, or reports why it cannot.</summary>
    /// <remarks>
    /// The already-hoisted set is consulted <em>first</em>. Both dictionaries would answer for a
    /// name this stage produced, and only one of the two messages is useful: "it collides with
    /// msg 'OrderTraceId'" points at a declaration the author cannot find in their file, whereas
    /// naming the other field says exactly which two lines pascal-case to the same thing.
    /// </remarks>
    private bool Claim(string derived, IonInlineMessageSyntax body, string siteDescription)
    {
        if (_hoisted.TryGetValue(derived, out var other))
        {
            Error(IonAnalyticCodes.ION0067_InlineTypeNameCollisionBetweenInlineTypes, body, derived, other,
                siteDescription);
            return false;
        }

        if (_claimed.TryGetValue(derived, out var holder))
        {
            Error(IonAnalyticCodes.ION0067_InlineTypeNameCollision, body, derived, holder, siteDescription);
            return false;
        }

        _hoisted[derived] = siteDescription;
        return true;
    }

    // ── Positions that cannot be hoisted ───────────────────────────────

    private void RejectInline(IonUnderlyingTypeSyntax written, string position, string remedy)
    {
        if (written.InlineBody is { } body)
        {
            Error(IonAnalyticCodes.ION0068_InlineTypeNotAllowedHere, body, position, remedy);
            return;
        }

        RejectInlineInArguments(written);
    }

    private void RejectInlineInArguments(IonUnderlyingTypeSyntax written)
    {
        foreach (var generic in written.generics)
        {
            if (generic.Type is not { } argument)
                continue;

            RejectInline(argument, "a generic argument",
                "Declare it as a named 'msg' and write that name as the argument.");
        }
    }

    // ── Naming ─────────────────────────────────────────────────────────

    /// <summary>
    /// <c>shipping</c> → <c>Shipping</c>, <c>trace_id</c> → <c>TraceId</c>, <c>URL</c> → <c>URL</c>.
    /// </summary>
    /// <remarks>
    /// Only the first letter of each <c>_</c> separated run is touched; the rest is left exactly as
    /// written, so an already-camel or already-acronym name is not mangled. Two field names that
    /// pascal-case to the same thing (<c>trace_id</c> and <c>traceId</c>) are a real collision and
    /// are reported as one — see <see cref="Claim"/>.
    /// </remarks>
    private static string Pascal(string name)
    {
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return name;

        return string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
