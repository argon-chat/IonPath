namespace ion.compiler.CodeGen;

using ion.runtime;
using System.Globalization;
using System.Numerics;
using System.Text;

/// <summary>
/// The <c>@deprecated(since: string?, reason: string?)</c> arguments of one declaration, unpacked.
/// </summary>
/// <remarks>
/// Both halves are independently optional — <c>@deprecated</c>, <c>@deprecated("2.0")</c>,
/// <c>@deprecated(reason: "use GetUserV2")</c> and <c>@deprecated("2.0", "use GetUserV2")</c> are
/// all legal — so every target has to render four cases, not two.
/// </remarks>
public readonly record struct IonDeprecation(string? Since, string? Reason)
{
    /// <summary>Whether <c>@deprecated</c> was written with no arguments at all.</summary>
    public bool IsBare => Since is null && Reason is null;

    /// <summary>
    /// The one-line, sentence-fragment rendering shared by C# and TypeScript —
    /// <c>since 2.0: use GetUserV2 instead.</c> — or <see langword="null"/> when bare.
    /// </summary>
    /// <remarks>
    /// Deliberately lowercase-leading: TypeScript appends it after
    /// <c>@deprecated </c>, where a capital would read as a new sentence. C# capitalizes it itself,
    /// because <c>[Obsolete]</c>'s argument is a standalone message. Rust does not use this at all —
    /// <c>#[deprecated]</c> carries <c>since</c> and <c>note</c> as separate structured keys.
    /// </remarks>
    public string? Fragment => (Since, Reason) switch
    {
        (null, null) => null,
        (null, var reason) => reason,
        (var since, null) => $"since {since}.",
        var (since, reason) => $"since {since}: {reason}"
    };
}

/// <summary>
/// Renders Ion attribute uses into each target language, type-aware per language.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why only C# emits general attributes.</strong> C# is the only one of the three targets
/// with a general purpose declaration-annotation mechanism that survives to runtime. A Rust
/// <c>#[…]</c> must be known to the compiler or provided by a proc-macro — <c>#[Cache(30)]</c> is a
/// hard error, not an ignored annotation. TypeScript decorators
/// are not applicable to <c>interface</c> / <c>type</c> declarations and would need a runtime
/// import that the generated client does not have. So Rust and TypeScript emit
/// <c>@deprecated</c> only — each in its native idiom — and skip every other attribute. See
/// <see cref="IonDeprecation"/> and the <c>…Deprecated…</c> members below.
/// </para>
/// <para>
/// <strong>Value domain.</strong> <see cref="IonAttributeInstance.arguments"/> holds real CLR
/// values produced by <c>IonAttributeBinder.Convert</c>: <see cref="bool"/>, the eight fixed width
/// integers, <see cref="BigInteger"/> (<c>i16</c> / <c>u16</c> / <c>bigint</c>), <see cref="Half"/>
/// / <see cref="float"/> / <see cref="double"/>, <see cref="string"/>, <see cref="Guid"/>,
/// <see cref="DateTime"/>, <see cref="DateOnly"/>, <see cref="TimeOnly"/>, <see cref="Uri"/>,
/// <see cref="TimeSpan"/>, a <c>List&lt;object?&gt;</c> for an array parameter, and
/// <see langword="null"/>. An <em>enum reference</em> can never appear: attribute parameter types
/// are restricted to std builtins, and <c>IonAttributeBinder.Describe</c> rejects
/// <c>Status.Active</c> at the use site with ION0033.
/// </para>
/// </remarks>
public static class AttributeEmission
{
    // ═══════════════════════════════════════════════════════════════════
    // STD ATTRIBUTE TABLE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every attribute declared by a builtin module — <c>std</c> plus <c>orleans</c>.
    /// </summary>
    /// <remarks>
    /// A generator only ever sees user declarations in <c>IonModule.Attributes</c> (the builtin
    /// modules live in <c>CompilationContext.GlobalModules</c> and are never merged in), so "not in
    /// this set" is exactly "user declared", and a user declaration always has a generated
    /// <c>{Name}Attribute</c> class to bind to.
    /// </remarks>
    private static readonly HashSet<string> StdAttributes = new(StringComparer.Ordinal)
    {
        "builtin", "scalar", "union", "unionCase", "bits",
        "deadline", "deprecated", "internal",
        "grainId", "oneWay"
    };

