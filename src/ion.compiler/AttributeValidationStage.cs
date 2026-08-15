namespace ion.compiler;

using ion.runtime;
using syntax;

/// <summary>
/// Checks every attribute <em>use</em> against the declaration it names: that the attribute exists
/// (ION0005), that it is legal in that position (ION0038), and that its arguments match the declared
/// parameters in count, name and type (ION0032–ION0037).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a separate stage.</strong> <see cref="TransformStage"/> already visits every
/// attribute — it has to, to lower them — so the checks could have lived there. They do not, for two
/// reasons. It visits some of them more than once (a service base argument is copied into every
/// method by <c>PrependMethods</c>, so a bad attribute on one was reported once per method), and it
/// visits them without knowing what they are attached to: by the time an inline union case and a
/// <c>msg</c> are both an <see cref="IonType"/>, the target has been erased. Walking
/// <see cref="IonAttributeSites"/> over the syntax tree gives each attribute exactly once, with its
/// target.
/// </para>
/// <para>
/// <strong>Cascade control.</strong> An undeclared attribute stops at ION0005. Nothing is known
/// about its parameters, so an arity or type complaint would be invented rather than derived — the
/// author has one thing to fix, and it is the name. Within a use, the binder applies the same rule
/// to the missing-argument check (see <see cref="IonAttributeBinder"/>).
/// </para>
/// <para>
/// Declaration-side rules are <em>not</em> here: ION0003 / ION0004 (parameter type),
/// ION0038's unknown-target variant, and ION0039 (a required parameter after an optional one) are
/// raised by <c>TransformStage.CompileAttributes</c>, which is where the declaration is lowered and
/// where the resolved parameter types exist.
/// </para>
/// </remarks>
public sealed class AttributeValidationStage(CompilationContext context) : CompilationStage(context)
{
    public override string StageName => "Attribute Validation";

    public override string StageDescription =>
        "Checking attribute arity, argument types, named arguments and targets";

    /// <summary>Collect every bad attribute, don't stop at the first.</summary>
    public override bool StopOnError => false;

    public override void DoProcess()
    {
        foreach (var file in Context.Files)
        foreach (var site in IonAttributeSites.Of(file))
            Validate(site);
    }

    private void Validate(IonAttributeSite site)
    {
        var use = site.Attribute;
        var declaration = Context.ResolveAttributeType(use.Name.Identifier);

        if (declaration is null)
        {
            Error(IonAnalyticCodes.ION0005_AttributeNotFoundOrMissingDependency, use, use.Name.Identifier);
            return;
        }

        // Checked before the target and the arguments: a reserved marker takes no arguments and is
        // declared on exactly the positions the compiler attaches it to, so an author who wrote one
        // in a legal position would otherwise get no diagnostic at all, and one who wrote it
        // elsewhere would get ION0038's target message — which reads as "put it somewhere else"
        // when the answer is "do not write it".
        if (IonReservedAttributes.IsReserved(use.Name.Identifier))
        {
            Error(IonAnalyticCodes.ION0038_AttributeIsCompilerInternal, use, use.Name.Identifier);
            return;
        }

        // `targets is null` means the declaration had no `on` clause and is unrestricted; Allows
        // handles that, and the message below only runs when there is a clause to quote.
        if (!declaration.Allows(site.Target))
            Error(IonAnalyticCodes.ION0038_AttributeTargetNotAllowed, use,
                declaration.name.Identifier,
                site.Target.Describe(),
                IonAttributeTargets.Format(declaration.targets!));

        foreach (var problem in IonAttributeBinder.Bind(declaration, use).Problems)
            Error(problem.Code, problem.Node, problem.Args);
    }
}
