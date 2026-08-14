namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, IonSyntaxMember> UseDirectiveCore =>
        Map(IonSyntaxMember
            (pos, path) => new IonUseSyntax(path).WithPos(pos),
            CurrentPos,
            Try(
                String("#use")
                    .Before(SkipTrivia)
                    .Then(StringLiteral)
                    .Before(SkipTrivia)
            )
        );

    public static Parser<char, IonSyntaxMember> UseDirective => WithLeading(UseDirectiveCore);

    private static Parser<char, (IEnumerable<string> Types, string Module, SourcePos ModStart, SourcePos ModEnd)> ImportBody =>
        String("#import")
            .Before(SkipTrivia)
            .Then(
                Map((types, _, modStart, module, modEnd) => (Types: types, Module: module, ModStart: modStart, ModEnd: modEnd),
                    ImportTypeList.Before(SkipTrivia),
                    String("from").Before(SkipTrivia),
                    CurrentPos,
                    StringLiteral,
                    CurrentPos
                )
            )
            .Before(SkipTrivia)
            .Before(Try(Char(';')).Optional());

    private static Parser<char, IonSyntaxMember> ImportDirectiveCore =>
        Map(IonSyntaxMember
            (pos, body, endPos) =>
            {
                var import = new IonImportSyntax(body.Types.ToList(), body.Module);
                import.ModuleNameStart = body.ModStart;
                import.ModuleNameEnd = body.ModEnd;
                return import.WithPos(pos, endPos);
            },
            CurrentPos,
            Try(ImportBody),
            CurrentPos
        );

    public static Parser<char, IonSyntaxMember> ImportDirective => WithLeading(ImportDirectiveCore);

    private static Parser<char, IEnumerable<string>> ImportTypeList =>
        Char('{')
            .Before(SkipTrivia)
            .Then(
                ImportIdentifier.Before(SkipTrivia)
                    .Separated(Char(',').Before(SkipTrivia))
            )
            .Before(SkipTriviaAll)
            .Before(Char('}'));

    private static Parser<char, string> ImportIdentifier =>
        Token(c => char.IsLetterOrDigit(c) || c == '_')
            .AtLeastOnceString();

    /// <summary>
    /// A double quoted string literal. Deliberately atomic: the trivia skipper is never entered
    /// from inside a literal, so <c>//</c> and <c>*/</c> inside a string are plain characters.
    /// </summary>
    private static Parser<char, string> StringLiteral =>
        Char('"').Then(AnyCharExcept('"').ManyString()).Before(Char('"'));

    private static Parser<char, IonSyntaxMember> FeatureDirectiveCore =>
        Map(IonSyntaxMember
                (pos, path) => new IonFeatureSyntax(path).WithPos(pos),
            CurrentPos,
            Try(
                String("#feature")
                    .Before(SkipTrivia)
                    .Then(StringLiteral)
                    .Before(SkipTrivia)
            )
        );

    public static Parser<char, IonSyntaxMember> FeatureDirective => WithLeading(FeatureDirectiveCore);
}