    /// <summary>
    /// Std attributes with a real C# counterpart, mapped to the class name to write.
    /// </summary>
    /// <remarks>
    /// This is the fix for <c>@deadline(30)</c> emitting <c>[deadline(30)]</c>: C# identifiers are
    /// case sensitive, so neither <c>deadline</c> nor <c>deadlineAttribute</c> resolves against
    /// <c>ion.runtime.DeadlineAttribute</c>. <c>deprecated</c> is deliberately absent — it maps to
    /// <c>[Obsolete]</c>, whose argument shape is nothing like the Ion one, and is built by
    /// <see cref="CSharpObsolete"/>.
    /// </remarks>
    private static readonly Dictionary<string, string> CSharpStdAttributes = new(StringComparer.Ordinal)
    {
        ["deadline"] = "Deadline"
    };

    /// <summary>
    /// Whether <paramref name="name"/> is declared by a builtin module rather than by the user.
    /// </summary>
    public static bool IsStd(string name) => StdAttributes.Contains(name);

    /// <summary>
    /// The builtin modules' attribute declarations, by name.
    /// </summary>
    /// <remarks>
    /// A generator is handed user modules only — the builtin modules live in
    /// <c>CompilationContext.GlobalModules</c> and are never merged in — so without this a std
    /// attribute's use site has no declaration to render its arguments against, and
    /// <c>@deadline(30)</c> falls through to CLR-type inference. Read from the same
    /// <see cref="IonModule"/> singletons the compiler binds against, so the parameter types can
    /// never drift from the ones the values were converted for.
    /// </remarks>
    private static readonly Lazy<Dictionary<string, IonAttributeType>> BuiltinDeclarations = new(() =>
        new[] { IonModule.GetStdModule.Value, IonModule.GetOrleansModule.Value }
            .SelectMany(module => module.Attributes)
            .ToDictionary(declaration => declaration.name.Identifier, StringComparer.Ordinal));

    /// <summary>
    /// The C# attribute name for one use, or <see langword="null"/> when nothing should be emitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A std attribute with no target-language equivalent <strong>emits nothing</strong>. That is
    /// every marker the compiler consumes and the wire format encodes, and emitting a C# attribute
    /// for one would be inventing an API the runtime does not have:
    /// </para>
    /// <list type="bullet">
    /// <item><c>@builtin</c>, <c>@scalar</c> — synthesized onto the std definitions themselves;
    /// never written on generated code.</item>
    /// <item><c>@union</c>, <c>@unionCase</c> — synthesized by <c>TransformStage</c> onto a union
    /// and its cases. The generator already expresses them structurally, as the union interface and
    /// its case classes.</item>
    /// <item><c>@bits</c> — wire encoding, read back through <c>IonType.Bits</c> and baked into the
    /// generated formatter. A C# attribute would be a second, unread copy of a decision already
    /// compiled in.</item>
    /// <item><c>@internal</c> — an accessibility marker. It would have to change the declaration's
    /// modifier, not add an annotation, and every generated type is currently <c>public</c>.</item>
    /// <item><c>@grainId</c>, <c>@oneWay</c> (orleans) — no attribute class exists in
    /// <c>ion.runtime</c> to bind to.</item>
    /// </list>
    /// </remarks>
    private static string? CSharpAttributeName(string ionName)
    {
        if (!IsStd(ionName))
            return ionName; // user declared: GenerateAttributeDefinition emits `{ionName}Attribute`

        return CSharpStdAttributes.GetValueOrDefault(ionName);
    }

