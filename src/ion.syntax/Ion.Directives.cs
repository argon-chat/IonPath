namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, IonSyntaxMember> UseDirective =>
        Map(IonSyntaxMember
            (doc, pos, path) => new IonUseSyntax(path).WithPos(pos).WithComments(doc),
            DocComment.Optional(),
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

    private static Parser<char, IonSyntaxMember> FeatureDirective =>
        Map(IonSyntaxMember
                (doc, pos, path) => new IonFeatureSyntax(path).WithPos(pos).WithComments(doc),
            DocComment.Optional(),
            CurrentPos,
            Try(
                String("#feature")
                    .Before(SkipWhitespaces)
                    .Then(StringLiteral)
                    .Before(SkipWhitespaces)
            )
        );
}