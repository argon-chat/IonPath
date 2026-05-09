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

    private static Parser<char, (IEnumerable<string> Types, string Module, SourcePos ModStart, SourcePos ModEnd)> ImportBody =>
        String("#import")
            .Before(SkipWhitespaces)
            .Then(
                Map((types, _, modStart, module, modEnd) => (Types: types, Module: module, ModStart: modStart, ModEnd: modEnd),
                    ImportTypeList.Before(SkipWhitespaces),
                    String("from").Before(SkipWhitespaces),
                    CurrentPos,
                    StringLiteral,
                    CurrentPos
                )
            )
            .Before(SkipWhitespaces)
            .Before(Try(Char(';')).Optional());

    private static Parser<char, IonSyntaxMember> ImportDirective =>
        Map(IonSyntaxMember
            (doc, pos, body, endPos) =>
            {
                var import = new IonImportSyntax(body.Types.ToList(), body.Module);
                import.ModuleNameStart = body.ModStart;
                import.ModuleNameEnd = body.ModEnd;
                return import.WithPos(pos, endPos).WithComments(doc);
            },
            DocComment.Optional(),
            CurrentPos,
            Try(ImportBody),
            CurrentPos
        );

    private static Parser<char, IEnumerable<string>> ImportTypeList =>
        Char('{')
            .Before(SkipWhitespaces)
            .Then(
                ImportIdentifier.Before(SkipWhitespaces)
                    .Separated(Char(',').Before(SkipWhitespaces))
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