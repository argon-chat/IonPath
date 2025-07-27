namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, IonArgument> ArgEntry =>
        Map(
            (doc, attr, pos, name, _, type) => new IonArgument(name, type)
                .WithPos(pos)
                .WithAttributes(attr)
                .WithComments(doc),
            DocComment,
            Attributes,
            CurrentPos,
            Identifier.Before(SkipWhitespaces),
            Char(':').Before(SkipWhitespaces),
            Type
        );

    private static Parser<char, IEnumerable<IonArgument>> ArgList =>
        ArgEntry
            .Separated(Char(',')
            .Before(SkipWhitespaces))
            .Between(Char('(').Before(SkipWhitespaces), Char(')')
            .Before(SkipWhitespaces));
}