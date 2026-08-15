namespace ion.compiler.CodeGen;

using ion.runtime;
using ion.syntax;

/// <summary>
/// Diagnostics raised by a code generator rather than by the compilation pipeline.
/// </summary>
/// <remarks>
/// <para>
/// These describe a <em>target capability limit</em>, not a language error: the Ion source is
/// valid and every other target compiles it. They therefore live on the generation path and are
/// reported only for the target that cannot express the construct — a project that does not
/// enable that generator is unaffected.
/// </para>
/// <para>
/// Codes are allocated from the same space as <c>ion.compiler.IonAnalyticCodes</c> and must not
/// collide with it: ION0001–ION0013 pipeline, ION0014–ION0017 typedefs, ION0018–ION0019 the
/// <c>Partial&lt;T&gt;</c> language rules, ION0020–ION0029 schema lock, ION0030 circular
/// references and builtin shadowing, ION0032–ION0039 attribute semantics, ION0040–ION0049 modules
/// and features, ION0060–ION0068 language features, ION1001–ION1002 unused symbols and ION1004
/// deprecated usage. ION0050–ION0059 is
/// reserved here for target capability limits, which are a different kind of thing from every
/// band above — the schema is legal, one backend just cannot express it. Allocated so far:
/// ION0051 a partial's field name.
/// </para>
/// <para>
/// <strong>Retired: ION0050, ION0052 and ION0053.</strong> All three were Go-only refusals —
/// <c>Partial&lt;T&gt;</c>, <c>decimal</c>, and <c>Map&lt;K,V&gt;</c> / <c>Set&lt;T&gt;</c> /
/// <c>T[N]</c> respectively — raised by a walk in <c>GoCodeGenerator</c> before it emitted
/// anything. The Go target was removed (there was never a Go runtime in this repository, and
/// nothing the generator emitted was ever compiled or verified here), which made all three
/// unreachable, so they were deleted rather than left inert. The three numbers stay burned: they
/// are not to be reused for an unrelated diagnostic, because a reader who meets ION0052 in an old
/// build log or an archived CI run should not be told it meant something else. ION0051 is live
/// and belongs to Rust.
/// </para>
/// </remarks>
public static class IonCodeGenDiagnostics
{
    /// <summary>ION0051 — the target can represent <c>Partial&lt;T&gt;</c>, but not this field name.</summary>
    public const string PartialFieldNameUnrepresentable = "ION0051";

    /// <summary>
    /// A field of a <c>Partial&lt;T&gt;</c> target is spelled with a Rust keyword.
    /// </summary>
    /// <remarks>
    /// <c>ion_partial!</c> derives the CBOR map keys from the field idents with
    /// <c>stringify!</c>, which preserves the raw-identifier prefix — a field written
    /// <c>r#type</c> would go on the wire as the key <c>"r#type"</c> and desynchronise from the
    /// C# and TypeScript runtimes. Silently emitting that is worse than refusing to.
    /// </remarks>
    public static IonDiagnostic PartialFieldIsRustKeyword(string messageName, string fieldName)
        => new(
            PartialFieldNameUnrepresentable,
            IonDiagnosticSeverity.Error,
            $"Cannot generate the Rust patch struct for 'Partial<{messageName}>': field " +
            $"'{fieldName}' is a Rust keyword, and the generated wire key would become " +
            $"'r#{fieldName}' instead of '{fieldName}'. Rename the field, or drop the rust " +
            $"generator from ion.config.json.",
            new IonSyntaxBase());
}
