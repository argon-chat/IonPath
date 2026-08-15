namespace ion.compiler;

public static class IonAnalyticCodes
{
    private static readonly Dictionary<string, IonAnalyticCode> _codeMap = new();

    static IonAnalyticCodes()
    {
        // Auto-register all codes via reflection
        foreach (var field in typeof(IonAnalyticCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.FieldType == typeof(IonAnalyticCode))
            {
                var code = (IonAnalyticCode)field.GetValue(null)!;
                _codeMap[code.code] = code;
            }
        }
    }

    /// <summary>
    /// Resolve a diagnostic code string to its IonAnalyticCode definition.
    /// </summary>
    public static IonAnalyticCode? Resolve(string code) => _codeMap.GetValueOrDefault(code);

    public static readonly IonAnalyticCode ION0001_CycleImportDetected 
        = new("ION0001", "Cyclic module import detected: {0}");
    public static readonly IonAnalyticCode ION0002_DuplicateDefinition 
        = new("ION0002", "Duplicate definition of '{0}' in module '{1}', first defined here {2}");
    public static readonly IonAnalyticCode ION0003_TypeNotFoundOrNotBuiltin 
        = new("ION0003", "Type '{0}' not found or is not a standard builtin type.");
    /// <summary>
    /// A type that resolved to a builtin, in a position that builtin may not occupy.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ION0003_TypeNotFoundOrNotBuiltin"/>, which is about a name that
    /// could not be resolved at all. Here the name resolved fine and the <em>position</em> is what
    /// rejects it, so the two are never both right about the same token.
    /// </remarks>
    public static readonly IonAnalyticCode ION0004_TypeNotAllowedInAttributeArguments
        = new("ION0004", "Type '{0}' is not allowed in attribute arguments.");

    /// <summary>
    /// An <c>enum</c> / <c>flags</c> base type that is a builtin but not an integral one.
    /// </summary>
    /// <remarks>
    /// <c>enum E : string { .. }</c> and <c>flags F : bool { .. }</c> used to compile clean and then
    /// lower every member to an <c>IonConstant</c> whose <c>type</c> was <c>string</c> while
    /// its <c>constantValue</c> was a decimal integer — a member no target could emit and no reader
    /// could decode. The member values of an enum are integers by construction (they are auto
    /// numbered, and <c>flags</c> shifts bits through them), so the base type has to be able to hold
    /// one.
    /// <para>
    /// <c>{0}</c> is the written base type, <c>{1}</c> the declaration kind, <c>{2}</c> its name and
    /// <c>{3}</c> the integral builtins that are accepted.
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0004_EnumBaseTypeNotIntegral
        = new("ION0004",
            "Type '{0}' cannot be the base type of {1} '{2}'. Members are numbered, so the base " +
            "must be an integral builtin: {3}.");
    public static readonly IonAnalyticCode ION0005_AttributeNotFoundOrMissingDependency
        = new("ION0005", "Attribute '{0}' not found. It may be missing a required import or feature.");
    public static readonly IonAnalyticCode ION0006_DuplicateEnumName
        = new("ION0006", "Duplicate enum item name '{0}' in enum '{1}', first defined here {2}");
    public static readonly IonAnalyticCode ION0007_InvalidEnumValue
        = new("ION0007", "Invalid value '{0}' for enum '{1}': value must be a constant integer.");
    public static readonly IonAnalyticCode ION0008_DuplicateEnumValue
        = new("ION0008", "Duplicate enum value '{0}' in enum '{1}', previously assigned to '{2}' at {3}");
    public static readonly IonAnalyticCode ION0009_UnresolvedTypeReference
        = new("ION0009", "Unresolved reference to type '{0}'. The type may be missing, misspelled, or not imported.");
    public static readonly IonAnalyticCode ION0009_UnresolvedTypeReferenceWithSuggestion
        = new("ION0009", "Unresolved reference to type '{0}'. Did you mean '{1}'?");

    /// <summary>
    /// Modifier suffixes written in an order other than the canonical <c>~</c>, <c>[]</c>, <c>?</c>.
    /// </summary>
    /// <remarks>
    /// <c>CompilationContext.WrapModifiers</c> always wraps Partial innermost, then Array, then
    /// Maybe, so <c>Data?~</c> and <c>Data~?</c> lower to the identical
    /// <c>Maybe&lt;Partial&lt;Data&gt;&gt;</c>. Only the canonical spelling reads the way the type is
    /// actually built, so the other permutations are rejected rather than left as synonyms.
    /// <para>
    /// <c>{0}</c> is the type as written, <c>{1}</c> the lowered IR it silently produces and
    /// <c>{2}</c> the canonical spelling to write instead.
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0010_TypeModifierOutOfOrder
        = new("ION0010",
            "Type '{0}' writes its modifiers out of order, so it silently compiles as '{1}'. " +
            "Modifiers apply inside-out and must be written '~', then '[]', then '?' — write '{2}'.");

    public static readonly IonAnalyticCode ION0011_EnumBitwiseOverlap
        = new("ION0011", "Enum item '{0}' in '{1}' has overlapping bits with '{2}', both resolve to value '{3}'");

