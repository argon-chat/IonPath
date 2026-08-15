namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

/// <summary>
/// Generic argument lists at a type <em>use</em> site — the <c>&lt;string, Array&lt;User&gt;&gt;</c>
/// of <c>Map&lt;string, Array&lt;User&gt;&gt;</c>.
/// <code>
/// genericArgs := '&lt;' ( typeArg ( ',' typeArg )* )? '&gt;'
/// typeArg     := type ( ':' type ( ',' type )* )?
/// </code>
/// <para>
/// An argument is a full <c>type</c>. It used to be a bare <c>Identifier</c>, which meant a nested
/// argument was a hard parse error: <c>Array&lt;User&gt;</c> read as the identifier <c>Array</c>
/// followed by an unexpected <c>&lt;</c>, so the list never found its <c>&gt;</c> and the whole
/// declaration died. One level of generics worked, two never did. Modifiers on an argument
/// (<c>Map&lt;string, User?&gt;</c>) were unreachable for the same reason, and so was an inline
/// anonymous type.
/// </para>
/// <para>
/// Arity is not checked here. <c>Map&lt;&gt;</c>, <c>Map&lt;string&gt;</c> and
/// <c>Map&lt;a, b, c&gt;</c> all parse and are all representable, because "wrong number of type
/// arguments" is a diagnostic the compiler can point at the argument list, whereas a parse failure
/// would abort the enclosing declaration.
/// </para>
/// </summary>
public partial class IonParser
{
    /// <summary>
    /// The generic argument list at the top of the type chain. Prefer
    /// <see cref="GenericArgumentListOf"/> inside the grammar: this one re-enters
    /// <see cref="Type"/> and so must never be reachable while the chain is still being built.
    /// </summary>
    public static Parser<char, IReadOnlyList<IonTypeParameterSyntax>> GenericParameterList =>
        GenericArgumentListOf(Type);

    /// <inheritdoc cref="GenericParameterList"/>
    public static Parser<char, IonTypeParameterSyntax> TypeParameterSyntax => TypeArgumentOf(Type);

    /// <param name="type">
    /// The parser for one argument — one level further down the chain than the list itself, which is
    /// what bounds <c>A&lt;A&lt;A&lt;…&gt;&gt;&gt;</c> to <see cref="MaxTypeNestingDepth"/>.
    /// </param>
    private static Parser<char, IReadOnlyList<IonTypeParameterSyntax>> GenericArgumentListOf(
        Parser<char, IonUnderlyingTypeSyntax> type) =>
        Char('<')
            .Before(SkipTrivia)
            .Then(TypeArgumentOf(type).Separated(Char(',').Then(SkipTrivia)))
            .Before(SkipTriviaAll)
            .Before(Char('>').Labelled("closing '>' of a generic argument list").Before(SkipTrivia))
            .Select(x => x.ToList()).OfType<IReadOnlyList<IonTypeParameterSyntax>>();

    /// <summary>
    /// One argument, plus the vestigial <c>: constraint, constraint</c> tail.
    /// </summary>
    /// <remarks>
    /// The constraint tail is parsed and discarded, exactly as it always was. Constraints belong on
    /// a generic <em>declaration</em>, which the grammar does not have yet; the syntax is kept only
    /// so that sources written against the old parser keep parsing. It is greedy — in
    /// <c>Map&lt;K: A, B&gt;</c> the <c>B</c> is read as a second constraint rather than a second
    /// argument — which is also unchanged, and is why the tail must not be grown.
    /// </remarks>
    private static Parser<char, IonTypeParameterSyntax> TypeArgumentOf(
        Parser<char, IonUnderlyingTypeSyntax> type) =>
        from startPos in CurrentPos
        from argument in type
        from _ in Char(':')
            .Then(SkipTrivia)
            .Then(type.Separated(Char(',').Then(SkipTrivia)))
            .Optional()
        from endPos in CurrentPos
        select new IonTypeParameterSyntax(argument.Name, argument).WithPos(startPos, endPos);
}
