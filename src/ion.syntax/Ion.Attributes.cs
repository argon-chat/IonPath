namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

/// <summary>
/// Attributes, both use site and declaration.
/// <code>
/// use          := '@' identifier ( '(' ( arg ( ',' arg )* )? ')' )?
/// arg          := ( identifier ':' )? literal
/// declaration  := "attribute" '@' identifier argList ( "on" target ( ',' target )* )? ';'
/// </code>
/// </summary>
public partial class IonParser
{
    #region use site

    private static Parser<char, IonAttributeSyntax> Attribute =>
        Map(
            (pos, name, args) => new IonAttributeSyntax(name, args).WithPos(pos),
            CurrentPos,
            Char('@').Then(Identifier).Before(SkipTrivia),
            AttributeArgList
                .Optional()
                .Select(opt => opt.HasValue ? opt.Value : [])
        ).Before(SkipTrivia);

    /// <summary>
    /// The name of a named argument. Atomic: a positional argument may itself begin with an
    /// identifier (<c>Status.Active</c>, <c>true</c>), so a failed <c>name :</c> match has to
    /// leave the input untouched for the literal parser.
    /// </summary>
    private static Parser<char, IonIdentifier> AttributeArgName =>
        Try(Identifier.Before(Char(':'))).Before(SkipTrivia);

    /// <summary>
    /// One argument. Positional after named is <em>accepted</em> here on purpose: the C# ordering
    /// rule is a semantic diagnostic, and representing the mistake lets the semantic layer say
    /// "positional arguments must precede named arguments" instead of the grammar failing with an
    /// unhelpful "expected ')'".
    /// </summary>
    private static Parser<char, IonAttributeArgumentSyntax> AttributeArgument =>
        Map(
            (pos, name, value) =>
                new IonAttributeArgumentSyntax(name.GetValueOrDefault(), value)
                    .WithPos(pos, value.EndPosition ?? pos),
            CurrentPos,
            AttributeArgName.Optional(),
            Literal);

    /// <summary>
    /// <c>( ... )</c>. Not wrapped in a <c>Try</c>: once the <c>(</c> is seen the argument list is
    /// committed, so <c>@Foo(</c> and <c>@Foo(1 2)</c> are parse errors rather than silently
    /// degrading to "attribute with no arguments" (which is what the old raw-span parser did).
    /// <para>
    /// A trailing comma is rejected, matching every other comma separated list in the language
    /// (fields, enum members, declaration arguments).
    /// </para>
    /// <para>
    /// The interior skips <see cref="SkipTriviaAll"/>, not <see cref="SkipTrivia"/>: nothing
    /// between <c>(</c> and <c>)</c> can carry documentation, so a stray <c>///</c> or
    /// <c>/** */</c> in there is trivia rather than a parse error. <see cref="Literal"/> itself
    /// still stops in front of a doc comment, which is what future consumers (a field default
    /// value) need so they do not swallow the next member's doc.
    /// </para>
    /// </summary>
    private static Parser<char, List<IonAttributeArgumentSyntax>> AttributeArgList =>
        AttributeArgument
            .Before(SkipTriviaAll)
            .Separated(Char(',').Before(SkipTriviaAll))
            .Between(Char('(').Before(SkipTriviaAll), SkipTriviaAll.Then(Char(')')))
            .Select(args => args.ToList());

    #endregion

    #region declaration

    /// <summary>
    /// One target, kept as the bare identifier the author wrote.
    /// <para>
    /// The set of legal targets — <c>msg</c>, <c>field</c>, <c>enum</c>, <c>flags</c>,
    /// <c>enumMember</c>, <c>union</c>, <c>unionCase</c>, <c>service</c>, <c>method</c>,
    /// <c>argument</c>, <c>typedef</c>, <c>attribute</c> — is closed, but it is <b>not</b>
    /// enforced here. Rejecting an unknown keyword in the grammar would abort the whole
    /// <c>attribute</c> declaration and lose error recovery for everything after it (the same
    /// reasoning that keeps a repeated type modifier a diagnostic rather than a parse error, see
    /// <c>Ion.Messages.cs</c>). Accepting the identifier and carrying its position lets
    /// <c>ion.compiler.AttributeValidationStage</c> answer with a targeted "unknown attribute
    /// target 'x', expected one of …" instead.
    /// </para>
    /// <para>
    /// <c>ion.compiler.runtime.IonAttributeTargets.Keywords</c> is the single authority for the
    /// list; this layer deliberately keeps no second copy to drift out of sync with it.
    /// </para>
    /// </summary>
    private static Parser<char, IonIdentifier> AttributeTarget => Identifier.Before(SkipTrivia);

    /// <summary>
    /// <c>on field, unionCase</c>. At least one target is required (a bare <c>on;</c> is a parse
    /// error); duplicates and unknown keywords are accepted and left for the semantic layer.
    /// </summary>
    private static Parser<char, List<IonIdentifier>> AttributeTargetClause =>
        Keyword("on")
            .Then(AttributeTarget.SeparatedAtLeastOnce(Char(',').Before(SkipTrivia)))
            .Select(t => t.ToList());

    /// <summary>
    /// Omitting the <c>on</c> clause yields <see langword="null"/> targets, meaning "any target" —
    /// so every attribute written before the clause existed keeps compiling unchanged.
    /// </summary>
    private static Parser<char, IonAttributeDefSyntax> AttributeDefCore =>
        Map(
            (pos, _, name, args, targets) =>
                new IonAttributeDefSyntax(name, args.ToList(), targets.GetValueOrDefault()).WithPos(pos),
            CurrentPos,
            String("attribute").Before(SkipTrivia),
            Char('@').Then(Identifier).Before(SkipTrivia),
            ArgList,
            AttributeTargetClause.Optional().Before(Char(';'))
        );

    public static Parser<char, IonAttributeDefSyntax> AttributeDef => WithLeading(AttributeDefCore);

    #endregion
}