    public static readonly IonAnalyticCode ION0012_UnionSharedFieldsWithReferencedCase
        = new("ION0012", "Union '{0}' declares shared fields but contains case '{1}' that is a type reference; unions with referenced cases cannot declare shared fields.");

    // ── Method signature shape (ION0013) ──
    //
    // One code, three variants: everything here says "this method header cannot mean what it says".
    // They share a slot because the fix is always the same kind of edit — delete one of the words —
    // and because the band ION0001–ION0013 is full.

    public static readonly IonAnalyticCode ION0013_MultipleStreamParameters
        = new("ION0013", "Method '{0}' declares multiple stream parameters; only one parameter may be marked as 'stream'.");

    /// <summary>
    /// <c>unary stream Foo();</c> — the two modifiers name opposite call shapes.
    /// </summary>
    /// <remarks>
    /// Nothing reads <c>unary</c>: <c>IonMethod.IsStreamable</c> tests only for <c>Stream</c>, so the
    /// pair silently produced a streaming method and the author's <c>unary</c> evaporated. <c>unary</c>
    /// survives as the explicit spelling of the default precisely so a reader can trust it, which it
    /// cannot if <c>stream</c> may sit beside it and win.
    /// </remarks>
    public static readonly IonAnalyticCode ION0013_ContradictoryMethodModifiers
        = new("ION0013",
            "Method '{0}' is declared both 'unary' and 'stream'. 'unary' is the explicit spelling of " +
            "the default single request/response shape, so the two cannot both apply — remove one.");

    /// <remarks><c>{1}</c> is the repeated modifier keyword.</remarks>
    public static readonly IonAnalyticCode ION0013_DuplicateMethodModifier
        = new("ION0013", "Method '{0}' repeats the '{1}' modifier. Write it once.");

    // ── Typedef codes (ION0014–ION0017) ──

    public static readonly IonAnalyticCode ION0014_TypedefWithoutUnderlyingType
        = new("ION0014", "Typedef '{0}' declares no underlying type. Write 'typedef {0} = <type>;'.");

    public static readonly IonAnalyticCode ION0015_TypedefNameModifier
        = new("ION0015",
            "Typedef '{0}' applies the '{1}' modifier to the alias name, which has no meaning. Move it to the underlying type ('typedef {0} = <type>{1};') or apply it at each use site.");

    public static readonly IonAnalyticCode ION0016_GenericTypedefNotSupported
        = new("ION0016", "Generic typedef '{0}<{1}>' is not supported. Declare a non-generic alias instead.");

    public static readonly IonAnalyticCode ION0017_CircularTypedef
        = new("ION0017",
            "Circular typedef: {0}. A typedef is a transparent alias, so the chain must terminate in a concrete type.");

    // ── Partial (`T~`) codes (ION0018–ION0019) ──

    /// <remarks>
    /// <para>
    /// <c>{0}</c> is the type exactly as written at the use site (so an alias is reported under the
    /// name the author typed, not under whatever it erases to); <c>{1}</c> is a predicate phrase
    /// produced by <c>PartialTypeValidationStage.Describe</c> and reads on from "…: ".
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0018_PartialOverNonMessage
        = new("ION0018",
            "The partial modifier '~' cannot be applied to '{0}': {1}. " +
            "Only a user-defined 'msg' can be made partial ('Data~', 'Data~?', 'Data~[]').");

    /// <summary>
    /// A modifier suffix written more than once — <c>Data~~</c>, <c>Data??</c>, <c>Data[][]</c>.
    /// </summary>
    /// <remarks>
    /// This is the code this slot was reserved for. It could not be raised until
    /// <c>IonUnderlyingTypeSyntax.ModifierTokens</c> existed: the grammar reduced the suffixes to
    /// three <see cref="bool"/>s with <c>.Contains</c>, so the repeat left no trace anywhere in the
    /// syntax tree and the author silently got a different type than they wrote.
    /// <para>
    /// The repeat is rejected rather than lowered because the doubled form has no wire
    /// representation: <c>Partial&lt;Partial&lt;T&gt;&gt;</c> is not encodable (see
    /// <see cref="PartialTypeValidationStage"/>), and neither <c>Maybe&lt;Maybe&lt;T&gt;&gt;</c> nor
    /// a jagged <c>T[][]</c> exists in the IR.
    /// </para>
    /// <para>
    /// <c>{0}</c> is the type as written, <c>{1}</c> the repeated token, <c>{2}</c> the lowered IR it
    /// silently produces and <c>{3}</c> the de-duplicated canonical spelling.
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0019_DuplicateTypeModifier
        = new("ION0019",
            "Type '{0}' repeats the '{1}' modifier, which Ion cannot represent: it still compiles as " +
            "'{2}', exactly as if it were written '{3}'. Remove the duplicate '{1}'.");

    // ── Schema Lock validation codes (ION0020–ION0029) ──

