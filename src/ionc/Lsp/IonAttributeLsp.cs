namespace ion.compiler.Lsp;

using System.Text;
using ion.runtime;
using ion.syntax;

/// <summary>One declared parameter of an attribute, as the language server needs to show it.</summary>
/// <param name="Type">The type as it would be written in source — <c>string?</c>, <c>i4[]</c>.</param>
/// <param name="IsOptional">
/// Declared <c>T?</c>. There is no <c>= default</c> syntax, so this is the only way a parameter can
/// be omitted, and optional parameters are trailing (ION0039) — which is what lets the argument
/// index the cursor is on map straight onto a parameter slot.
/// </param>
public sealed record IonAttributeParameter(string Name, string Type, bool IsOptional, string? Doc);

/// <summary>
/// A declared attribute, normalised from either a parsed <c>attribute @x(…) on …;</c> in the
/// workspace or a builtin module's already lowered <see cref="IonAttributeType"/>.
/// </summary>
/// <param name="Targets">
/// <see langword="null"/> means the declaration had no <c>on</c> clause and is allowed anywhere —
/// not the same as an empty list. Mirrors <see cref="IonAttributeType.targets"/>.
/// </param>
/// <param name="Origin">Where it came from, for the hover footer: a module name or a file name.</param>
public sealed record IonAttributeDeclaration(
    string Name,
    IReadOnlyList<IonAttributeParameter> Parameters,
    IReadOnlyList<IonAttributeTarget>? Targets,
    string? Doc,
    string Origin,
    bool IsBuiltin)
{
    public bool Allows(IonAttributeTarget target) => Targets is null || Targets.Contains(target);

    /// <summary>The parameter list as written — <c>since: string?, reason: string?</c>.</summary>
    public string Signature => string.Join(", ", Parameters.Select(p => $"{p.Name}: {p.Type}"));

    /// <summary>The full use-site spelling — <c>@deprecated(since: string?, reason: string?)</c>.</summary>
    public string Label => Parameters.Count == 0 ? $"@{Name}" : $"@{Name}({Signature})";

    /// <summary>The <c>on</c> clause, or <see langword="null"/> when the attribute is unrestricted.</summary>
    public string? TargetClause =>
        Targets is null ? null : "on " + IonAttributeTargets.Format(Targets);

    public int RequiredCount => Parameters.Count(p => !p.IsOptional);

    public int IndexOf(string parameterName)
    {
        for (var i = 0; i < Parameters.Count; i++)
            if (string.Equals(Parameters[i].Name, parameterName, StringComparison.Ordinal))
                return i;

        return -1;
    }
}

/// <summary>
/// One attribute use with each written argument matched to the parameter slot it fills, the same
/// way <c>IonAttributeBinder</c> does it — positionally, then by name.
/// </summary>
/// <param name="Values">
/// One entry per declared parameter, in declaration order. <see langword="null"/> where nothing was
/// written for that slot.
/// </param>
/// <param name="Surplus">Arguments that matched no parameter, rendered as written.</param>
public sealed record IonAttributeUseBinding(
    IonAttributeDeclaration Declaration,
    IReadOnlyList<string?> Values,
    IReadOnlyList<string> Surplus);

/// <summary>
/// A call the cursor sits inside: <c>@Retry(<i>|</i>)</c>, <c>Foo(<i>|</i>)</c>. Produced by a
/// forward scan over the whole document rather than the current line, so an argument list split
/// across lines and a <c>(</c> that only occurs inside a comment or a string both behave.
/// </summary>
/// <param name="ArgumentIndex">Zero-based index of the argument the cursor is in.</param>
/// <param name="ActiveArgumentName">
/// The name of the argument being written, when it was written as <c>name: value</c>.
/// </param>
/// <param name="PositionalsBefore">
/// How many <em>positional</em> arguments precede the cursor's argument. This — not
/// <see cref="ArgumentIndex"/> — is the parameter slot an unnamed argument fills, because a named
/// argument does not consume a positional slot.
/// </param>
/// <param name="IsAttribute">The name was preceded by <c>@</c>.</param>
/// <param name="IsDeclaration">
/// The call is the parameter list of an <c>attribute @x(…)</c> <em>declaration</em>, not a use.
/// </param>
public sealed record IonCallContext(
    string Name,
    int NameLine,
    int NameStartChar,
    int OpenParenLine,
    int OpenParenChar,
    int ArgumentIndex,
    string? ActiveArgumentName,
    int PositionalsBefore,
    IReadOnlyList<string?> ArgumentNames,
    bool IsAttribute,
    bool IsDeclaration);

