namespace ion.compiler;

using ion.runtime;
using ion.syntax;
using System.Globalization;
using System.Numerics;

/// <summary>
/// A diagnostic the binder found, recorded rather than reported.
/// </summary>
/// <remarks>
/// Binding runs twice over each attribute use: once in <see cref="TransformStage"/>, which needs the
/// <em>values</em>, and once in <see cref="AttributeValidationStage"/>, which needs the
/// <em>diagnostics</em>. Reporting from the binder itself would duplicate every message. Carrying
/// them as data means both callers see the identical analysis and exactly one of them speaks.
/// </remarks>
public sealed record IonAttributeProblem(IonAnalyticCode Code, IonSyntaxBase Node, object[] Args);

/// <summary>The result of matching one attribute use against its declaration.</summary>
/// <param name="Values">
/// Positional, in declaration order, always <c>Declaration.arguments.Count</c> long. Named
/// arguments have been moved to their declared slot and omitted trailing optionals are
/// <see langword="null"/> — see <see cref="IonAttributeInstance.arguments"/> for the contract this
/// establishes for every downstream consumer.
/// </param>
public sealed record IonAttributeBinding(
    IonAttributeType Declaration,
    List<object?> Values,
    IReadOnlyList<IonAttributeProblem> Problems);

/// <summary>
/// Matches an attribute use (<c>@Cache(key: "u", 30)</c>) against its declaration
/// (<c>attribute @Cache(duration: i4, key: string?) on msg;</c>): arity, argument types, named
/// argument resolution, and integer / floating point range.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the loop that used to live in <c>CompilationContext.ResolveAttributeInstance</c>,
/// which read <c>attr.arguments[i].name.Identifier</c> — the parameter's <em>name</em> — as the type
/// to parse the argument as, and then threw <see cref="InvalidOperationException"/> when that name
/// was not a std type name. Any attribute whose first parameter was not accidentally called
/// <c>i4</c> / <c>string</c> / … crashed the compiler on its first use. Nothing here throws: every
/// reachable failure is an <see cref="IonAttributeProblem"/> with a source position.
/// </para>
/// <para>
/// <strong>Optionality.</strong> A parameter is optional when its declared type is
/// <c>Maybe&lt;T&gt;</c>, i.e. it was written <c>T?</c>. There is no <c>= default</c> syntax
/// (roadmap 1.3), so <c>T?</c> is the only way to make an argument omittable, and optional
/// parameters must be trailing (ION0039) — which is what lets <c>@deprecated</c>,
/// <c>@deprecated("2.0")</c> and <c>@deprecated("2.0", "use GetUserV2")</c> all bind against the
/// single declaration <c>@deprecated(since: string?, reason: string?)</c>.
/// </para>
/// </remarks>
public static class IonAttributeBinder
{
    /// <summary>How a parameter slot came to be filled — <c>ION0036</c> needs to tell them apart.</summary>
    private enum Bound
    {
        No,
        Positionally,
        ByName
    }

    // ── Public shape queries ───────────────────────────────────────────

    /// <summary>Whether <paramref name="parameter"/> was declared <c>T?</c> and may be omitted.</summary>
    public static bool IsOptional(IonArgument parameter) => parameter.type is IonGenericType { IsMaybe: true };

    /// <summary>The declared type with any outer <c>Maybe</c> removed.</summary>
    public static IonType Required(IonType type) =>
        type is IonGenericType { IsMaybe: true, TypeArguments.Count: > 0 } maybe
            ? maybe.TypeArguments[0]
            : type;

    /// <summary>The type as it would be written in source — <c>string?</c>, <c>i4[]</c>.</summary>
    public static string TypeName(IonType type) => type switch
    {
        IonGenericType { IsMaybe: true, TypeArguments.Count: > 0 } g => TypeName(g.TypeArguments[0]) + "?",
        IonGenericType { IsArray: true, TypeArguments.Count: > 0 } g => TypeName(g.TypeArguments[0]) + "[]",
        IonGenericType { IsPartial: true, TypeArguments.Count: > 0 } g => TypeName(g.TypeArguments[0]) + "~",
        _ => type.name.Identifier
    };

    /// <summary>The declaration's parameter list as written — <c>since: string?, reason: string?</c>.</summary>
    public static string Signature(IonAttributeType declaration) =>
        string.Join(", ", declaration.arguments.Select(a => $"{a.name.Identifier}: {TypeName(a.type)}"));