    /// <summary>
    /// A cycle made entirely of <em>unconditionally owned</em> field references.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a bare <c>T</c> field is an owned edge. <c>T?</c>, <c>T[]</c>, <c>T~</c> and a union arm
    /// are cycle-breaking — each can terminate at runtime (absent, empty, an empty patch, a different
    /// case) — so a cycle running through one of them is finite on the wire and is not reported.
    /// </para>
    /// <para>
    /// The old rule unwrapped <c>Maybe</c> / <c>Array</c> / <c>Partial</c> <em>before</em> testing,
    /// which rejected every tree, graph, comment thread and org chart while the message told the
    /// author to apply the very modifier that had just been stripped. It also early-returned on a
    /// direct self reference, so <c>msg A { a: A; }</c> — the one shape that is genuinely infinite —
    /// compiled silently.
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0030_CircularTypeReference
        = new("ION0030",
            "Circular type reference detected: {0}. Every step on this path is an unconditionally " +
            "owned field, so a value of this type can never be finite. Break the cycle by making one " +
            "of those fields optional ('T?'), an array ('T[]') or partial ('T~').");

    /// <summary>
    /// A declaration whose name is already taken by a builtin type.
    /// </summary>
    /// <remarks>
    /// <c>CompilationContext.ResolveTypeFor</c> calls <c>ResolveBuiltinType</c> before it looks at
    /// anything the project declared, so <c>typedef u4 = i8;</c> or <c>msg u4 { … }</c> produced a
    /// declaration that no reference could ever reach — every <c>u4</c> in the file still meant the
    /// builtin. Both used to report "Check passed".
    /// <para>
    /// <c>{0}</c> is the declaration kind, <c>{1}</c> the colliding name and <c>{2}</c> the builtin
    /// module that owns it — derived from <see cref="CompilationContext.GlobalModules"/>, so a
    /// feature-gated builtin only collides when its feature is enabled.
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0031_DeclarationShadowsBuiltin
        = new("ION0031",
            "{0} '{1}' has the same name as the builtin type '{1}' from module '{2}'. Builtins win " +
            "type resolution, so this declaration could never be referenced — rename it.");

    /// <summary>
    /// An <c>attribute</c> declaration whose name is already taken by a std attribute.
    /// </summary>
    /// <remarks>
    /// The attribute namespace has exactly the same hazard as the type namespace, and the same
    /// cause: <c>CompilationContext.ResolveAttributeType</c> searches
    /// <see cref="CompilationContext.GlobalModules"/> before it looks at anything the project
    /// declared, so <c>attribute @deprecated(x: string);</c> produced a declaration no <c>@deprecated</c>
    /// could ever reach — every use still bound to the std signature, and a use written against the
    /// author's own signature failed with an arity error pointing at a declaration that looked
    /// correct. It reported "Check passed" before.
    /// <para>
    /// <c>{0}</c> is the colliding attribute name and <c>{1}</c> the builtin module that owns it.
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0031_AttributeShadowsBuiltin
        = new("ION0031",
            "Attribute '@{0}' has the same name as the builtin attribute '@{0}' from module '{1}'. " +
            "Builtins win attribute resolution, so this declaration could never be referenced — rename it.");

    // ── Attribute semantics (ION0032–ION0039) ──
    //
    // ION0004 (parameter type not allowed) and ION0005 (attribute not found) predate this band and
    // stay where they are. Everything below is about a *use* of an attribute except ION0038's
    // second variant and ION0039, which are declaration-side.
    //
    // Every message names the attribute, the parameter it is about, and what was expected against
    // what was written, because an attribute's signature is not visible at the use site — unlike a
    // field type, the reader cannot see what they got wrong without being told.

    /// <summary>
    /// Fewer arguments than the declaration has required (non-<c>T?</c>) parameters.
    /// </summary>
    /// <remarks>
    /// <c>{0}</c> attribute name, <c>{1}</c> the quoted names of the unbound required parameters,
    /// <c>{2}</c> the full signature so the fix is readable without opening the declaration.
    /// <para>
    /// Suppressed when any per-argument error was already reported for the same use: a rejected
    /// argument leaves its parameter unbound, and "you also forgot it" is noise on top of the real
    /// mistake.
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0032_AttributeTooFewArguments
        = new("ION0032",
            "Attribute '@{0}' is missing required argument{1} {2}. Expected signature: '@{0}({3})'.");

    /// <remarks><c>{1}</c> is the declared parameter count, <c>{2}</c> the number written.</remarks>
    public static readonly IonAnalyticCode ION0032_AttributeTooManyArguments
        = new("ION0032",
            "Attribute '@{0}' takes {1} argument(s) but {2} were given. Expected signature: '@{0}({3})'.");

    /// <summary>
    /// A literal that cannot be converted to the declared parameter type at all.
    /// </summary>
    /// <remarks>
    /// <c>{0}</c> attribute, <c>{1}</c> parameter (an array element reads as <c>items[2]</c>),
    /// <c>{2}</c> the declared type, <c>{3}</c> a phrase describing what was written — see
    /// <c>IonAttributeBinder.Describe</c>. Distinct from
    /// <see cref="ION0034_AttributeArgumentOutOfRange"/>, which is the right <em>kind</em> of
    /// literal and only the wrong magnitude.
    /// </remarks>
    public static readonly IonAnalyticCode ION0033_AttributeArgumentTypeMismatch
        = new("ION0033", "Attribute '@{0}' argument '{1}' expects '{2}', but {3} was given.");

