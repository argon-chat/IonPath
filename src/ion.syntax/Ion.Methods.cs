namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, IonArgumentSyntax> ArgEntry =>
        Map(
            (doc, attr, pos, mod, name, type) => new IonArgumentSyntax(name, type, mod.GetValueOrDefault())
                .WithPos(pos)
                .WithAttributes(attr)
                .WithComments(doc),
            LeadingDoc,
            Attributes,
            CurrentPos,
            ArgumentModifierOne.Optional(),
            Identifier.Before(SkipWhitespaces),
            Char(':').Before(SkipWhitespaces).Then(Type).Before(SkipWhitespaces)
        );

    private static Parser<char, IonArgumentModifiers> ArgumentModifierOne =>
        Try(String("stream").ThenReturn(IonArgumentModifiers.Stream))
            .Before(SkipWhitespaces);

    private static Parser<char, IEnumerable<IonArgumentSyntax>> ArgList =>
        ArgEntry
            .Separated(Char(',')
            .Before(SkipWhitespaces))
            .Between(Char('(').Before(SkipWhitespaces), Char(')')
            .Before(SkipWhitespaces));
}