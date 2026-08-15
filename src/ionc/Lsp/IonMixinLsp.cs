namespace ion.compiler.Lsp;

using System.Text;
using ion.syntax;

/// <summary>
/// One field a <c>with</c> clause brought into a declaration, and where it came from.
/// </summary>
/// <param name="Origin">
/// The mixin that declared the field, or <see langword="null"/> when the declaration wrote it
/// itself.
/// </param>
/// <param name="ListedAs">
/// The name written in the root declaration's <c>with</c> clause that this field arrived through.
/// Differs from <paramref name="Origin"/> when the field came in transitively —
/// <c>mixin 'Audited' (included by 'Traced')</c> — which is exactly what a diamond looks like.
/// </param>
public sealed record IonMixinField(IonFieldSyntax Field, string? Origin, string? ListedAs);

/// <summary>
/// The mixin-aware half of the language server: which mixins are declared, what a <c>with</c>
/// clause actually contributes, and who includes whom.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="IonAttributeLsp"/>, and for the same reason: mixins need a handful of
/// cross-handler questions answered consistently (hover, completion, definition, references,
/// rename, symbols, semantic tokens all ask at least one of them), and answering each one
/// separately per handler is how the answers drift apart.
/// </para>
/// <para>
/// <strong>The expansion here mirrors <c>ion.compiler.MixinExpansionStage</c> exactly</strong> —
/// depth-first over the <c>with</c> list left to right, base mixins before their includer, one
/// visited set for the whole expansion so a mixin contributes once however many paths reach it,
/// then the declaration's own fields last. That order <em>is</em> the wire field numbering, so an
/// editor that showed a different one would be worse than showing nothing. It is deliberately a
/// re-implementation rather than a call into the stage: the stage reports diagnostics and needs a
/// <c>CompilationContext</c>, and hover has to work on a file that has never compiled.
/// </para>
/// <para>
/// Everything here is defensive about cycles. A cyclic mixin is ION0064 and the compiler refuses
/// it, but the editor sees the source mid-edit — <c>mixin A with A</c> exists for as long as it
/// takes to finish typing the real name — so every walk carries a visited set.
/// </para>
/// </remarks>
public static class IonMixinLsp
{
    // ── Declared mixins ────────────────────────────────────────────────

    /// <summary>Every <c>mixin</c> declared anywhere in the workspace, by name. First one wins.</summary>
    /// <remarks>
    /// Ordinal, matching <c>CompilationContext.RegisterMixin</c>. A second declaration of the same
    /// name is ION0002 and is not this code's problem.
    /// </remarks>
    public static Dictionary<string, IonMixinSyntax> Declarations(IonWorkspace workspace)
    {
        var result = new Dictionary<string, IonMixinSyntax>(StringComparer.Ordinal);

        foreach (var file in workspace.ParsedFiles)
            foreach (var mixin in file.mixinSyntaxes)
                result.TryAdd(mixin.Name.Identifier, mixin);

        return result;
    }

    public static IonMixinSyntax? Find(IonWorkspace workspace, string name)
        => Declarations(workspace).GetValueOrDefault(name);

    /// <summary>The <c>with</c> clause of a <c>msg</c> or a <c>mixin</c>, or <see langword="null"/>.</summary>
    /// <remarks>
    /// <see langword="null"/> means no clause was written, which is not the same as an empty one —
    /// <c>with</c> requires at least one name, so an empty list is unreachable.
    /// </remarks>
    public static List<IonIdentifier>? ClauseOf(IonSyntaxMember declaration) => declaration switch
    {
        IonMessageSyntax message => message.Mixins,
        IonMixinSyntax mixin => mixin.Mixins,
        _ => null
    };

    /// <summary>The declaration's own name, for the two kinds that can carry a <c>with</c> clause.</summary>
    public static string? NameOf(IonSyntaxMember declaration) => declaration switch
    {
        IonMessageSyntax message => message.Name.Identifier,
        IonMixinSyntax mixin => mixin.Name.Identifier,
        _ => null
    };

    /// <summary>Every declaration in the workspace that can carry a <c>with</c> clause.</summary>
    public static IEnumerable<(IonFileSyntax File, IonSyntaxMember Declaration)> Includers(IonWorkspace workspace)
    {
        foreach (var file in workspace.ParsedFiles)
        {
            foreach (var mixin in file.mixinSyntaxes)
                yield return (file, mixin);

            foreach (var message in file.messageSyntaxes)
                yield return (file, message);
        }
    }