    /// <remarks>
    /// Split from the general mismatch because the fix is specific and worth naming: optionality is
    /// how an attribute parameter is made omittable, and <c>T?</c> is the only spelling for it
    /// (there is no <c>= default</c>). <c>{2}</c> is the declared type without the <c>?</c>.
    /// </remarks>
    public static readonly IonAnalyticCode ION0033_AttributeArgumentNullNotAllowed
        = new("ION0033",
            "Attribute '@{0}' argument '{1}' is not optional, so 'null' is not allowed. " +
            "Declare it as '{2}?' to make it omittable.");

    /// <summary>
    /// The right kind of literal, outside the declared type's range — <c>@bits(300)</c> on a
    /// <c>u1</c> parameter, or an <c>f8</c>-sized magnitude in an <c>f4</c>.
    /// </summary>
    /// <remarks>
    /// <c>{2}</c> is the literal exactly as written (its raw text, so <c>0xFF</c> is not silently
    /// restated as <c>255</c>) and <c>{4}</c> states the limit that was crossed.
    /// </remarks>
    public static readonly IonAnalyticCode ION0034_AttributeArgumentOutOfRange
        = new("ION0034", "Attribute '@{0}' argument '{1}': the value {2} does not fit in '{3}' ({4}).");

    public static readonly IonAnalyticCode ION0035_UnknownNamedAttributeArgument
        = new("ION0035", "Attribute '@{0}' has no parameter named '{1}'. Its parameters are: {2}.");

    public static readonly IonAnalyticCode ION0035_UnknownNamedAttributeArgumentWithSuggestion
        = new("ION0035", "Attribute '@{0}' has no parameter named '{1}'. Did you mean '{2}'?");

    public static readonly IonAnalyticCode ION0036_DuplicateNamedAttributeArgument
        = new("ION0036", "Attribute '@{0}' argument '{1}' is specified more than once.");

    /// <remarks><c>{2}</c> is the 1-based position the value was already supplied at.</remarks>
    public static readonly IonAnalyticCode ION0036_NamedAttributeArgumentAlreadyPositional
        = new("ION0036",
            "Attribute '@{0}' argument '{1}' was already supplied positionally (argument {2}), " +
            "so it cannot also be given by name.");

    /// <remarks>
    /// <c>{1}</c> is the 1-based position of the offending positional argument. Reported once per
    /// stray argument; the resulting unbound parameters do not additionally raise ION0032.
    /// </remarks>
    public static readonly IonAnalyticCode ION0037_PositionalAfterNamedAttributeArgument
        = new("ION0037",
            "Attribute '@{0}': positional argument {1} follows a named argument. " +
            "Once an argument is named, every argument after it must be named too.");

    /// <summary>
    /// An attribute written somewhere its <c>on</c> clause forbids.
    /// </summary>
    /// <remarks>
    /// <c>{1}</c> names the position in prose ("a field"), <c>{2}</c> repeats the declared clause.
    /// A declaration with no <c>on</c> clause is unrestricted and never reaches this.
    /// </remarks>
    public static readonly IonAnalyticCode ION0038_AttributeTargetNotAllowed
        = new("ION0038", "Attribute '@{0}' cannot be applied to {1}. It is declared 'on {2}'.");

    /// <summary>
    /// A compiler-synthesized marker attribute written by hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>@builtin</c>, <c>@scalar</c>, <c>@union</c> and <c>@unionCase</c> are attached to the IR by
    /// the compiler — <c>IonModule.GetStdModule</c> puts the first two on every builtin definition
    /// and <c>TransformStage</c> puts the last two on a union and its inline cases. They stay
    /// <em>declared</em> so a stray use resolves and lands somewhere sane instead of being a silently
    /// accepted no-op, but they are not user vocabulary.
    /// </para>
    /// <para>
    /// Writing one is rejected rather than deduplicated. <c>@union</c> on a union produced a second
    /// <c>IonUnionAttributeInstance</c> beside the synthesized marker, which breaks any consumer
    /// doing <c>.Single(a =&gt; a.IsUnion)</c>; and the other two are worse than redundant — a
    /// hand written <c>@builtin</c> on a <c>msg</c> makes <c>IonType.IsBuiltin</c> answer true, which
    /// excludes the message from cycle detection and makes every generator treat it as a primitive
    /// it has no mapping for. Deduplicating would leave that hole open.
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0038_AttributeIsCompilerInternal
        = new("ION0038",
            "Attribute '@{0}' is a compiler-internal marker and cannot be written in source. " +
            "The compiler attaches it on its own; remove it.");

    /// <summary>Declaration side: a word in the <c>on</c> clause that is not a target keyword.</summary>
    public static readonly IonAnalyticCode ION0038_UnknownAttributeTarget
        = new("ION0038",
            "Unknown attribute target '{0}' in the 'on' clause of attribute '@{1}'. Valid targets are: {2}.");

