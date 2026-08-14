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

    private static Parser<char, IonArgumentModifiers> ArgumentModifierOne =>
        Try(String("stream").ThenReturn(IonArgumentModifiers.Stream))
            .Before(SkipTrivia);

    private static Parser<char, IEnumerable<IonArgumentSyntax>> ArgList =>
        ArgEntry
            .Separated(Char(',')
            .Before(SkipTrivia))
            .Between(Char('(').Before(SkipTrivia), SkipTriviaAll.Then(Char(')'))
            .Before(SkipTrivia));
}