    // ═══════════════════════════════════════════════════════════════════
    // @deprecated
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The <c>@deprecated</c> use on a declaration, or <see langword="null"/> when it carries none.
    /// </summary>
    public static IonDeprecation? DeprecationOf(IReadOnlyList<IonAttributeInstance>? attributes)
    {
        if (attributes is null)
            return null;

        foreach (var attribute in attributes)
        {
            if (!attribute.IsDeprecated)
                continue;

            return new IonDeprecation(Text(attribute, "since", 0), Text(attribute, "reason", 1));
        }

        return null;
    }

    /// <summary>
    /// Reads one <c>@deprecated</c> argument, by name when the instance was bound against a
    /// declaration and positionally otherwise.
    /// </summary>
    /// <remarks>
    /// <c>parameterNames</c> is empty on the hand-built instances the std module attaches to its own
    /// definitions, which makes <see cref="IonAttributeInstance.Get{T}"/> return <c>null</c> for
    /// everything. Reading positionally is always correct — the argument list is normalized into
    /// declaration order before the instance is built — so the positional read is the fallback
    /// rather than the primary, which keeps a renamed parameter honest.
    /// </remarks>
    private static string? Text(IonAttributeInstance attribute, string parameterName, int index)
    {
        var byName = attribute.Get<string>(parameterName);
        if (byName is not null)
            return byName;

        return index < attribute.arguments.Count ? attribute.arguments[index] as string : null;
    }

    /// <summary>
    /// The C# <c>[Obsolete]</c> form. <c>since</c> is folded into the message, because
    /// <see cref="ObsoleteAttribute"/> has no separate slot for it.
    /// </summary>
    /// <remarks>
    /// The second <c>[Obsolete]</c> parameter is <c>bool error</c>, which would turn every use site
    /// into CS0619 — a build break, not a deprecation — so it is never emitted.
    /// </remarks>
    public static string CSharpObsolete(IonDeprecation deprecation)
    {
        var fragment = deprecation.Fragment;
        return fragment is null ? "Obsolete" : $"Obsolete({CSharpString(Capitalize(fragment))})";
    }

    /// <summary>The Rust <c>#[deprecated]</c> form, omitting either key when it was not written.</summary>
    public static string RustDeprecated(IonDeprecation deprecation)
    {
        if (deprecation.IsBare)
            return "#[deprecated]";

        var keys = new List<string>(2);
        if (deprecation.Since is { } since)
            keys.Add($"since = {RustString(since)}");
        if (deprecation.Reason is { } reason)
            keys.Add($"note = {RustString(reason)}");

        return $"#[deprecated({string.Join(", ", keys)})]";
    }

    /// <summary>
    /// The text of the JSDoc <c>@deprecated</c> tag — the empty string for a bare
    /// <c>@deprecated</c>, which JSDoc and every editor understand on its own.
    /// </summary>
    public static string JsDocDeprecated(IonDeprecation deprecation)
        => OneLine(deprecation.Fragment ?? string.Empty);

