namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, Unit> BlockComment =>
        Try(String("/*"))
            .Then(Any.ManyString().Before(Try(String("*/"))))
            .Select(_ => Unit.Value)
            .Labelled("block-comment");

    private static Parser<char, Unit> SkipTrivia =>
        SkipWhitespaces.Then(
            BlockComment.Then(SkipWhitespaces).SkipMany());

    private static Parser<char, string> DocComment =>
        Try(String("//")
                .Then(SkipWhitespaces)
                .Then(AnyCharExcept('\r', '\n').ManyString())
                .Before(SkipWhitespaces))
            .Labelled("doc-comment");

    private static Parser<char, string?> LeadingDoc =>
        BlockComment.Before(SkipWhitespaces).SkipMany()
            .Then(DocComment.Many())
            .Select(lines => lines.Any() ? string.Join("\n", lines) : null);
}