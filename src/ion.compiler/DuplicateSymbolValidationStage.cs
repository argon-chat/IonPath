namespace ion.compiler;

using syntax;

public sealed class DuplicateSymbolValidationStage(CompilationContext context)
    : CompilationStage(context)
{
    public override string StageName => "Symbol Validation";
    public override string StageDescription => "Checking for duplicate definitions and symbol conflicts";
    public override bool StopOnError => false; // Collect ALL duplicates, don't stop

    public override void DoProcess()
    {
        var builtins = BuiltinTypeNames();
        var builtinAttributes = BuiltinAttributeNames();
        var nameToDef = new Dictionary<string, IonSyntaxMember>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in Context.Files)
        {
            // Mixins are not in `Definitions` — they are not types, and every stage that walks that
            // list treats its members as types. They still share the one flat declaration namespace:
            // `mixin Audited` beside `msg Audited` would leave a `with Audited` and an
            // `x: Audited` naming two different things, and `mixin u4` is unreachable for exactly
            // the reason ION0031 exists. So they are concatenated in here, and nowhere else.
            foreach (var def in module.Definitions.Concat<IonSyntaxMember>(module.mixinSyntaxes))
            {
                if (Declaration(def) is not { } declaration)
                    continue;

                var (name, kind, nameNode) = declaration;

                // A builtin is unshadowable, not merely duplicated: ResolveTypeFor consults
                // ResolveBuiltinType first, so the declaration is unreachable rather than ambiguous.
                // Ordinal on purpose — ResolveBuiltinType compares with string.Equals, so `msg U4`
                // does *not* shadow `u4` and must not be reported as if it did.
                if (def is not IonServiceSyntax && builtins.TryGetValue(name, out var owner))
                    Error(IonAnalyticCodes.ION0031_DeclarationShadowsBuiltin, nameNode, kind, name, owner);

                // The attribute namespace is separate but has the identical hazard, via the identical
                // mechanism: ResolveAttributeType searches GlobalModules before ProcessedModules, so
                // `attribute @deprecated(x: string);` was a silent no-op — no diagnostic, and every
                // `@deprecated` in the project still bound to the std signature.
                if (def is IonAttributeDefSyntax && builtinAttributes.TryGetValue(name, out var attrOwner))
                    Error(IonAnalyticCodes.ION0031_AttributeShadowsBuiltin, nameNode, name, attrOwner);

                if (nameToDef.TryGetValue(name, out var existing))
                    Error(IonAnalyticCodes.ION0002_DuplicateDefinition, def, name,
                        module.file.FullName, existing.SourceFile?.FullName ?? "unknown");
                else
                    nameToDef[name] = def;
            }
        }
    }

    /// <summary>
    /// Every builtin type name in scope, mapped to the module that declares it.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="CompilationContext.GlobalModules"/> rather than a hardcoded list, so
    /// it tracks <c>IonModule.GetStdModule</c> / <c>GetOrleansModule</c> automatically and respects
    /// the enabled feature set — a name only collides when the module that declares it is actually
    /// in scope. The builtin generics <c>Maybe</c> / <c>Array</c> / <c>Partial</c> are
    /// <c>IonGenericType</c>s carrying the same <c>builtin</c> attribute, so they are covered by the
    /// same predicate.
    /// </remarks>
    private Dictionary<string, string> BuiltinTypeNames() =>
        Context.GlobalModules
            .SelectMany(module => module.Definitions
                .Where(definition => definition.IsBuiltin)
                .Select(definition => (Name: definition.name.Identifier, Module: module.Name)))
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Module, StringComparer.Ordinal);

    /// <summary>
    /// Every attribute name declared by a module in scope, mapped to the module that declares it.
    /// </summary>
    /// <remarks>
    /// The attribute counterpart of <see cref="BuiltinTypeNames"/>, derived the same way and for the
    /// same reason: it tracks whatever <c>IonModule.GetStdModule</c> / <c>GetOrleansModule</c>
    /// declare and respects the enabled feature set, so <c>attribute @grainId();</c> collides only
    /// in a project with the <c>orleans</c> feature on and is a perfectly good name without it.
    /// Ordinal, because <c>ResolveAttributeType</c> matches with <c>string.Equals</c> — <c>@Deprecated</c>
    /// does not shadow <c>@deprecated</c> and must not be reported as if it did.
    /// </remarks>
    private Dictionary<string, string> BuiltinAttributeNames() =>
        Context.GlobalModules
            .SelectMany(module => module.Attributes
                .Select(attribute => (Name: attribute.name.Identifier, Module: module.Name)))
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Module, StringComparer.Ordinal);

    /// <summary>
    /// The name a declaration contributes to the type namespace, the human-readable kind used in
    /// diagnostics, and the identifier node to point at — or <see langword="null"/> when the member
    /// declares nothing (directives: <c>#use</c> / <c>#import</c> / <c>#feature</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enums, flags, unions and attribute definitions were all missing here at one point or another,
    /// so <c>typedef Foo = u4;</c> next to <c>enum Foo { .. }</c> was accepted silently and
    /// <c>CompilationContext.ResolveTypeFor</c> then picked whichever definition happened to be
    /// registered first.
    /// </para>
    /// <para>
    /// Attribute definitions are included even though <c>@Foo</c> resolves through a separate
    /// <c>ResolveAttributeType</c> lookup. Sharing one namespace is the deliberate choice: a
    /// generator that emits a C# type per declaration cannot emit <c>msg Foo</c> and
    /// <c>attribute @Foo</c> into the same namespace, and "the attribute and the message named Foo
    /// are unrelated" is not a distinction worth asking a reader to hold.
    /// </para>
    /// <para>
    /// A <c>service</c> is listed so it keeps its pre-existing place in the duplicate map, but it is
    /// exempt from the builtin check at the call site: <c>TransformStage</c> files services under
    /// <c>IonModule.Services</c> and never <c>IonModule.Definitions</c>, so <c>service u4()</c>
    /// shadows nothing and every <c>u4</c> reference still resolves to the builtin.
    /// </para>
    /// <para>
    /// The node returned is the <em>identifier</em>, not the whole declaration, so ION0031 squiggles
    /// the offending name. ION0002 keeps pointing at the declaration, as it always has.
    /// </para>
    /// </remarks>
    private static (string Name, string Kind, IonSyntaxBase NameNode)? Declaration(IonSyntaxMember def) => def switch
    {
        IonTypedefSyntax typeDef => (typeDef.TypeName.Name.Identifier, "Typedef", typeDef.TypeName.Name),
        IonMessageSyntax msg => (msg.Name.Identifier, "Message", msg.Name),
        IonMixinSyntax mixin => (mixin.Name.Identifier, "Mixin", mixin.Name),
        IonServiceSyntax service => (service.serviceName.Identifier, "Service", service.serviceName),
        IonEnumSyntax @enum => (@enum.Name.Identifier, "Enum", @enum.Name),
        IonFlagsSyntax flags => (flags.Name.Identifier, "Flags", flags.Name),
        IonUnionSyntax union => (union.unionName.Identifier, "Union", union.unionName),
        IonAttributeDefSyntax attribute => (attribute.Name.Identifier, "Attribute", attribute.Name),
        _ => null
    };
}