    /// <summary>
    /// The std builtins an attribute parameter may be declared as: everything with a literal form.
    /// </summary>
    /// <remarks>
    /// <c>void</c> and <c>bytes</c> are builtins with no literal syntax, so a parameter of that type
    /// could never be supplied — they are rejected in the declaration (ION0004) rather than left to
    /// fail at every use site. Message, enum and union types are excluded for a second reason:
    /// attribute declarations are lowered before any other definition exists, so the name could not
    /// be resolved there even if the value could be written.
    /// </remarks>
    private static readonly HashSet<string> ParameterTypes = new(StringComparer.Ordinal)
    {
        "bool",
        "i1", "i2", "i4", "i8", "i16",
        "u1", "u2", "u4", "u8", "u16",
        "f2", "f4", "f8",
        "bigint", "string", "guid", "datetime", "dateonly", "timeonly", "uri", "duration"
    };

    /// <summary>Whether a resolved builtin is legal as an attribute parameter type (ION0004).</summary>
    public static bool IsAllowedParameterType(IonType type) =>
        type is not IonGenericType && ParameterTypes.Contains(type.name.Identifier);

    // ── Binding ────────────────────────────────────────────────────────

    public static IonAttributeBinding Bind(IonAttributeType declaration, IonAttributeSyntax use)
    {
        var problems = new List<IonAttributeProblem>();
        var parameters = declaration.arguments;
        var name = declaration.name.Identifier;

        var values = new object?[parameters.Count];
        var bound = new Bound[parameters.Count];
        var boundAt = new int[parameters.Count];

        var sawNamed = false;
        var nextPositional = 0;
        var surplus = 0;

        // Set by the mistakes that leave a parameter *unbound*. A rejected value still occupies its
        // slot, so it must not suppress the missing-argument check; a stray or misnamed argument
        // does, because "and you forgot 'reason'" on top of "there is no parameter 'resaon'" is the
        // same mistake reported twice.
        var unbindingError = false;

        for (var i = 0; i < use.Args.Count; i++)
        {
            var argument = use.Args[i];
            var position = i + 1;

            if (argument.Name is null)
            {
                if (sawNamed)
                {
                    problems.Add(new(IonAnalyticCodes.ION0037_PositionalAfterNamedAttributeArgument,
                        argument, [name, position]));
                    unbindingError = true;
                    continue;
                }

                if (nextPositional >= parameters.Count)
                {
                    // Counted, not reported per argument: `@bits(1, 2, 3)` on a one-parameter
                    // attribute is one mistake, and the count is more useful than three squiggles.
                    surplus++;
                    continue;
                }

                var slot = nextPositional++;
                values[slot] = Convert(name, parameters[slot], parameters[slot].name.Identifier,
                    argument.Value, problems);
                bound[slot] = Bound.Positionally;
                boundAt[slot] = position;
                continue;
            }

            sawNamed = true;

            var index = IndexOf(parameters, argument.Name.Identifier);

            if (index < 0)
            {
                Unknown(name, declaration, argument, problems);
                unbindingError = true;
                continue;
            }

            if (bound[index] is not Bound.No)
            {
                problems.Add(bound[index] is Bound.Positionally
                    ? new(IonAnalyticCodes.ION0036_NamedAttributeArgumentAlreadyPositional,
                        argument, [name, argument.Name.Identifier, boundAt[index]])
                    : new(IonAnalyticCodes.ION0036_DuplicateNamedAttributeArgument,
                        argument, [name, argument.Name.Identifier]));
                unbindingError = true;
                continue;
            }

            values[index] = Convert(name, parameters[index], parameters[index].name.Identifier,
                argument.Value, problems);
            bound[index] = Bound.ByName;
            boundAt[index] = position;
        }

        if (surplus > 0)
        {
            problems.Add(new(IonAnalyticCodes.ION0032_AttributeTooManyArguments,
                use, [name, parameters.Count, use.Args.Count, Signature(declaration)]));
            unbindingError = true;
        }

        if (!unbindingError)
        {
            var missing = parameters
                .Where((p, index) => bound[index] is Bound.No && !IsOptional(p))
                .Select(p => $"'{p.name.Identifier}'")
                .ToList();

            if (missing.Count > 0)
                problems.Add(new(IonAnalyticCodes.ION0032_AttributeTooFewArguments,
                    use, [name, missing.Count == 1 ? "" : "s", string.Join(", ", missing), Signature(declaration)]));
        }

        return new IonAttributeBinding(declaration, [..values], problems);
    }