/// <summary>
/// The attribute-aware half of the language server: which attributes are declared and visible, what
/// their signatures are, which declaration position the cursor is in, and where inside an argument
/// list it sits.
/// </summary>
/// <remarks>
/// Two resolution paths exist on purpose. Anything that needs a <em>target</em> for an attribute
/// that is already written goes through <see cref="IonAttributeSites"/> over the parsed tree, which
/// is exactly what the compiler validates against (so hover can never disagree with ION0038).
/// Anything that has to work while the author is still typing — completion, signature help — goes
/// through the lexical scanner instead, because a half-written <c>@</c> often means the enclosing
/// declaration does not parse yet.
/// </remarks>
public static class IonAttributeLsp
{
    // ── Declared attributes ────────────────────────────────────────────

    /// <summary>
    /// Every attribute visible from <paramref name="uri"/>, builtins first, in the same precedence
    /// order <c>CompilationContext.ResolveAttributeType</c> uses: a global (feature-gated) module
    /// wins over a project declaration of the same name.
    /// </summary>
    public static IReadOnlyList<IonAttributeDeclaration> Declarations(IonWorkspace workspace, string uri)
    {
        var result = new List<IonAttributeDeclaration>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var module in GlobalModules(workspace, uri))
            foreach (var attribute in module.Attributes)
                if (seen.Add(attribute.name.Identifier))
                    result.Add(FromRuntime(attribute, module.Name, isBuiltin: true));

        // The parsed syntax rather than ctx.ProcessedModules: it is refreshed on every keystroke,
        // it keeps the `on` clause exactly as written, and it is present even before the file has
        // ever compiled.
        foreach (var file in workspace.ParsedFiles)
            foreach (var declaration in file.attributeDefSyntaxes)
                if (seen.Add(declaration.Name.Identifier))
                    result.Add(FromSyntax(declaration, file.Name));

        foreach (var module in workspace.GetExternalModulesForFile(uri))
            foreach (var attribute in module.Attributes)
                if (seen.Add(attribute.name.Identifier))
                    result.Add(FromRuntime(attribute, module.SourceModule ?? module.Name, isBuiltin: false));