/// <summary>
/// Checks that a method header can mean what it says: its modifier list and its stream parameters.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is <em>not</em> an error.</strong> <c>internal stream Foo()</c> is legal, and
/// deliberately so. The two modifiers answer different questions: <c>internal</c> is about which
/// side of the generated code gets the method (omitted from the client, kept in the server
/// executor), <c>stream</c> is about the call shape. A server-to-server streaming method that no
/// generated client can reach is a coherent thing to declare, and rejecting it would be inventing a
/// rule rather than reporting one. The same goes for <c>internal unary</c>.
/// </para>
/// <para>
/// <c>unary stream</c> is the pair that cannot hold: they name opposite call shapes, and only one
/// of them is read — <c>IonMethod.IsStreamable</c> tests for <c>Stream</c> and nothing tests for
/// <c>Unary</c> — so the author's <c>unary</c> silently lost. A repeated modifier is rejected on the
/// same grounds as a repeated type modifier (ION0019): the second one cannot mean anything the
/// first did not already say.
/// </para>
/// </remarks>
public sealed class StreamParameterValidationStage(CompilationContext context)
    : CompilationStage(context)
{
    public override string StageName => "Method Signature Validation";
    public override string StageDescription => "Checking method modifiers and stream parameter constraints";
    public override bool StopOnError => false; // Collect ALL issues, don't stop

    public override void DoProcess()
    {
        foreach (var module in Context.Files)
        {
            foreach (var def in module.Definitions)
            {
                if (def is not IonServiceSyntax service)
                    continue;

                foreach (var method in service.Methods.OfType<IonMethodSyntax>())
                {
                    ValidateModifiers(method);

                    if (method.arguments.Count(p => p.modifiers == IonArgumentModifiers.Stream) > 1)
                        Error(IonAnalyticCodes.ION0013_MultipleStreamParameters, method,
                            method.methodName.Identifier);
                }
            }
        }
    }

    private void ValidateModifiers(IonMethodSyntax method)
    {
        var name = method.methodName.Identifier;

        // Reported on the *second* occurrence only, and once per distinct modifier, so
        // `stream stream stream Foo()` is one message about `stream` rather than two.
        foreach (var repeated in method.modifiers
                     .GroupBy(m => m)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
        {
            Error(IonAnalyticCodes.ION0013_DuplicateMethodModifier, method, name, Spelling(repeated));
        }

        if (method.modifiers.Contains(IonMethodModifiers.Unary) &&
            method.modifiers.Contains(IonMethodModifiers.Stream))
        {
            Error(IonAnalyticCodes.ION0013_ContradictoryMethodModifiers, method, name);
        }
    }

    /// <summary>The source keyword for a modifier, so a message quotes what the author wrote.</summary>
    private static string Spelling(IonMethodModifiers modifier) => modifier switch
    {
        IonMethodModifiers.Unary => "unary",
        IonMethodModifiers.Stream => "stream",
        IonMethodModifiers.Internal => "internal",
        _ => modifier.ToString().ToLowerInvariant()
    };
}