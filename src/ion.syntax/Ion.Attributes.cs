namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, IonAttributeSyntax> Attribute =>
        Map(
            (pos, name, args) => new IonAttributeSyntax(name, args.Where(x => !string.IsNullOrEmpty(x)).ToList()).WithPos(pos),
            CurrentPos,
            Char('@').Then(Identifier).Before(SkipTrivia),
            Try(
                Char('(')
                    .Then(AttributeArgSpan.Select(SplitAttributeArgs))
                    .Before(Char(')'))
            ).Optional().Select(opt => opt.HasValue ? opt.Value : [])
        ).Before(SkipTrivia);

    /// <summary>
    /// The raw text between an attribute's parentheses. Comments and string literals are consumed
    /// whole, so a <c>)</c> that only occurs inside one — <c>@a(/* ) */ x)</c>, <c>@a("a)b")</c> —
    /// no longer terminates the argument list early.
    /// </summary>
    private static Parser<char, string> AttributeArgSpan =>
        OneOf(
            RawComment,
            RawStringLiteral,
            AnyCharExcept(')', '"', '/').AtLeastOnceString(),
            Char('/').ThenReturn("/")
        ).ManyString();

    /// <summary>
    /// Attribute arguments are still a raw, comma split character span (typed argument lexing is a
    /// separate work item — see roadmap 0.5). Comments are stripped first, and the split skips
    /// over string literals so that <c>@a(x /* y */, "a,b")</c> yields two arguments, not three.
    /// </summary>
    private static List<string> SplitAttributeArgs(string raw)
    {
        var span = StripComments(raw);
        var args = new List<string>();
        var start = 0;
        var inString = false;

        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] == '"')
                inString = !inString;
            else if (span[i] == ',' && !inString)
            {
                args.Add(span[start..i].Trim());
                start = i + 1;
            }
        }

        args.Add(span[start..].Trim());
        return args;
    }

    private static Parser<char, IonAttributeDefSyntax> AttributeDefCore =>
        Map(
            (pos, _, name, args) => new IonAttributeDefSyntax(name, args.ToList()).WithPos(pos),
            CurrentPos,
            String("attribute").Before(SkipTrivia),
            Char('@').Then(Identifier).Before(SkipTrivia),
            ArgList.Before(Char(';'))
        );

    public static Parser<char, IonAttributeDefSyntax> AttributeDef => WithLeading(AttributeDefCore);
}