    /// <summary>
    /// Builds the IR instance for a bound use, preferring the specialised subclasses the rest of the
    /// compiler pattern-matches on.
    /// </summary>
    /// <remarks>
    /// <c>IonType.Bits</c> reads <see cref="IonBitAttributeInstance"/>, so a source-written
    /// <c>@bits(8)</c> has to produce the same node the std module builds with
    /// <c>NumberBitEx.Bits()</c>. <c>@bits</c> did not do that before — it fell through to a plain
    /// instance and <c>HasBitsAttribute</c> stayed false.
    /// <para>
    /// The reserved markers are matched by name rather than reached through
    /// <see cref="IonReservedAttributes"/> at runtime because each maps to a <em>different</em>
    /// record. Source can no longer write them (ION0038); these arms exist for the instances the
    /// compiler synthesizes and re-binds.
    /// </para>
    /// </remarks>
    public static IonAttributeInstance Materialize(IonAttributeBinding binding)
    {
        var declaration = binding.Declaration;
        var values = binding.Values;
        var names = declaration.arguments.Select(a => a.name.Identifier).ToList();

        switch (declaration.name.Identifier)
        {
            case IonReservedAttributes.Builtin: return new IonBuiltinAttributeInstance();
            case IonReservedAttributes.Scalar: return new IonScalarAttributeInstance();
            case IonReservedAttributes.Union: return new IonUnionAttributeInstance();
            case IonReservedAttributes.UnionCase: return new IonUnionCaseAttributeInstance();

            // Guarded on the bound value: a rejected `@bits("x")` still reaches here (the compile
            // fails on its own diagnostic) and must not be forced into the typed node.
            case "bits" when values is [int bits]:
                return new IonBitAttributeInstance(bits) { parameterNames = names };
        }

        return new IonAttributeInstance(declaration.name, values) { parameterNames = names };
    }

    private static int IndexOf(List<IonArgument> parameters, string name)
    {
        for (var i = 0; i < parameters.Count; i++)
            if (string.Equals(parameters[i].name.Identifier, name, StringComparison.Ordinal))
                return i;

        return -1;
    }

    private static void Unknown(string attribute, IonAttributeType declaration,
        IonAttributeArgumentSyntax argument, List<IonAttributeProblem> problems)
    {
        var written = argument.Name!.Identifier;
        var candidates = declaration.arguments.Select(a => a.name.Identifier).ToList();
        var suggestion = LevenshteinDistance.FindClosest(written, candidates);

        problems.Add(suggestion is not null
            ? new(IonAnalyticCodes.ION0035_UnknownNamedAttributeArgumentWithSuggestion,
                argument.Name, [attribute, written, suggestion])
            : new(IonAnalyticCodes.ION0035_UnknownNamedAttributeArgument,
                argument.Name, [attribute, written, candidates.Count == 0
                    ? "(none)"
                    : string.Join(", ", candidates.Select(c => $"'{c}'"))]));
    }

    // ── Literal conversion ─────────────────────────────────────────────

    /// <summary>
    /// Converts one literal to the CLR value for <paramref name="parameter"/>'s declared type,
    /// appending a problem and returning <see langword="null"/> when it cannot.
    /// </summary>
    /// <remarks>
    /// <c>label</c> is how the parameter is named in a message: the parameter name at the top level,
    /// and <c>items[2]</c> inside an array literal, so an element failure points at the element.
    /// </remarks>
    private static object? Convert(string attribute, IonArgument parameter, string label,
        IonLiteralSyntax literal, List<IonAttributeProblem> problems)
    {
        // The declared type never resolved (ION0003 / ION0004 already said so). Checking the value
        // against a type the compiler does not have would only invent a second, bogus complaint.
        if (parameter.type is IonUnresolvedType)
            return null;

        return Convert(attribute, label, parameter.type, literal, problems);
    }