    // ═══════════════════════════════════════════════════════════════════
    // GENERAL C# ATTRIBUTE EMISSION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Renders every attribute on one declaration into C# attribute bodies —
    /// <c>Obsolete("…")</c>, <c>Deadline(30)</c>, <c>Cache(30, "users", true)</c> — ready to be
    /// wrapped in brackets by the caller (which decides between <c>[x]</c> and
    /// <c>[property: x]</c>).
    /// </summary>
    /// <param name="attributes">The declaration's attribute list; may be empty, never null.</param>
    /// <param name="declarationOf">
    /// Resolves an attribute name to its declaration, so an argument can be rendered against its
    /// <em>declared</em> type rather than guessed from the boxed value. Supplying it is what makes
    /// <c>i8[]</c> emit <c>new i8[] { 1, 2 }</c> instead of a <c>new[] { 1, 2 }</c> that C# would
    /// infer as <c>int[]</c> and reject. May return <see langword="null"/> for an unknown name.
    /// </param>
    public static List<string> CSharpAttributes(
        IReadOnlyList<IonAttributeInstance>? attributes,
        Func<string, IonAttributeType?>? declarationOf = null)
    {
        var rendered = new List<string>();
        if (attributes is null)
            return rendered;

        foreach (var attribute in attributes)
        {
            if (attribute.IsDeprecated)
            {
                rendered.Add(CSharpObsolete(
                    new IonDeprecation(Text(attribute, "since", 0), Text(attribute, "reason", 1))));
                continue;
            }

            var name = CSharpAttributeName(attribute.name.Identifier);
            if (name is null)
                continue;

            var declaration = declarationOf?.Invoke(attribute.name.Identifier)
                              ?? BuiltinDeclarations.Value.GetValueOrDefault(attribute.name.Identifier);
            var arguments = CSharpArguments(attribute, declaration);

            // A value with no C# constant form and no declared type to lower it against: emitting
            // `[Cache(30, )]` or a bare `null` in a slot that is not nullable would be worse than
            // emitting nothing, and the attribute is metadata — dropping it cannot change behaviour.
            if (arguments is null)
                continue;

            rendered.Add(arguments.Count == 0 ? name : $"{name}({string.Join(", ", arguments)})");
        }

        return rendered;
    }

    /// <summary>
    /// The rendered argument list, with omitted trailing optionals dropped, or
    /// <see langword="null"/> when some value has no C# form.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> in a <em>middle</em> slot is emitted as C# <c>null</c> — it is a
    /// real, written value that a later argument depends on for its position. Only a trailing run
    /// of nulls is dropped, which is exactly the "omitted trailing optional" convention
    /// <see cref="IonAttributeInstance.arguments"/> establishes; keeping them would emit
    /// <c>[Cache(30, "users", )]</c>.
    /// </remarks>
    private static List<string>? CSharpArguments(IonAttributeInstance attribute, IonAttributeType? declaration)
    {
        var count = attribute.arguments.Count;
        while (count > 0 && attribute.arguments[count - 1] is null)
            count--;

        var rendered = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            var declared = declaration is not null && i < declaration.arguments.Count
                ? declaration.arguments[i].type
                : null;

            var literal = CSharpLiteral(attribute.arguments[i], declared);
            if (literal is null)
                return null;

            rendered.Add(literal);
        }