        return result;
    }

    public static IonAttributeDeclaration? Find(IonWorkspace workspace, string uri, string name)
        => Declarations(workspace, uri)
            .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The builtin modules enabled for the project owning <paramref name="uri"/>.
    /// </summary>
    /// <remarks>
    /// Read from the project's <c>ion.config.json</c> features rather than from
    /// <c>LastContext.GlobalModules</c>, so <c>@deprecated</c> resolves on the very first keystroke
    /// in a file that has never compiled. The gating matches
    /// <c>CompilationContext.Create</c> exactly — an <c>orleans</c>-only attribute must not appear
    /// in a project that never enabled the feature.
    /// </remarks>
    private static IEnumerable<IonModule> GlobalModules(IonWorkspace workspace, string uri)
    {
        var context = workspace.GetContextForFile(uri);

        if (context is not null)
            return context.GlobalModules;

        var features = workspace.FindProjectForFile(uri)?.GetFeatures() ?? [];
        var modules = new List<IonModule>();

        if (features.Contains("std")) modules.Add(IonModule.GetStdModule.Value);
        if (features.Contains("orleans")) modules.Add(IonModule.GetOrleansModule.Value);

        return modules;
    }

    private static IonAttributeDeclaration FromSyntax(IonAttributeDefSyntax syntax, string origin)
    {
        var parameters = syntax.Args
            .Select(a => new IonAttributeParameter(
                a.argName.Identifier, FormatType(a.type), a.type.IsOptional, a.Comments))
            .ToList();

        return new IonAttributeDeclaration(
            syntax.Name.Identifier, parameters, ParseTargets(syntax.Targets),
            syntax.Comments, origin, IsBuiltin: false);
    }

    private static IonAttributeDeclaration FromRuntime(IonAttributeType type, string origin, bool isBuiltin)
    {
        var parameters = type.arguments
            .Select(a => new IonAttributeParameter(
                a.name.Identifier,
                IonAttributeBinder.TypeName(a.type),
                IonAttributeBinder.IsOptional(a),
                a.Doc))
            .ToList();

        return new IonAttributeDeclaration(
            type.name.Identifier, parameters, type.targets, type.Doc, origin, isBuiltin);
    }

    /// <summary>
    /// Mirrors <c>TransformStage.Targets</c>: unknown keywords are dropped (they are ION0038 on
    /// their own), duplicates collapse, and a clause left with nothing legal in it degrades to
    /// "unrestricted" rather than to "forbidden everywhere".
    /// </summary>
    private static IReadOnlyList<IonAttributeTarget>? ParseTargets(List<IonIdentifier>? written)
    {
        if (written is null)
            return null;

        var targets = new List<IonAttributeTarget>();

        foreach (var word in written)
            if (IonAttributeTargets.TryParse(word.Identifier, out var target) && !targets.Contains(target))
                targets.Add(target);

        return targets.Count == 0 ? null : targets;
    }

    // ── Matching a use against its declaration ─────────────────────────

    /// <summary>
    /// Fills each parameter slot with the argument that binds to it, so hover can show
    /// <c>since = "2.0"</c> instead of leaving the reader to count commas.
    /// </summary>
    /// <remarks>
    /// Deliberately a re-implementation of the positional/named matching rather than a call into
    /// <c>IonAttributeBinder</c>: the binder converts to CLR values and reports diagnostics, neither
    /// of which hover wants, and it needs an <see cref="IonAttributeType"/> that does not exist for
    /// a file that has not compiled yet. It stays faithful to the one rule that matters here —
    /// positional arguments fill slots in order, a named argument goes to its own slot.
    /// </remarks>
    public static IonAttributeUseBinding BindForDisplay(IonAttributeDeclaration declaration, IonAttributeSyntax use)
    {
        var values = new string?[declaration.Parameters.Count];
        var surplus = new List<string>();
        var next = 0;

        foreach (var argument in use.Args)
        {
            var text = RenderLiteral(argument.Value);

            if (argument.Name is null)
            {
                if (next < values.Length)
                    values[next++] = text;
                else
                    surplus.Add(text);

                continue;
            }

            var index = declaration.IndexOf(argument.Name.Identifier);

            if (index < 0)
                surplus.Add($"{argument.Name.Identifier}: {text}");
            else
                values[index] = text;
        }

        return new IonAttributeUseBinding(declaration, values, surplus);
    }

    // ── Rendering ──────────────────────────────────────────────────────

    /// <summary>A literal as source text, round-tripping the author's own spelling where one exists.</summary>
    public static string RenderLiteral(IonLiteralSyntax literal) => literal switch
    {
        // Raw, not Value: `0xFF` and `1_000` must not be silently restated as `255` and `1000`.
        IonIntegerLiteralSyntax i => i.Raw,
        IonFloatLiteralSyntax f => f.Raw,
        IonStringLiteralSyntax s => Quote(s.Value),
        IonBoolLiteralSyntax b => b.Value ? "true" : "false",
        IonNullLiteralSyntax => "null",
        IonEnumRefLiteralSyntax e => $"{e.TypeName.Identifier}.{e.Member.Identifier}",
        IonArrayLiteralSyntax a => "[" + string.Join(", ", a.Items.Select(RenderLiteral)) + "]",
        _ => "?"
    };

    /// <summary>Re-escapes a decoded string value back into the source form the grammar accepts.</summary>
    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');

        foreach (var c in value)
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                < ' ' => $"\\u{(int)c:X4}",
                _ => c.ToString()
            });

        return sb.Append('"').ToString();
    }

    /// <inheritdoc cref="IonLspHelpers.FormatTypeSyntax"/>
    /// <remarks>
    /// Was a third copy of the same renderer, with the same nesting bug. An attribute parameter is
    /// restricted to a builtin (ION0003 / ION0004), so it could not currently be nested — but the
    /// signature this feeds is what completion, signature help and hover all show, and a renderer
    /// that is only correct because of a rule enforced somewhere else is one rule change away from
    /// being wrong.
    /// </remarks>
    public static string FormatType(IonUnderlyingTypeSyntax type)
        => IonLspHelpers.FormatTypeSyntax(type);

    /// <summary>The declaration as it would be written, <c>on</c> clause included.</summary>
    public static string DeclarationSignature(IonAttributeDeclaration declaration)
    {
        var clause = declaration.TargetClause is { } on ? " " + on : "";
        return $"attribute @{declaration.Name}({declaration.Signature}){clause};";
    }

    // ── The attribute under the cursor ─────────────────────────────────

    /// <summary>
    /// The attribute use whose name token covers the cursor, together with the declaration kind it
    /// is written on.
    /// </summary>
    /// <remarks>
    /// <see cref="IonAttributeSites"/> is the compiler's own enumeration of "every position an
    /// attribute may be written, and what that position is". Reusing it is what makes the target
    /// hover reports identical to the one ION0038 checks against.
    /// </remarks>
    public static IonAttributeSite? SiteAt(IonFileSyntax file, int line, int character)
    {
        foreach (var site in IonAttributeSites.Of(file))
            if (IonLspHelpers.Covers(site.Attribute.Name, line, character))
                return site;

        return null;
    }

    /// <summary>Every attribute use in a file, with its target. Ordered as written.</summary>
    public static IEnumerable<IonAttributeSite> Sites(IonFileSyntax file) => IonAttributeSites.Of(file);

    // ── Lexical context ────────────────────────────────────────────────

    private enum Bracket
    {
        Paren,
        Square,
        Brace
    }

    private readonly record struct Frame(Bracket Kind, int Line, int Char);

    /// <summary>
    /// The innermost bracket frames open at the cursor, outermost first. Comments and string
    /// literals are skipped via the scanner mask, so a <c>(</c> in prose never opens a frame.
    /// </summary>
    private static List<Frame> Frames(IonScannedDocument scan, int line, int character)
    {
        var stack = new List<Frame>();

        for (var l = 0; l <= line && l < scan.Lines.Length; l++)
        {
            var text = scan.Lines[l];
            var to = l == line ? Math.Min(character, text.Length) : text.Length;

            for (var i = 0; i < to; i++)
            {
                if (scan.ClassAt(l, i) != IonCharClass.Code)
                    continue;

                switch (text[i])
                {
                    case '(': stack.Add(new Frame(Bracket.Paren, l, i)); break;
                    case '[': stack.Add(new Frame(Bracket.Square, l, i)); break;
                    case '{': stack.Add(new Frame(Bracket.Brace, l, i)); break;

                    case ')' or ']':
                        if (stack.Count > 0 && stack[^1].Kind != Bracket.Brace)
                            stack.RemoveAt(stack.Count - 1);
                        break;

                    // A `}` closes its block *and* discards any bracket left unbalanced inside it,
                    // so one stray `(` earlier in the file cannot poison every later position.
                    case '}':
                        while (stack.Count > 0)
                        {
                            var top = stack[^1];
                            stack.RemoveAt(stack.Count - 1);
                            if (top.Kind == Bracket.Brace) break;
                        }

                        break;
                }
            }
        }

        return stack;
    }

    /// <summary>
    /// The call the cursor sits in, or <see langword="null"/> when it is not inside an argument
    /// list. A cursor inside an array literal still reports the enclosing call, with the argument
    /// index of the argument that holds the array.
    /// </summary>
    public static IonCallContext? FindCall(IonScannedDocument scan, int line, int character)
    {
        var frames = Frames(scan, line, character);
        Frame? open = null;

        for (var i = frames.Count - 1; i >= 0; i--)
        {
            if (frames[i].Kind == Bracket.Brace)
                break;

            if (frames[i].Kind == Bracket.Paren)
            {
                open = frames[i];
                break;
            }
        }

        if (open is not { } paren)
            return null;

        var text = scan.Lines[paren.Line];

        var nameEnd = paren.Char;
        while (nameEnd > 0 && char.IsWhiteSpace(text[nameEnd - 1]))
            nameEnd--;

        var nameStart = nameEnd;
        while (nameStart > 0 && IonLspHelpers.IsWordChar(text[nameStart - 1]))
            nameStart--;

        if (nameStart >= nameEnd)
            return null;

        var isAttribute = nameStart > 0 && text[nameStart - 1] == '@';
        var isDeclaration = isAttribute && PrecedingWord(text, nameStart - 1) == "attribute";

        var chunks = SplitArguments(scan, paren, line, character);
        var names = chunks.Select(ArgumentName).ToList();

        return new IonCallContext(
            text[nameStart..nameEnd],
            paren.Line,
            nameStart,
            paren.Line,
            paren.Char,
            chunks.Count - 1,
            names[^1],
            names.Take(names.Count - 1).Count(n => n is null),
            names,
            isAttribute,
            isDeclaration);
    }

    /// <summary>The identifier ending immediately before <paramref name="before"/>, ignoring spaces.</summary>
    private static string PrecedingWord(string text, int before)
    {
        var end = before;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            end--;

        var start = end;
        while (start > 0 && IonLspHelpers.IsWordChar(text[start - 1]))
            start--;

        return text[start..end];
    }

    /// <summary>
    /// The argument list text from just after <c>(</c> up to the cursor, split on top level commas.
    /// Always at least one chunk; the last one is the argument being written.
    /// </summary>
    /// <remarks>
    /// Characters classified as string or comment are dropped rather than copied, which is what
    /// makes a <c>,</c> inside <c>"a,b"</c> or inside <c>/* , */</c> not split an argument. The name
    /// prefix of a named argument survives that, because it is code.
    /// </remarks>
    private static List<string> SplitArguments(IonScannedDocument scan, Frame open, int line, int character)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        for (var l = open.Line; l <= line && l < scan.Lines.Length; l++)
        {
            var text = scan.Lines[l];
            var from = l == open.Line ? open.Char + 1 : 0;
            var to = l == line ? Math.Min(character, text.Length) : text.Length;

            for (var i = from; i < to; i++)
            {
                if (scan.ClassAt(l, i) != IonCharClass.Code)
                    continue;

                var c = text[i];

                switch (c)
                {
                    case '(' or '[':
                        depth++;
                        break;
                    case ')' or ']':
                        depth--;
                        break;
                    case ',' when depth == 0:
                        chunks.Add(current.ToString());
                        current.Clear();
                        continue;
                }

                current.Append(c);
            }

            current.Append(' ');
        }

        chunks.Add(current.ToString());
        return chunks;
    }

    /// <summary>The name of a <c>name: value</c> argument, or <see langword="null"/> when positional.</summary>
    public static string? ArgumentName(string chunk)
    {
        var text = chunk.TrimStart();
        var i = 0;

        while (i < text.Length && IonLspHelpers.IsWordChar(text[i]))
            i++;

        if (i == 0 || !char.IsLetter(text[0]) && text[0] != '_')
            return null;

        // `Status.Active` stops at the '.', so the tail test is what keeps an enum reference from
        // being read as a name.
        return text[i..].TrimStart().StartsWith(':') ? text[..i] : null;
    }

    // ── Which declaration position is the cursor in ────────────────────

    /// <summary>
    /// The <see cref="IonAttributeTarget"/> an attribute written at the cursor would apply to, or
    /// <see langword="null"/> when it cannot be told.
    /// </summary>
    /// <remarks>
    /// Purely lexical, and that is the point: this runs while the author is mid-token, when the
    /// enclosing declaration usually does not parse. <see langword="null"/> means "unknown" and
    /// callers must fall back to showing everything — a completion list that silently hides the
    /// attribute someone is reaching for is worse than one that is too long.
    /// </remarks>
    public static IonAttributeTarget? InferTarget(IonScannedDocument scan, int line, int character)
    {
        var frames = Frames(scan, line, character);

        var paren = LastBefore(frames, Bracket.Paren);
        var brace = LastBefore(frames, Bracket.Brace);

        // The inner frame wins: `service S { Foo(|) }` is an argument position, `msg M { |` is a
        // field position, and only the later of the two brackets says which.
        if (paren is { } p && (brace is null || p.Line > brace.Value.Line ||
                               p.Line == brace.Value.Line && p.Char > brace.Value.Char))
        {
            // A union case's own arguments lower to fields, not to arguments — see
            // TransformStage.PrependFields and the `union` arm of IonAttributeSites.
            if (brace is { } enclosing && Owner(scan, enclosing) == "union")
                return IonAttributeTarget.Field;

            return IonAttributeTarget.Argument;
        }

        if (brace is { } block)
            return Owner(scan, block) switch
            {
                "msg" => IonAttributeTarget.Field,
                "enum" or "flags" => IonAttributeTarget.EnumMember,
                "union" => IonAttributeTarget.UnionCase,
                "service" => IonAttributeTarget.Method,
                _ => null
            };

        return Following(scan, line) switch
        {
            "msg" => IonAttributeTarget.Msg,
            "enum" => IonAttributeTarget.Enum,
            "flags" => IonAttributeTarget.Flags,
            "union" => IonAttributeTarget.Union,
            "service" => IonAttributeTarget.Service,
            "typedef" => IonAttributeTarget.Typedef,
            "attribute" => IonAttributeTarget.Attribute,
            _ => null
        };
    }

    private static Frame? LastBefore(List<Frame> frames, Bracket kind)
    {
        for (var i = frames.Count - 1; i >= 0; i--)
            if (frames[i].Kind == kind)
                return frames[i];

        return null;
    }

    /// <summary>
    /// The declaration keyword that opened a <c>{</c> block: read from the same line when the brace
    /// is on the header, otherwise from the nearest preceding line that starts with one.
    /// </summary>
    private static string? Owner(IonScannedDocument scan, Frame brace)
    {
        var head = CodeOf(scan, brace.Line, 0, brace.Char).TrimStart();
        var keyword = LeadingKeyword(head);

        if (keyword is not null)
            return keyword;

        for (var l = brace.Line - 1; l >= 0 && l > brace.Line - 16; l--)
        {
            keyword = LeadingKeyword(CodeOf(scan, l, 0, scan.Lines[l].Length).TrimStart());
            if (keyword is not null)
                return keyword;
        }

        return null;
    }

    /// <summary>The declaration keyword of the first line at or after <paramref name="line"/> that has one.</summary>
    private static string? Following(IonScannedDocument scan, int line)
    {
        for (var l = line; l < scan.Lines.Length && l < line + 16; l++)
        {
            var keyword = LeadingKeyword(CodeOf(scan, l, 0, scan.Lines[l].Length).TrimStart());
            if (keyword is not null)
                return keyword;
        }

        return null;
    }

    private static string? LeadingKeyword(string text)
    {
        foreach (var keyword in Keywords)
            if (text.StartsWith(keyword, StringComparison.Ordinal) &&
                (text.Length == keyword.Length || !IonLspHelpers.IsWordChar(text[keyword.Length])))
                return keyword;

        return null;
    }

    private static readonly string[] Keywords =
        ["msg", "enum", "flags", "union", "service", "typedef", "attribute"];

    /// <summary>The code-classified characters of a line span, with comments and strings blanked out.</summary>
    private static string CodeOf(IonScannedDocument scan, int line, int from, int to)
    {
        if (line < 0 || line >= scan.Lines.Length)
            return "";

        var text = scan.Lines[line];
        var sb = new StringBuilder();

        for (var i = Math.Max(0, from); i < Math.Min(to, text.Length); i++)
            sb.Append(scan.ClassAt(line, i) == IonCharClass.Code ? text[i] : ' ');

        return sb.ToString();
    }

    /// <summary>
    /// Whether the cursor is after the <c>on</c> keyword of an <c>attribute</c> declaration, i.e.
    /// where a target keyword belongs.
    /// </summary>
    public static bool InTargetClause(IonScannedDocument scan, int line, int character)
    {
        if (Frames(scan, line, character).Count > 0)
            return false;

        // Walk back to the start of the statement. An attribute declaration is one `;` terminated
        // line in practice, but the clause is allowed to wrap.
        var text = new StringBuilder();

        for (var l = Math.Max(0, line - 8); l <= line && l < scan.Lines.Length; l++)
        {
            var to = l == line ? character : scan.Lines[l].Length;
            text.Append(CodeOf(scan, l, 0, to)).Append('\n');
        }

        var statement = text.ToString();
        var semicolon = statement.LastIndexOf(';');

        if (semicolon >= 0)
            statement = statement[(semicolon + 1)..];

        var trimmed = statement.TrimStart();

        if (LeadingKeyword(trimmed) != "attribute")
            return false;

        var close = trimmed.LastIndexOf(')');

        if (close < 0)
            return false;

        var tail = trimmed[(close + 1)..];

        return tail.TrimStart().StartsWith("on", StringComparison.Ordinal) &&
               (tail.TrimStart().Length == 2 || !IonLspHelpers.IsWordChar(tail.TrimStart()[2]));
    }

    /// <summary>
    /// Whether the word at the cursor is an attribute name being typed, i.e. is preceded by <c>@</c>.
    /// </summary>
    public static bool AfterAtSign(IonScannedDocument scan, int line, int character)
    {
        if (line < 0 || line >= scan.Lines.Length)
            return false;

        var text = scan.Lines[line];
        var start = Math.Min(character, text.Length);

        while (start > 0 && IonLspHelpers.IsWordChar(text[start - 1]))
            start--;

        return start > 0 && text[start - 1] == '@' && scan.ClassAt(line, start - 1) == IonCharClass.Code;
    }
}