    private static object? Convert(string attribute, string label, IonType declared,
        IonLiteralSyntax literal, List<IonAttributeProblem> problems)
    {
        var optional = declared is IonGenericType { IsMaybe: true };
        var expected = Required(declared);

        if (literal is IonNullLiteralSyntax)
        {
            if (optional)
                return null;

            problems.Add(new(IonAnalyticCodes.ION0033_AttributeArgumentNullNotAllowed,
                literal, [attribute, label, TypeName(expected)]));
            return null;
        }

        if (expected is IonGenericType { IsArray: true, TypeArguments.Count: > 0 } array)
        {
            if (literal is not IonArrayLiteralSyntax items)
                return Mismatch(attribute, label, declared, literal, problems);

            var element = array.TypeArguments[0];
            var converted = new List<object?>(items.Items.Count);

            for (var i = 0; i < items.Items.Count; i++)
                converted.Add(Convert(attribute, $"{label}[{i}]", element, items.Items[i], problems));

            return converted;
        }

        if (literal is IonArrayLiteralSyntax)
            return Mismatch(attribute, label, declared, literal, problems);

        var name = expected.name.Identifier;

        if (IntegerRanges.ContainsKey(name) || name is "bigint")
            return Integer(attribute, label, name, TypeName(declared), literal, problems);

        if (name is "f2" or "f4" or "f8")
            return Floating(attribute, label, name, TypeName(declared), literal, problems);

        switch (name)
        {
            case "bool":
                return literal is IonBoolLiteralSyntax b
                    ? b.Value
                    : Mismatch(attribute, label, declared, literal, problems);

            case "string":
                return literal is IonStringLiteralSyntax s
                    ? s.Value
                    : Mismatch(attribute, label, declared, literal, problems);

            case "guid" or "datetime" or "dateonly" or "timeonly" or "uri" or "duration":
                if (literal is not IonStringLiteralSyntax text)
                    return Mismatch(attribute, label, declared, literal, problems);

                var parsed = ParseText(name, text.Value);

                if (parsed is not null)
                    return parsed;

                problems.Add(new(IonAnalyticCodes.ION0033_AttributeArgumentTypeMismatch,
                    literal, [attribute, label, TypeName(declared),
                        $"the string literal \"{text.Value}\" (not a valid {name})"]));
                return null;

            default:
                // A builtin with no literal form (`void`, `bytes`) or a bare generic. ION0004
                // rejects these in the declaration; this arm only runs for a declaration that was
                // already reported, so it stays a plain mismatch rather than a third opinion.
                return Mismatch(attribute, label, declared, literal, problems);
        }
    }

    private static object? Mismatch(string attribute, string label, IonType declared,
        IonLiteralSyntax literal, List<IonAttributeProblem> problems)
    {
        problems.Add(new(IonAnalyticCodes.ION0033_AttributeArgumentTypeMismatch,
            literal, [attribute, label, TypeName(declared), Describe(literal)]));
        return null;
    }

    /// <summary>A phrase naming what was written, reading on from "…, but ".</summary>
    public static string Describe(IonLiteralSyntax literal) => literal switch
    {
        IonIntegerLiteralSyntax i => $"the integer literal {i.Raw}",
        IonFloatLiteralSyntax f => $"the floating point literal {f.Raw}",
        IonStringLiteralSyntax s => $"the string literal \"{s.Value}\"",
        IonBoolLiteralSyntax b => $"the boolean literal {(b.Value ? "true" : "false")}",
        IonNullLiteralSyntax => "'null'",
        IonArrayLiteralSyntax a => $"an array literal of {a.Items.Count} element(s)",

        // No attribute parameter can have an enum type — parameter types are std builtins, because
        // attribute declarations are lowered before any user definition exists to resolve against.
        IonEnumRefLiteralSyntax e =>
            $"the enum reference '{e.TypeName.Identifier}.{e.Member.Identifier}' " +
            "(attribute arguments must be literals of a builtin type)",

        _ => "an unsupported literal"
    };

    // ── Numerics ───────────────────────────────────────────────────────

    /// <summary>
    /// Inclusive bounds per integer builtin. <c>i16</c> / <c>u16</c> are sixteen <em>bytes</em> —
    /// <c>IonModule</c> declares them as <c>16.Bits()</c>, i.e. 128 bits.
    /// </summary>
    private static readonly Dictionary<string, (BigInteger Min, BigInteger Max)> IntegerRanges =
        new(StringComparer.Ordinal)
        {
            ["i1"] = (sbyte.MinValue, sbyte.MaxValue),
            ["i2"] = (short.MinValue, short.MaxValue),
            ["i4"] = (int.MinValue, int.MaxValue),
            ["i8"] = (long.MinValue, long.MaxValue),
            ["i16"] = (Int128.MinValue, Int128.MaxValue),
            ["u1"] = (byte.MinValue, byte.MaxValue),
            ["u2"] = (ushort.MinValue, ushort.MaxValue),
            ["u4"] = (uint.MinValue, uint.MaxValue),
            ["u8"] = (ulong.MinValue, ulong.MaxValue),
            ["u16"] = (UInt128.MinValue, UInt128.MaxValue)
        };