    /// <summary>
    /// Declaration side: <c>attribute @a(x: string?, y: i4);</c>.
    /// </summary>
    /// <remarks>
    /// Optionality is positional. An argument list may only be truncated from the end, so an
    /// optional parameter followed by a required one can never actually be omitted — the
    /// declaration promises something the grammar cannot express. Rejected in the declaration
    /// rather than at every use, where it would look like an arity bug in the caller's code.
    /// </remarks>
    public static readonly IonAnalyticCode ION0039_AttributeRequiredParameterAfterOptional
        = new("ION0039",
            "Attribute '@{0}' declares required parameter '{1}' after optional parameter '{2}'. " +
            "Optional parameters ('T?') must come last, because arguments can only be omitted from the end.");

    public static readonly IonAnalyticCode ION0020_LockFieldRemoved
        = new("ION0020", "Breaking change: field '{0}' (index {1}) was removed from '{2}'. Use 'reserved' or '--update-lock' to acknowledge.");
    public static readonly IonAnalyticCode ION0021_LockFieldReordered
        = new("ION0021", "Breaking change: field '{0}' in '{1}' changed index from {2} to {3}. Field order determines wire identity.");
    public static readonly IonAnalyticCode ION0022_LockFieldTypeChanged
        = new("ION0022", "Breaking change: field '{0}' in '{1}' changed type from '{2}' to '{3}'.");
    public static readonly IonAnalyticCode ION0023_LockDefinitionRemoved
        = new("ION0023", "Breaking change: definition '{0}' ({1}) was removed.");
    public static readonly IonAnalyticCode ION0024_LockDefinitionKindChanged
        = new("ION0024", "Breaking change: definition '{0}' changed kind from '{1}' to '{2}'.");
    public static readonly IonAnalyticCode ION0025_LockMethodRemoved
        = new("ION0025", "Service '{0}' removed method '{1}'. Existing clients will fail.");
    public static readonly IonAnalyticCode ION0026_LockMethodSignatureChanged
        = new("ION0026", "Breaking change: method '{0}.{1}' signature changed: {2}.");
    public static readonly IonAnalyticCode ION0027_LockEnumValueChanged
        = new("ION0027", "Breaking change: {0} '{1}' member '{2}' changed value from '{3}' to '{4}'.");
    public static readonly IonAnalyticCode ION0028_LockUnionCaseReordered
        = new("ION0028", "Breaking change: union '{0}' case '{1}' changed index from {2} to {3}. Index is the wire discriminator.");
    public static readonly IonAnalyticCode ION0029_LockFieldAddedNonNullable
        = new("ION0029", "Field '{0}' added to '{1}' is not nullable. Older readers will fail to deserialize. Consider using '{0}: {2}?'.");

    // ── Language feature codes (ION0060–ION0068) ──
    //
    // A new band, and deliberately not a reuse of a hole. The audit that preceded it:
    // ION0001–ION0045 and ION0047–ION0049 are all allocated above; ION0046 and ION1003 are the only
    // two numbers free in the whole space, and both were *vacated on purpose* with a comment saying
    // a reader who greps for them should find nothing (ION0046 is held for module pinning, which
    // needs somewhere to store the expectation; ION1003 was a hint that could only ever be wrong).
    // Taking either back would undo a decision, not fill a gap. ION0050–ION0059 belongs to
    // `ion.compiler.CodeGen.IonCodeGenDiagnostics` — target capability limits, raised by a
    // generator rather than by this pipeline — so the next free run starts at ION0060.
    //
    // Sub-bands, following the ION0014–ION0017 / ION0018–ION0019 precedent of one run per feature:
    //   ION0060–ION0061  generics (arity, Map key)
    //   ION0062          fixed-size arrays
    //   ION0063–ION0066  mixins
    //   ION0067–ION0068  inline anonymous types
    // ION0069 is left free so the run has somewhere to grow.

    /// <summary>
    /// A generic used with the wrong number of type arguments — <c>Maybe&lt;A, B&gt;</c>,
    /// <c>Map&lt;string&gt;</c>, a bare <c>Array</c>.
    /// </summary>
    /// <remarks>
    /// There was no arity check anywhere before <c>Map</c> and <c>Set</c> arrived, so
    /// <c>Maybe&lt;A, B&gt;</c> resolved silently: <c>ResolveTypeFor</c> wrote both arguments into a
    /// one-parameter definition, and a bare <c>Array</c> lowered to the open generic with no element
    /// type at all. The check reads <c>IonGenericType.TypeParameters.Count</c>, so it covers the
    /// three pre-existing wrappers on the same terms as the two new collections.
    /// <para>
    /// <c>{0}</c> is the generic, <c>{1}</c> the declared arity, <c>{2}</c> the number written and
    /// <c>{3}</c> the expected spelling with the parameter names in it.
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0060_GenericArityMismatch
        = new("ION0060",
            "Generic type '{0}' takes {1} type argument(s), but was given {2}. Write '{3}'.");

    /// <remarks>
    /// The zero-arity half of the same rule, split out because the fix is the opposite one: there is
    /// no correct argument list to write, the angle brackets have to go. <c>{1}</c> is the number of
    /// arguments written.
    /// </remarks>
    public static readonly IonAnalyticCode ION0060_TypeIsNotGeneric
        = new("ION0060",
            "Type '{0}' is not generic, but was given {1} type argument(s). Remove the '<...>'.");

