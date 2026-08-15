namespace ion.compiler.CodeGen;

using ion.runtime;

/// <summary>
/// Shared by the three <c>CollectPartialTargets</c> walks (C#, TypeScript, Rust): turns the
/// <c>T</c> of a <c>Partial&lt;T&gt;</c> into the real declaration, even when the IR still holds an
/// <see cref="IonUnresolvedType"/> placeholder in that slot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is needed.</b> A <c>Partial&lt;T&gt;</c> reached through <em>any</em> wrapper —
/// <c>T~[]</c>, <c>T~?</c>, <c>Map&lt;K, T~&gt;</c>, <c>Set&lt;T~&gt;</c> — arrives at codegen with
/// its target still an <c>IonUnresolvedType</c>. <c>CompilationStage.ResolveTypeFor</c> runs while
/// <c>TransformStage</c> is still compiling messages, so a forward or same-file reference lowers to
/// a placeholder; <c>RestoreUnresolvedTypeStage</c> then repairs the direct case but not the
/// wrapped one. The three collectors all skip an unresolved target on purpose (a <c>~</c> on a
/// non-<c>msg</c> is rejected upstream and emitting <c>Register(...)</c> for it would not compile),
/// so the placeholder made the whole registration disappear — and the failure is silent at build
/// time: the generated field type still <em>reads</em> <c>IonPartial&lt;Doc&gt;</c>, and the
/// missing schema only surfaces at run time as a reflection-derived fallback with best-effort
/// field order, or as "not registered".
/// </para>
/// <para>
/// <b>Pre-existing, not new.</b> <c>T~[]</c> and <c>T~?</c> have the same defect today; the
/// fixtures hide it because <c>PatchEnvelope</c> also carries a direct <c>one: PatchTarget~</c>
/// field, and one direct use is enough to register the schema for every wrapped use of the same
/// target. <c>Map&lt;K, T~&gt;</c> is simply the first shape with no direct sibling. The real fix
/// belongs in <c>RestoreUnresolvedTypeStage</c>; this is the generator-side repair, which is
/// enough because a generator always has the full definition list in hand.
/// </para>
/// <para>
/// Resolution is by name against the definitions the generator was handed, which is exactly what
/// <c>CompilationStage.ResolveType</c> would have done. A name that is not there is left as
/// <see langword="null"/> so the caller still skips it — ION0009 owns that case and has already
/// reported it.
/// </para>
/// </remarks>
public static class IonPartialTargets
{
    /// <summary>
    /// The declaration a <c>Partial&lt;T&gt;</c>'s <paramref name="target"/> refers to, or
    /// <see langword="null"/> when the name resolves to nothing.
    /// </summary>
    public static IonType? Resolve(IonType target, IReadOnlyList<IonType> definitions)
    {
        if (target is not IonUnresolvedType)
            return target;

        // First-declared wins, matching CompilationStage.ResolveType's own FirstOrDefault. Only a
        // real declaration is accepted: another placeholder of the same name would put us straight
        // back where we started.
        foreach (var definition in definitions)
        {
            if (definition is not IonUnresolvedType
                && definition.name.Identifier.Equals(target.name.Identifier, StringComparison.Ordinal))
                return definition;
        }

        return null;
    }
}
