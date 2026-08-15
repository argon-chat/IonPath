namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

/// <summary>
/// Typedef layer of the Ion grammar.
/// <code>
/// typedef UserId = u4;        the documented form
/// typedef UserId = u4 {}      legacy form; the block is vestigial and ignored
/// typedef UserId = u4 {};     legacy form with a redundant terminator
/// </code>
/// <para>
/// A typedef is a transparent compile-time alias: the compiler erases it and every use site
/// carries the underlying type. See <c>ion.compiler.RestoreUnresolvedTypeStage</c>.
/// </para>
/// </summary>
public partial class IonParser
{
    /// <summary>
    /// The legacy <c>{ ... }</c> block.
    /// <para>
    /// It has never been documented and its contents have never been read — the original grammar
    /// parsed the span and threw it away. It stays accepted purely so that existing sources keep
    /// compiling; treat it as vestigial and do not grow it. The body cannot contain <c>}</c>,
    /// which matches the original behaviour exactly.
    /// </para>
    /// </summary>
    private static Parser<char, Unit> TypedefVestigialBlock =>
        Char('{')
            .Then(AnyCharExcept('}').SkipMany())
            .Before(Char('}'))
            .Then(SkipTrivia)
            .Then(Char(';').Optional())
            .ThenReturn(Unit.Value);

    /// <summary>
    /// How a typedef ends: either the vestigial block (with an optional <c>;</c>) or a
    /// mandatory <c>;</c>. Requiring the terminator in the block-less form keeps
    /// <c>typedef A = u4</c> from silently swallowing whatever follows it.
    /// </summary>
    private static Parser<char, Unit> TypedefTerminator =>
        OneOf(
            TypedefVestigialBlock,
            Char(';').Then(SkipTrivia));

    private static Parser<char, IonTypedefSyntax> TypedefCore =>
        Map((pos, name, baseType, _, endPos) =>
                new IonTypedefSyntax(name, baseType.GetValueOrDefault()).WithPos(pos, endPos),
            CurrentPos,
            // The name is parsed with the full Type parser rather than Identifier so that the
            // meaningless forms (`typedef Foo?`, `typedef Foo[]`, `typedef Foo<T>`) reach the
            // compiler and get a proper diagnostic instead of a bare parse error.
            Keyword("typedef").Then(Type),
            Char('=').Then(SkipTrivia).Then(Type).Optional(),
            // SkipTriviaAll rather than SkipTrivia: SkipTrivia deliberately stops in front of
            // `///` and `/** */` so a doc comment is never eaten as trailing trivia, which would
            // make `typedef Foo = Bar /** x */ {}` fail at the '{'. There is nothing after a
            // typedef for such a comment to document, so it is consumed and dropped here.
            // This sits *before* the terminator, so a doc comment written after the ';' still
            // attaches to the next declaration.
            SkipTriviaAll.Then(TypedefTerminator),
            CurrentPos
        );

    public static Parser<char, IonTypedefSyntax> Typedef => WithLeading(TypedefCore);
}