    /// <summary>
    /// A <c>Map&lt;K, V&gt;</c> whose <c>K</c> cannot serve as a key.
    /// </summary>
    /// <remarks>
    /// The only guarantee there is. <c>IonMapFormatter&lt;TKey, TValue&gt;</c> encodes whatever the
    /// key formatter emits and orders the entries by those bytes; it never asks whether the bytes
    /// are canonical or whether the decoded key has value equality. See
    /// <c>ion.runtime.IonModule.MapKeyBuiltins</c> for the line and for why floats sit on the wrong
    /// side of it despite being scalar builtins.
    /// <para>
    /// <c>{0}</c> is the key type as written and <c>{1}</c> a phrase from
    /// <c>GenericTypeValidationStage.DescribeKey</c> that reads on from "…: ".
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0061_MapKeyTypeNotAllowed
        = new("ION0061",
            "'{0}' cannot be a Map key: {1}. A key must be an integral scalar builtin, 'bool', " +
            "'duration', 'string', 'guid', or an enum.");

    /// <summary>
    /// A fixed-size array whose size is not at least 1 — <c>f4[0]</c>, <c>f4[-3]</c>.
    /// </summary>
    /// <remarks>
    /// <c>N &lt; 1</c>, not <c>N &lt;= 1</c>. A one-element fixed array is odd but it is a coherent
    /// wire shape and it is what a schema mirroring a fixed C struct field will sometimes need.
    /// <c>f4[0]</c> is not: it encodes nothing, so it is indistinguishable from the field being
    /// absent, and <c>ReadFixedArray(reader, 0)</c> is a call with no reason to exist. A negative
    /// size has no reading at all — the grammar carries it here verbatim rather than failing, so
    /// that one bad size does not abort the enclosing declaration.
    /// <para>
    /// Anchored on the <c>[N]</c> suffix (<c>IonUnderlyingTypeSyntax.ArraySizeStart</c> /
    /// <c>ArraySizeEnd</c>) rather than on the whole type: the element type is fine and squiggling
    /// it would point at the half that is right.
    /// </para>
    /// <para><c>{0}</c> is the type as written and <c>{1}</c> the size.</para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0062_FixedArraySizeNotPositive
        = new("ION0062",
            "Fixed-size array '{0}' declares a size of {1}. A size must be at least 1 — a fixed " +
            "array of no elements encodes nothing, so it cannot be told apart from the field not " +
            "being there. Give it a positive size, or drop the size for a variable-length array.");

    // ── Mixins (ION0063–ION0066) ──

    /// <summary>A <c>with</c> clause naming something that is not a declared mixin.</summary>
    /// <remarks><c>{0}</c> is the name written, <c>{1}</c> the declaration that wrote it.</remarks>
    public static readonly IonAnalyticCode ION0063_MixinNotFound
        = new("ION0063",
            "'{0}' in the 'with' clause of {1} does not name a mixin. Declare 'mixin {0} {{ … }}', " +
            "or remove it from the clause.");

    /// <remarks>
    /// The name resolves, just not to a mixin. Kept apart from
    /// <see cref="ION0063_MixinNotFound"/> the same way ION0004 is kept apart from ION0003: the two
    /// are never both right about the same token, and the fix is different — a <c>msg</c> cannot be
    /// mixed in, because a mixin is a field-set template with no wire identity of its own.
    /// <c>{0}</c> is the name, <c>{1}</c> what it actually is, <c>{2}</c> the declaration.
    /// </remarks>
    public static readonly IonAnalyticCode ION0063_WithClauseNamesNonMixin
        = new("ION0063",
            "'{0}' in the 'with' clause of {2} is {1}, not a mixin. Only a 'mixin' can be included " +
            "with 'with'.");

    /// <remarks>
    /// Rejected rather than collapsed. Including the same mixin twice would otherwise contribute its
    /// fields twice and be caught downstream as a field collision with itself, which names the wrong
    /// mistake. <c>{0}</c> is the mixin, <c>{1}</c> the declaration.
    /// </remarks>
    public static readonly IonAnalyticCode ION0063_DuplicateMixinInWithClause
        = new("ION0063", "Mixin '{0}' is listed more than once in the 'with' clause of {1}. Write it once.");

    /// <summary>A <c>mixin</c> that transitively includes itself.</summary>
    /// <remarks>
    /// Shaped after <see cref="ION0017_CircularTypedef"/>, and reported at most once per mixin for
    /// the same reason: the cycle is reachable from every message that includes any member of it.
    /// Unlike a type cycle (ION0030) there is no modifier that can break this one — expansion is a
    /// compile-time splice, so a cycle simply has no fixed point.
    /// </remarks>
    public static readonly IonAnalyticCode ION0064_CircularMixin
        = new("ION0064",
            "Circular mixin inclusion: {0}. A mixin is spliced into its includer at compile time, " +
            "so the chain must terminate.");

