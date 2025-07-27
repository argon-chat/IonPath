namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, string> DocComment =>
        Try(String("//")
                .Then(SkipWhitespaces)
                .Then(AnyCharExcept('\r', '\n').ManyString())
                .Before(SkipWhitespaces))
            .Labelled("doc-comment");

    private static Parser<char, string?> LeadingDoc =>
        DocComment.Many()
            .Select(lines => lines.Any() ? string.Join("\n", lines) : null);
}