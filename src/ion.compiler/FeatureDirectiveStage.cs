namespace ion.compiler;

using ion.runtime;

/// <summary>
/// Gives <c>#feature "x"</c> a meaning: <em>this file requires feature x</em>.
/// </summary>
/// <remarks>
/// <para>
/// The directive was parsed into <c>IonFileSyntax.featureSyntaxes</c> and read by nothing. Features
/// arrived only from <c>ion.config.json</c>, so a file could declare <c>#feature "orleans"</c>, be
/// compiled by a project that does not enable orleans, and fail with an ION0005 on every
/// <c>@grainId</c> — a pile of diagnostics naming the symptom while the one line that stated the
/// requirement sat there inert.
/// </para>
/// <para>
/// It is a requirement, not a switch: a <c>.ion</c> file cannot turn a feature on. Features change
/// which builtin modules exist and which generators run, which is a project-wide decision, and
/// letting one file flip it would mean two files in the same compilation disagreeing about what
/// <c>vec3f</c> means. So the directive asserts, and the assert either holds or is ION0049.
/// </para>
/// <para>
/// <strong>Position in the pipeline.</strong> First. An unmet feature is the cause of every
/// unresolved name that follows it, and the pipeline collects all diagnostics rather than stopping,
/// so this message needs to be the one at the top of the list.
/// </para>
/// </remarks>
public sealed class FeatureDirectiveStage(CompilationContext context) : CompilationStage(context)
{
    public override string StageName => "Feature Directives";
    public override string StageDescription => "Checking '#feature' declarations against the project configuration";

    /// <summary>Report every unmet requirement, not just the first.</summary>
    public override bool StopOnError => false;

    public override void DoProcess()
    {
        var enabled = new HashSet<string>(Context.Features, StringComparer.OrdinalIgnoreCase);

        foreach (var file in Context.Files)
        {
            foreach (var directive in file.featureSyntaxes)
            {
                var requested = directive.featureName;

                // Unknown first. "Add 'vectr' to ion.config.json" would be actively harmful advice —
                // the config would then be rejected by its own feature converter, or accepted and
                // silently map to no module at all.
                if (!IonModule.KnownFeatures.Contains(requested, StringComparer.OrdinalIgnoreCase))
                {
                    Error(IonAnalyticCodes.ION0049_UnknownFeature, directive,
                        requested, Quoted(IonModule.KnownFeatures));
                    continue;
                }

                if (enabled.Contains(requested))
                    continue;

                Error(IonAnalyticCodes.ION0049_FeatureNotEnabled, directive,
                    requested,
                    file.Name,
                    Context.Features.Count == 0 ? "(none)" : Quoted(Context.Features));
            }
        }
    }

    private static string Quoted(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));
}
