namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, IonArgumentSyntax> ArgEntry =>
        Map(
            (lead, pos, mod, name, type) => new IonArgumentSyntax(name, type, mod.GetValueOrDefault())
                .WithPos(pos)
                .WithAttributes(lead.Attributes)
                .WithComments(lead.Doc),
            LeadingSection,
            CurrentPos,
            ArgumentModifierOne.Optional(),
            Identifier.Before(SkipTrivia),
            Char(':').Before(SkipTrivia).Then(Type).Before(SkipTrivia)
        );

    /// <summary>
    /// The <c>stream</c> parameter modifier, terminated by a word boundary.
    /// </summary>
    /// <remarks>
    /// See <c>MethodModifierOne</c>. Without <see cref="Keyword"/> this matched the first six
    /// characters of a parameter name: <c>Ok(streamValue: i4)</c> lowered to a parameter literally
    /// named <c>" Value"</c> (leading space included) of type <c>IAsyncEnumerable&lt;i4&gt;</c>.
    /// </remarks>
    private static Parser<char, IonArgumentModifiers> ArgumentModifierOne =>
        Keyword("stream").ThenReturn(IonArgumentModifiers.Stream);

    private static Parser<char, IEnumerable<IonArgumentSyntax>> ArgList =>
        ArgEntry
            .Separated(Char(',')
            .Before(SkipTrivia))
            .Between(Char('(').Before(SkipTrivia), SkipTriviaAll.Then(Char(')'))
            .Before(SkipTrivia));
}