    /// <summary>
    /// Two fields with the same name reaching one message, from a mixin and from somewhere else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both sources are named because neither is obviously the one to change, and because the
    /// second source is usually not visible in the file being read.
    /// </para>
    /// <para>
    /// This is also what a diamond (<c>mixin B with A</c>, then <c>msg M with A, B</c>) hits.
    /// Deduplicating instead would be worse than it looks: the wire is positional, so the surviving
    /// field's <em>index</em> would depend on which arm of the diamond was linearised first, and the
    /// author would have to compute a resolution order to know their own field numbering. Writing
    /// <c>with B</c> alone gives A's fields anyway.
    /// </para>
    /// <para>
    /// <c>{0}</c> is the field name, <c>{1}</c> the mixin it arrived from, <c>{2}</c> the other
    /// source, <c>{3}</c> the declaration they collide in.
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0065_MixinFieldCollision
        = new("ION0065",
            "Field '{0}' is contributed by {1} and is also declared by {2}, which would give {2} two " +
            "fields called '{0}'. Rename one of them.");

    /// <remarks>
    /// The two-mixin variant. <c>{0}</c> field name, <c>{1}</c> and <c>{2}</c> the two contributing
    /// sources (each already phrased as <c>mixin 'A'</c>, or <c>mixin 'A' (included by 'B')</c> when
    /// it arrived transitively — which is what a diamond looks like), <c>{3}</c> the declaration.
    /// </remarks>
    public static readonly IonAnalyticCode ION0065_MixinFieldCollisionBetweenMixins
        = new("ION0065",
            "Field '{0}' is contributed by both {1} and {2}, which would give {3} two fields called " +
            "'{0}'. Rename one of them, or include only the mixin that already carries the other.");

    /// <summary>A mixin written where a type is expected.</summary>
    /// <remarks>
    /// A mixin is a field-set template, not a type: it has no wire identity, no entry in
    /// <c>ion.lock.json</c> and no generated declaration, so a field typed by one would refer to
    /// something that does not exist at runtime. Reported from the syntax walk, and
    /// <c>RestoreUnresolvedTypeStage</c> stays silent about the same name so it is not also ION0009
    /// — the name resolves fine, it is the <em>position</em> that rejects it.
    /// <para><c>{0}</c> is the mixin name.</para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0066_MixinInTypePosition
        = new("ION0066",
            "'{0}' is a mixin, which is a field-set template rather than a type, so it cannot be " +
            "used here. Include it with 'with {0}', or declare a 'msg' if you need a type.");

    // ── Inline anonymous types (ION0067–ION0068) ──

    /// <summary>
    /// An inline anonymous type whose derived name is already taken by a declaration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An error and not a silent rename. Hoisting puts a derived name into the same flat global
    /// namespace as every declared type — which is exactly the pollution module namespacing is meant
    /// to fix later — and until then this diagnostic is the entire safety net. A compiler that
    /// quietly renamed <c>OrderShipping</c> to <c>OrderShipping1</c> would change the name in
    /// <c>ion.lock.json</c> and in four generated languages without anyone asking for it.
    /// </para>
    /// <para>
    /// <c>{0}</c> is the derived name, <c>{1}</c> what already holds it, <c>{2}</c> the field the
    /// inline type was written on.
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0067_InlineTypeNameCollision
        = new("ION0067",
            "The inline type on {2} hoists to '{0}', but {1} already has that name. Rename the " +
            "field, or declare the type explicitly and reference it by name.");

    /// <remarks>
    /// Two inline types deriving the same name — <c>trace_id</c> and <c>traceId</c> on one owner
    /// both pascal-case to <c>TraceId</c>. <c>{0}</c> is the derived name, <c>{1}</c> and
    /// <c>{2}</c> the two fields.
    /// </remarks>
    public static readonly IonAnalyticCode ION0067_InlineTypeNameCollisionBetweenInlineTypes
        = new("ION0067",
            "The inline types on {1} and on {2} both hoist to '{0}'. Rename one of the fields.");

    /// <summary>
    /// An inline anonymous type in a position that yields no name to derive from.
    /// </summary>
    /// <remarks>
    /// The grammar accepts <c>msg { … }</c> in every type position; the naming rule is
    /// <c>{Owner}{PascalCasedFieldName}</c>, so a position with no field name has nothing to hoist
    /// to. Rejected rather than named by invention — a generated type called <c>Result</c> or
    /// <c>Item</c> that the author never wrote is a name they then have to keep forever, because it
    /// is in the lock.
    /// <para><c>{0}</c> names the position, <c>{1}</c> says what to write instead.</para>
    /// </remarks>
    public static readonly IonAnalyticCode ION0068_InlineTypeNotAllowedHere
        = new("ION0068",
            "An inline 'msg {{ … }}' cannot be written as {0}: there is no field name to derive a " +
            "type name from. {1}");

    // ── Advisory codes (ION1001–ION1004) ──
    //
    // Non-blocking observations about otherwise valid schemas. ION1001–ION1002 are unused-symbol
    // hints; ION1004 widens the band from "unused" to "advisory" — a deprecated symbol is very much
    // used, which is the point of saying so.
    //
    // ION1003 was "field is never used by any service method" and has been removed. A field is part
    // of the wire contract: it is written by every encoder and read by every decoder of that message,
    // whether or not a service in *this* project mentions the message. "Unused" is not a property a
    // field can have, so the hint could only ever be wrong — and acting on it (deleting the field)
    // is a breaking schema change that ION0020 exists to stop.

