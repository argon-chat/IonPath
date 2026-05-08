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

    private static Parser<char, IonSyntaxMember> ImportDirective =>
        Map(IonSyntaxMember
            (doc, pos, typeNames, moduleName) => new IonImportSyntax(typeNames.ToList(), moduleName).WithPos(pos).WithComments(doc),
            DocComment.Optional(),
            CurrentPos,
            Try(
                String("#import")
                    .Before(SkipWhitespaces)
                    .Then(ImportTypeList)
                    .Before(SkipWhitespaces)
            ),
            String("from")
                .Before(SkipWhitespaces)
                .Then(StringLiteral)
                .Before(SkipWhitespaces)
        );

    private static Parser<char, IEnumerable<string>> ImportTypeList =>
        Char('{')
            .Before(SkipWhitespaces)
            .Then(
                ImportIdentifier.Before(SkipWhitespaces)
                    .SeparatedAtLeastOnce(Char(',').Before(SkipWhitespaces))
            )
            .Before(SkipWhitespaces)
            .Before(Char('}'));

    private static Parser<char, string> ImportIdentifier =>
        Token(c => char.IsLetterOrDigit(c) || c == '_')
            .AtLeastOnceString();

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