        return rendered;
    }

    // ═══════════════════════════════════════════════════════════════════
    // C# LITERALS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// One attribute argument as a C# constant expression, or <see langword="null"/> when the value
    /// has no such form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C# restricts attribute arguments to compile-time constants of a fixed set of types, so the
    /// values Ion allows but C# cannot express — <see cref="BigInteger"/>, <see cref="Int128"/>,
    /// <see cref="Guid"/>, <see cref="DateTime"/>, … — are carried as their invariant string form.
    /// <see cref="CSharpParameterType"/> lowers the generated attribute class's parameter to
    /// <c>string</c> in exactly the same cases, so the two always agree.
    /// </para>
    /// <para>
    /// <strong>Rendering is driven by the declared Ion type, not by the value's CLR type.</strong>
    /// That is what makes the two agree: the parameter is declared <c>i4</c>, so the argument has to
    /// be an integer literal, whatever width the value happens to be boxed as. It is also what makes
    /// this correct in the face of <c>IonAttributeBinder.Integer</c>, whose <c>type switch</c> has a
    /// natural type of <see cref="BigInteger"/> — every arm converts to it — so <em>every</em>
    /// integer argument arrives boxed as a <see cref="BigInteger"/> regardless of the declared
    /// width, and a CLR-type-driven renderer would quote all of them. Only when the declaration is
    /// unavailable does <see cref="InferredLiteral"/> fall back to reading the CLR type.
    /// </para>
    /// </remarks>
    private static string? CSharpLiteral(object? value, IonType? declared)
    {
        if (value is null)
            return "null";

        var expected = Unwrap(declared, "Maybe");

        if (expected is IonGenericType { IsArray: true, TypeArguments.Count: > 0 } array)
            return value is List<object?> typed ? CSharpArray(typed, array.TypeArguments[0]) : null;

        if (value is List<object?> untyped)
            return CSharpArray(untyped, null);

        return expected?.name.Identifier switch
        {
            "bool" => value is bool b ? (b ? "true" : "false") : null,
            "string" => value is string s ? CSharpString(s) : null,

            "i1" or "i2" or "i4" or "i8" or "u1" or "u2" or "u4" or "u8" => IntegerLiteral(value),

            "f4" => FloatLiteral(value),
            // `f2` is System.Half, which C# cannot take as an attribute argument; CSharpParameterType
            // widens the parameter to f8, so the value widens with it.
            "f2" or "f8" => DoubleLiteral(value),

            // No C# constant form: carried as the invariant text the parameter was lowered to.
            "bigint" or "i16" or "u16" => IntegerLiteral(value) is { } digits ? CSharpString(digits) : null,
            "guid" or "datetime" or "dateonly" or "timeonly" or "uri" or "duration" => TextLiteral(value),

            _ => InferredLiteral(value)
        };
    }

    /// <summary>The digits of any integral value, whatever CLR width it is boxed as.</summary>
    private static string? IntegerLiteral(object value) => value switch
    {
        sbyte or short or int or long or byte or ushort or uint or ulong
            => Convert.ToString(value, CultureInfo.InvariantCulture),
        BigInteger big => big.ToString(CultureInfo.InvariantCulture),
        Int128 i128 => i128.ToString(CultureInfo.InvariantCulture),
        UInt128 u128 => u128.ToString(CultureInfo.InvariantCulture),
        _ => null
    };

    private static string? FloatLiteral(object value) => value switch
    {
        float f => CSharpFloat(f),
        double d => CSharpFloat((float)d),
        Half h => CSharpFloat((float)h),
        BigInteger big => CSharpFloat((float)big),
        sbyte or short or int or long or byte or ushort or uint or ulong
            => CSharpFloat(Convert.ToSingle(value, CultureInfo.InvariantCulture)),
        _ => null
    };

    private static string? DoubleLiteral(object value) => value switch
    {
        double d => CSharpDouble(d),
        float f => CSharpDouble(f),
        Half h => CSharpDouble((double)h),
        BigInteger big => CSharpDouble((double)big),
        sbyte or short or int or long or byte or ushort or uint or ulong
            => CSharpDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        _ => null
    };

    /// <summary>The invariant, round-trippable text of a value with no C# constant form.</summary>
    private static string? TextLiteral(object value) => value switch
    {
        string s => CSharpString(s),
        Guid guid => CSharpString(guid.ToString("D", CultureInfo.InvariantCulture)),
        DateTime dt => CSharpString(dt.ToString("O", CultureInfo.InvariantCulture)),
        // `datetime` now maps to DateTimeOffset in generated code. Attribute arguments never
        // reach the wire — CSharpParameterType lowers `datetime` to `string` — so the binder is
        // free to hand over either CLR shape; both must render, or the argument is silently
        // dropped (CSharpArguments discards the whole attribute on a null literal).
        DateTimeOffset dto => CSharpString(dto.ToString("O", CultureInfo.InvariantCulture)),
        DateOnly date => CSharpString(date.ToString("O", CultureInfo.InvariantCulture)),
        TimeOnly time => CSharpString(time.ToString("O", CultureInfo.InvariantCulture)),
        TimeSpan span => CSharpString(span.ToString("c", CultureInfo.InvariantCulture)),
        Uri uri => CSharpString(uri.OriginalString),
        _ => null
    };

    /// <summary>
    /// Best effort rendering from the CLR type alone, for a use site whose declaration this
    /// generator has not been shown (an attribute declared in a module emitted later).
    /// </summary>
    private static string? InferredLiteral(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string s => CSharpString(s),
        sbyte or short or int or long or byte or ushort or uint or ulong
            => Convert.ToString(value, CultureInfo.InvariantCulture),
        float f => CSharpFloat(f),
        double d => CSharpDouble(d),
        Half h => CSharpDouble((double)h),
        BigInteger or Int128 or UInt128 => CSharpString(IntegerLiteral(value)!),
        _ => TextLiteral(value)
    };

    /// <summary>
    /// A C# array creation expression. The element type is always written out.
    /// </summary>
    /// <remarks>
    /// <c>new[] { 1, 2 }</c> would be inferred as <c>int[]</c>, which does not convert to the
    /// <c>long[]</c> an <c>i8[]</c> parameter declares, and an empty array cannot be written at all
    /// without naming its type. Both cases need the declared element type; when the declaration is
    /// unavailable the first non-null element's CLR type is used, and an empty array with no
    /// declaration is the one shape that has to be given up on.
    /// </remarks>
    private static string? CSharpArray(List<object?> items, IonType? element)
    {
        var rendered = new List<string>(items.Count);

        foreach (var item in items)
        {
            var literal = CSharpLiteral(item, element);
            if (literal is null)
                return null;
            rendered.Add(literal);
        }

        var elementType = element is not null
            ? CSharpParameterType(element)
            : InferredElementType(items);

        if (elementType is null)
            return null;

        return rendered.Count == 0
            ? $"new {elementType}[0]"
            : $"new {elementType}[] {{ {string.Join(", ", rendered)} }}";
    }

    /// <summary>The C# element type read off the values, for when the declaration is unknown.</summary>
    private static string? InferredElementType(List<object?> items)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case null: continue;
                case bool: return "bool";
                case string: return "string";
                case sbyte: return "sbyte";
                case short: return "short";
                case int: return "int";
                case long: return "long";
                case byte: return "byte";
                case ushort: return "ushort";
                case uint: return "uint";
                case ulong: return "ulong";
                case float: return "float";
                case double or Half: return "double";
                case BigInteger or Int128 or UInt128 or Guid or DateTime or DateTimeOffset
                    or DateOnly or TimeOnly or TimeSpan or Uri: return "string";
                default: return null;
            }
        }

        return null;
    }

    private static IonType? Unwrap(IonType? type, string wrapper)
        => type is IonGenericType generic
           && generic.name.Identifier == wrapper
           && generic.TypeArguments.Count > 0
            ? generic.TypeArguments[0]
            : type;

    /// <summary>
    /// The C# type to declare a generated attribute class's constructor parameter as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C# accepts only <c>bool</c>, the numeric primitives, <c>char</c>, <c>string</c>,
    /// <c>object</c>, <see cref="Type"/>, an enum, and single-dimensional arrays of those as
    /// attribute parameter types (CS0181). Writing the Ion type through unchanged produced
    /// declarations that could not compile:
    /// </para>
    /// <list type="bullet">
    /// <item><c>i4[]</c> resolved to <c>IonArray&lt;i4&gt;</c>, a class.</item>
    /// <item><c>bigint</c>, <c>i16</c>, <c>u16</c>, <c>guid</c>, <c>datetime</c>, <c>dateonly</c>,
    /// <c>timeonly</c>, <c>uri</c>, <c>duration</c> resolved to types with no constant form (and
    /// several, like <c>bigint</c>, to a name with no <c>global using</c> at all).</item>
    /// <item><c>i4?</c> resolved to <c>int?</c>, i.e. <c>Nullable&lt;int&gt;</c>.</item>
    /// </list>
    /// <para>
    /// Each is lowered to the nearest legal type: an unrepresentable scalar to its invariant
    /// <c>string</c> form, <c>f2</c> to <c>f8</c> (<see cref="Half"/> widens to
    /// <see cref="double"/> losslessly), an optional value type to <c>object?</c>, and an array to
    /// a real <c>T[]</c>. <see cref="CSharpLiteral"/> renders values to match.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Whether a declared attribute parameter is <c>T?</c> and may be left off at the use site.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>IonAttributeBinder.IsOptional</c>, which is the compiler's definition of the same
    /// question. Kept here so the generator does not have to reach into the binder, and so the
    /// reason it matters stays next to the emission that needs it: an omitted trailing optional is
    /// <em>dropped</em> from the rendered argument list, which only compiles if the generated
    /// constructor parameter has a C# default to fall back on.
    /// </remarks>
    public static bool IsOptionalParameter(IonType type) => type is IonGenericType { IsMaybe: true };

    public static string CSharpParameterType(IonType type)
    {
        if (type is IonGenericType { IsMaybe: true, TypeArguments.Count: > 0 } maybe)
        {
            var inner = CSharpParameterType(maybe.TypeArguments[0]);
            // A nullable reference type stays itself; a nullable value type would be Nullable<T>,
            // which is not a legal attribute parameter type, so it becomes `object?`.
            return inner is "string" or "string[]" || inner.EndsWith("[]", StringComparison.Ordinal)
                ? $"{inner}?"
                : "object?";
        }

        if (type is IonGenericType { IsArray: true, TypeArguments.Count: > 0 } array)
            return $"{CSharpParameterType(array.TypeArguments[0])}[]";

        return type.name.Identifier switch
        {
            // `f2` is System.Half, which is not a legal attribute parameter type; f8 is exact.
            "f2" => "f8",

            // No constant form in C# — carried as the invariant string the value round-trips through.
            "bigint" or "i16" or "u16"
                or "guid" or "datetime" or "dateonly" or "timeonly" or "uri" or "duration" => "string",

            // bool / i1..i8 / u1..u8 / f4 / f8 / string alias straight onto legal primitives
            // through the generated globals.cs.
            var name => name
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // STRING LITERALS AND COMMENT TEXT
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>A C# double-quoted string literal.</summary>
    public static string CSharpString(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\0': sb.Append("\\0"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary>A Rust double-quoted string literal.</summary>
    public static string RustString(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\0': sb.Append("\\0"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                        sb.Append("\\u{").Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append('}');
                    else
                        sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Flattens text onto one line, for the single-line contexts a doc tag lives in.
    /// </summary>
    /// <remarks>
    /// A <c>reason</c> is an ordinary Ion string and may contain <c>\n</c>. Emitted raw it would
    /// break out of a <c>//</c> line comment entirely, and would silently continue the previous
    /// JSDoc tag in TypeScript.
    /// </remarks>
    private static string OneLine(string text)
    {
        var collapsed = text
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        // `*/` would terminate the enclosing JSDoc block early; harmless in a `//` line comment.
        return DocCommentFormatter.JsDocEscape(collapsed);
    }

    private static string Capitalize(string text)
        => text.Length == 0 || char.IsUpper(text[0])
            ? text
            : char.ToUpperInvariant(text[0]) + text[1..];

    // ═══════════════════════════════════════════════════════════════════
    // NUMERIC LITERALS
    // ═══════════════════════════════════════════════════════════════════

    /// <remarks>
    /// <c>float.NaN</c> / <c>float.PositiveInfinity</c> / <c>float.NegativeInfinity</c> are
    /// <c>const</c> fields, so they are legal attribute arguments — but they have no literal form,
    /// and <c>NaNf</c> is not C#.
    /// </remarks>
    private static string CSharpFloat(float value)
    {
        if (float.IsNaN(value)) return "float.NaN";
        if (float.IsPositiveInfinity(value)) return "float.PositiveInfinity";
        if (float.IsNegativeInfinity(value)) return "float.NegativeInfinity";
        return value.ToString("R", CultureInfo.InvariantCulture) + "F";
    }

    private static string CSharpDouble(double value)
    {
        if (double.IsNaN(value)) return "double.NaN";
        if (double.IsPositiveInfinity(value)) return "double.PositiveInfinity";
        if (double.IsNegativeInfinity(value)) return "double.NegativeInfinity";
        return value.ToString("R", CultureInfo.InvariantCulture) + "D";
    }
}