    private static object? Integer(string attribute, string label, string type, string declared,
        IonLiteralSyntax literal, List<IonAttributeProblem> problems)
    {
        if (literal is not IonIntegerLiteralSyntax integer)
        {
            problems.Add(new(IonAnalyticCodes.ION0033_AttributeArgumentTypeMismatch,
                literal, [attribute, label, declared, Describe(literal)]));
            return null;
        }

        if (IntegerRanges.TryGetValue(type, out var range) &&
            (integer.Value < range.Min || integer.Value > range.Max))
        {
            problems.Add(new(IonAnalyticCodes.ION0034_AttributeArgumentOutOfRange,
                literal, [attribute, label, integer.Raw, type, $"valid range {range.Min} to {range.Max}"]));
            return null;
        }

        // `i16` / `u16` / `bigint` stay BigInteger, which is what the old std type table produced
        // and therefore what any consumer that already reads a 128-bit attribute value expects.
        //
        // Every arm is cast to `object` explicitly. Without it the switch expression has a natural
        // type: sbyte/short/int/... all convert implicitly to BigInteger while BigInteger (the `_`
        // arm) converts to none of them, so C# widened every narrow arm straight back to BigInteger
        // and boxed that. Consumers matching on the declared width then never matched — `@bits(8)`
        // failed `values is [int bits]` in Materialize and silently lowered to a plain instance, so
        // IonType.Bits threw `Sequence contains no elements` because HasBitsAttribute was false.
        return type switch
        {
            "i1" => (object)(sbyte)integer.Value,
            "i2" => (short)integer.Value,
            "i4" => (int)integer.Value,
            "i8" => (long)integer.Value,
            "u1" => (byte)integer.Value,
            "u2" => (ushort)integer.Value,
            "u4" => (uint)integer.Value,
            "u8" => (ulong)integer.Value,
            _ => integer.Value
        };
    }

    private static object? Floating(string attribute, string label, string type, string declared,
        IonLiteralSyntax literal, List<IonAttributeProblem> problems)
    {
        double value;
        string raw;

        switch (literal)
        {
            case IonFloatLiteralSyntax f:
                value = f.Value;
                raw = f.Raw;
                break;

            // An integer literal widens into a float parameter: `@ratio(1)` for `ratio: f4`.
            case IonIntegerLiteralSyntax i:
                raw = i.Raw;
                try
                {
                    value = (double)i.Value;
                }
                catch (OverflowException)
                {
                    value = double.PositiveInfinity;
                }

                if (double.IsInfinity(value))
                    return OutOfRange(attribute, label, raw, type, literal, problems);

                break;

            default:
                problems.Add(new(IonAnalyticCodes.ION0033_AttributeArgumentTypeMismatch,
                    literal, [attribute, label, declared, Describe(literal)]));
                return null;
        }

        // The literal grammar has no infinity or NaN form (`Ion.Literals.cs` rejects bare
        // identifiers), so a non-finite value here can only have come from a decimal literal that
        // saturated: `double.Parse("1e400")` returns +Inf rather than throwing on .NET Core 3.0+.
        // That is out of range for every float width, including `f8` — guarding the narrowing check
        // on IsFinite used to let it through silently for f2, f4 and f8 alike.
        if (!double.IsFinite(value))
            return OutOfRange(attribute, label, raw, type, literal, problems);

        switch (type)
        {
            case "f4" when float.IsInfinity((float)value):
                return OutOfRange(attribute, label, raw, type, literal, problems);
            case "f2" when Half.IsInfinity((Half)value):
                return OutOfRange(attribute, label, raw, type, literal, problems);
        }

        return type switch
        {
            "f2" => (Half)value,
            "f4" => (float)value,
            _ => value
        };
    }

    /// <summary>
    /// The largest finite value of each float width, for the ION0034 message.
    /// </summary>
    /// <remarks>
    /// ASCII only. Diagnostics are rendered to consoles and CI logs whose encoding is not ours to
    /// choose, and a mojibaked "±" in a range message is worse than "+/-".
    /// </remarks>
    private static readonly Dictionary<string, string> FloatLimits = new(StringComparer.Ordinal)
    {
        ["f2"] = "the largest finite f2 is +/-65500",
        ["f4"] = "the largest finite f4 is +/-3.4028235E+38",
        ["f8"] = "the largest finite f8 is +/-1.7976931348623157E+308"
    };

    private static object? OutOfRange(string attribute, string label, string raw, string type,
        IonSyntaxBase node, List<IonAttributeProblem> problems)
    {
        problems.Add(new(IonAnalyticCodes.ION0034_AttributeArgumentOutOfRange,
            node, [attribute, label, raw, type, FloatLimits[type]]));
        return null;
    }

    private static object? ParseText(string type, string text) => type switch
    {
        "guid" => Guid.TryParse(text, out var guid) ? guid : null,
        "uri" => Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out var uri) ? uri : null,
        "datetime" => DateTime.TryParse(text, CultureInfo.InvariantCulture, out var dt) ? dt : null,
        "dateonly" => DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var d) ? d : null,
        "timeonly" => TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out var t) ? t : null,
        "duration" => TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts) ? ts : null,
        _ => null
    };
}