    /// <summary>
    /// The declarations that name <paramref name="mixinName"/> in their own <c>with</c> clause,
    /// as <c>msg 'Document'</c> / <c>mixin 'Traced'</c> phrases, in workspace order.
    /// </summary>
    /// <remarks>
    /// Direct inclusion only. A message that reaches the mixin transitively is not listed, because
    /// the useful question at a declaration is "which clause do I edit to stop including this",
    /// and that is always a direct one.
    /// </remarks>
    public static List<string> IncludedBy(IonWorkspace workspace, string mixinName)
    {
        var result = new List<string>();

        foreach (var (_, declaration) in Includers(workspace))
        {
            if (ClauseOf(declaration) is not { } clause)
                continue;

            if (clause.Any(n => string.Equals(n.Identifier, mixinName, StringComparison.Ordinal)))
                result.Add(Describe(declaration));
        }

        return result;
    }

    /// <summary><c>msg 'Document'</c> / <c>mixin 'Traced'</c>.</summary>
    public static string Describe(IonSyntaxMember declaration) => declaration switch
    {
        IonMessageSyntax message => $"msg '{message.Name.Identifier}'",
        IonMixinSyntax mixin => $"mixin '{mixin.Name.Identifier}'",
        _ => "declaration"
    };

    // ── Expansion ──────────────────────────────────────────────────────

    /// <summary>
    /// The full field list a declaration ends up with, in wire order, each field tagged with the
    /// mixin it came from.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>MixinExpansionStage.ExpandFrom</c>. Fields whose names collide are ION0065 and
    /// the compiler drops the loser; the same rule is applied here so the editor's field list is
    /// the one the wire will carry, not a longer list that only exists in a failing build.
    /// </remarks>
    public static List<IonMixinField> Expand(
        IonSyntaxMember declaration,
        IReadOnlyDictionary<string, IonMixinSyntax> mixins)
    {
        var own = declaration switch
        {
            IonMessageSyntax message => message.Fields,
            IonMixinSyntax mixin => mixin.Fields,
            _ => []
        };

        var fields = new List<IonMixinField>();
        var byName = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var written in ClauseOf(declaration) ?? [])
            if (mixins.TryGetValue(written.Identifier, out var mixin))
                Splice(mixin, written.Identifier, mixins, visited, fields, byName);

        foreach (var field in own)
            if (byName.Add(field.Name.Identifier))
                fields.Add(new IonMixinField(field, null, null));

