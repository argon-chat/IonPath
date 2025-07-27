namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, IonDefinition> UseDirective =>
        Map(IonDefinition
            (doc, pos, path) => new IonUse(path).WithPos(pos).WithComments(doc),
            DocComment,
            CurrentPos,
            Try(
                String("#use")
                    .Before(SkipWhitespaces)
                    .Then(StringLiteral)
                    .Before(SkipWhitespaces)
            )
        );

    private static Parser<char, string> StringLiteral =>
        Char('"').Then(AnyCharExcept('"').ManyString()).Before(Char('"'));
}