    public static readonly IonAnalyticCode ION1001_UnusedType
        = new("ION1001", "Type '{0}' is defined but never referenced.");

    /// <remarks>
    /// The mixin variant, sharing the code because it is the same hint about the same kind of dead
    /// declaration. Worded separately because a mixin is not referenced the way a type is: the only
    /// thing that can use one is a <c>with</c> clause, so "never referenced" would send the reader
    /// looking for a field type that could never have existed.
    /// </remarks>
    public static readonly IonAnalyticCode ION1001_UnusedMixin
        = new("ION1001", "Mixin '{0}' is defined but no message or mixin includes it with 'with'.");
    public static readonly IonAnalyticCode ION1002_UnusedImport
        = new("ION1002", "Import '{0}' is unused. No types from this file are referenced.");

    /// <summary>
    /// A reference to a declaration that carries <c>@deprecated</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>{0}</c> is the referenced name, <c>{1}</c> is <c>""</c> or <c>" since '2.0'"</c> from the
    /// attribute's first argument, <c>{2}</c> names the referencing site
    /// (<c>IonTypeSites.Describe</c> — "the field 'owner' of msg 'Doc'") and <c>{3}</c> is <c>""</c>
    /// or the attribute's <c>reason</c> as a trailing sentence.
    /// </para>
    /// <para>
    /// A warning, not an error: deprecation is advice about a schema that still compiles, and the
    /// whole point is that the old name keeps working while callers migrate.
    /// </para>
    /// </remarks>
    public static readonly IonAnalyticCode ION1004_DeprecatedSymbolUsage
        = new("ION1004", "'{0}' is deprecated{1} and is referenced by {2}.{3}");

    // ── Module system codes (ION0040–ION0049) ──

    public static readonly IonAnalyticCode ION0040_ModuleCircularDependency
        = new("ION0040", "Circular module dependency detected: {0}");
    public static readonly IonAnalyticCode ION0041_ModuleConfigNotFound
        = new("ION0041", "Module '{0}' config not found at path '{1}'. Ensure the path contains a valid ion.config.json.");
    public static readonly IonAnalyticCode ION0042_ModuleUnknown
        = new("ION0042", "Unknown module '{0}'. It is not declared in ion.config.json modules.");
    public static readonly IonAnalyticCode ION0043_ModuleTypeNotFound
        = new("ION0043", "Type '{0}' not found in module '{1}'.");
    public static readonly IonAnalyticCode ION0044_ModuleTypeNotFoundWithSuggestion
        = new("ION0044", "Type '{0}' not found in module '{1}'. Did you mean '{2}'?");
    public static readonly IonAnalyticCode ION0045_ModuleUnusedImport
        = new("ION0045", "Imported type '{0}' from module '{1}' is never used.");

    // ION0046 was "module content hash drift". It has been removed along with
    // ModuleResolver.ComputeHash and ResolvedModule.ContentHash: the hash was computed, stored on the
    // resolved module and never compared to anything, and there was nowhere to record an expected
    // value. Module pinning is a real feature that needs a place to keep the expectation; the code
    // is deliberately left unallocated until it has one, so a reader searching for ION0046 finds
    // nothing rather than a checker that never fires.

    public static readonly IonAnalyticCode ION0047_DeprecatedUseDirective
        = new("ION0047", "#use is deprecated. Use '#import {{ TypeName }} from \"moduleName\"' instead.");
    public static readonly IonAnalyticCode ION0048_CrossModuleDuplicateTypeName
        = new("ION0048", "Type '{0}' in this project has the same name as type '{0}' from module '{1}'. This may cause ambiguity.");

    // ── #feature directive (ION0049) ──
    //
    // Shares the module band because `#feature` is a directive, parsed and read beside `#use` and
    // `#import`, and because it is about project configuration rather than about the schema.
    //
    // The directive used to be parsed into IonFileSyntax.featureSyntaxes and read by nothing: a file
    // could declare `#feature "orleans"`, be compiled by a project that does not enable orleans, and
    // fail later with ION0005 on every `@grainId` — a diagnostic that names the symptom and not the
    // cause. It now means "this file requires feature x", checked against ion.config.json.

    /// <remarks>
    /// <c>{0}</c> is the feature, <c>{1}</c> the file that declares it, <c>{2}</c> the features the
    /// project does enable (or <c>(none)</c>). The remedy names the config key literally: the author
    /// of a <c>.ion</c> file is often not the author of <c>ion.config.json</c>.
    /// </remarks>
    public static readonly IonAnalyticCode ION0049_FeatureNotEnabled
        = new("ION0049",
            "File '{1}' declares '#feature \"{0}\"', but the project does not enable it. " +
            "Add \"{0}\" to the \"features\" array of ion.config.json. Currently enabled: {2}.");

    /// <remarks><c>{1}</c> is the closed set of feature names the compiler knows.</remarks>
    public static readonly IonAnalyticCode ION0049_UnknownFeature
        = new("ION0049",
            "Unknown feature '{0}'. '#feature' names a compiler feature, and the set is closed: {1}.");
}

public record IonAnalyticCode(string code, string template);