        return fields;
    }

    private static void Splice(
        IonMixinSyntax mixin, string listed,
        IReadOnlyDictionary<string, IonMixinSyntax> mixins,
        HashSet<string> visited, List<IonMixinField> fields, HashSet<string> byName)
    {
        // A diamond, or a cycle the compiler is about to reject as ION0064. Either way this mixin
        // has already had its say.
        if (!visited.Add(mixin.Name.Identifier))
            return;

        foreach (var written in mixin.Mixins ?? [])
            if (mixins.TryGetValue(written.Identifier, out var included))
                Splice(included, listed, mixins, visited, fields, byName);

        foreach (var field in mixin.Fields)
            if (byName.Add(field.Name.Identifier))
                fields.Add(new IonMixinField(field, mixin.Name.Identifier, listed));
    }

    /// <summary>
    /// The fields one <c>with</c> clause entry actually contributes to its includer — which is
    /// <em>not</em> the same as the mixin's own field list.
    /// </summary>
    /// <remarks>
    /// Two things can make it smaller. A diamond: in
    /// <c>msg Document with Audited, Traced</c> over <c>mixin Traced with Audited</c>, the entry
    /// <c>Traced</c> contributes only <c>traceId</c>, because <c>Audited</c> was already spliced by
    /// the earlier entry. And a collision: a field name the declaration also declares itself is
    /// ION0065 and does not reach the wire twice. Saying "contributes nothing here" is the single
    /// most useful thing hover can tell someone reading a diamond.
    /// </remarks>
    public static List<IonMixinField> ContributionOf(
        IonSyntaxMember declaration, string entry,
        IReadOnlyDictionary<string, IonMixinSyntax> mixins)
        => Expand(declaration, mixins)
            .Where(f => f.ListedAs is { } listed && string.Equals(listed, entry, StringComparison.Ordinal))
            .ToList();

    // ── Rendering ──────────────────────────────────────────────────────

    /// <summary>The declaration as it would be written, <c>with</c> clause and body included.</summary>
    public static string Signature(IonMixinSyntax mixin)
    {
        var sb = new StringBuilder("mixin ").Append(mixin.Name.Identifier);

        if (mixin.Mixins is { Count: > 0 } clause)
            sb.Append(" with ").Append(string.Join(", ", clause.Select(m => m.Identifier)));

        if (mixin.Fields.Count == 0)
            return sb.Append(" {}").ToString();

        sb.Append(" {\n");
        foreach (var field in mixin.Fields)
            sb.Append("    ").Append(field.Name.Identifier).Append(": ")
                .Append(IonLspHelpers.FormatTypeSyntax(field.Type)).Append(";\n");

        return sb.Append('}').ToString();
    }

    /// <summary>The <c>with</c> clause as written, for a message signature.</summary>
    public static string WithClause(List<IonIdentifier>? clause)
        => clause is { Count: > 0 } ? " with " + string.Join(", ", clause.Select(m => m.Identifier)) : "";

    // ── Lexical context ────────────────────────────────────────────────

    /// <summary>
    /// The <c>with</c> clause the cursor is in — the declaration that opened it and the names
    /// already written — or <see langword="null"/> when the cursor is somewhere else.
    /// </summary>
    /// <param name="Owner">The declaration keyword, <c>msg</c> or <c>mixin</c>.</param>
    /// <param name="Declared">The name being declared, so it can be kept out of its own clause.</param>
    /// <param name="Written">Names already listed in the clause, complete ones only.</param>
    public sealed record Clause(string Owner, string? Declared, IReadOnlyList<string> Written);

    /// <summary>
    /// Whether the cursor sits where a mixin name belongs, and what is already there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Purely lexical, for the same reason <see cref="IonAttributeLsp.InTargetClause"/> is: this
    /// runs while the author is mid-token, and a declaration whose <c>with</c> clause is still
    /// being typed usually does not parse — <c>msg Document with </c> has no body yet, so there is
    /// no <see cref="IonMessageSyntax"/> to ask.
    /// </para>
    /// <para>
    /// The statement is reconstructed from code-classified characters only, so a <c>with</c>
    /// inside a comment or a string opens nothing, and it is cut at the last <c>;</c>, <c>{</c> or
    /// <c>}</c> before the cursor — which is what stops a field named <c>within</c> deep inside a
    /// body from being read as a clause.
    /// </para>
    /// </remarks>
    public static Clause? InWithClause(IonScannedDocument scan, int line, int character)
    {
        if (line < 0 || line >= scan.Lines.Length || scan.IsCommentOrString(line, Math.Max(0, character - 1)))
            return null;

        var text = new StringBuilder();

        // A `with` clause is one line in practice but is allowed to wrap, so a small window back.
        for (var l = Math.Max(0, line - 8); l <= line; l++)
        {
            var to = l == line ? Math.Min(character, scan.Lines[l].Length) : scan.Lines[l].Length;
            text.Append(CodeOf(scan, l, to)).Append(' ');
        }

        var statement = text.ToString();

        // Anything that closes or opens a block ends the header. `{` in particular: past it the
        // cursor is in a field list, not in a clause.
        var cut = statement.LastIndexOfAny([';', '{', '}']);
        if (cut >= 0)
            statement = statement[(cut + 1)..];

        var words = statement.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0 || words[0] is not ("msg" or "mixin"))
            return null;

        // Split on commas too, so `with A,B` and `with A, B` both enumerate.
        var withAt = Array.FindIndex(words, w => w == "with");

        if (withAt < 0)
            return null;

        var declared = withAt >= 2 ? words[1] : null;

        // Only the complete entries: the trailing word is what the cursor is still typing, unless
        // the text ends on a separator, in which case a fresh entry is starting.
        var entries = string.Join(' ', words.Skip(withAt + 1))
            .Split(',', StringSplitOptions.TrimEntries)
            .ToList();

        if (entries.Count > 0 && !statement.TrimEnd().EndsWith(','))
            entries.RemoveAt(entries.Count - 1);

        return new Clause(words[0], declared,
            entries.Where(e => e.Length > 0).ToList());
    }

    /// <summary>A line's code characters up to <paramref name="to"/>, comments and strings blanked.</summary>
    private static string CodeOf(IonScannedDocument scan, int line, int to)
    {
        var text = scan.Lines[line];
        var sb = new StringBuilder();

        for (var i = 0; i < Math.Min(to, text.Length); i++)
            sb.Append(scan.ClassAt(line, i) == IonCharClass.Code ? text[i] : ' ');

        return sb.ToString();
    }

    /// <summary>
    /// The standing explanation of what a mixin is. Repeated on every mixin hover on purpose: "it
    /// is not a type" is the one thing about mixins that surprises people, and it is the cause of
    /// ION0066.
    /// </summary>
    public const string NotATypeNote =
        "**Not a type.** A mixin is a field-set template: it has no wire identity, no entry in "
        + "`ion.lock.json` and no generated declaration. It cannot be written in type position — "
        + "that is `ION0066`. Include it with `with`.";
}
