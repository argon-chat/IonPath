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
            Char('@').Then(Identifier).Before(SkipWhitespaces),
            Try(
                Char('(')
                    .Then(
                        AnyCharExcept(')')
                            .ManyString()
                            .Select(s => s.Split(',').Select(a => a.Trim()).ToList())
                    )
                    .Before(Char(')'))
            ).Optional().Select(opt => opt.HasValue ? opt.Value : [])
        ).Before(SkipWhitespaces);

    private static Parser<char, IEnumerable<IonAttributeSyntax>> Attributes =>
        Attribute.Many().Before(SkipWhitespaces);

    public static Parser<char, IonAttributeDefSyntax> AttributeDef =>
        Map(
            (doc, pos, _, name, args) => new IonAttributeDefSyntax(name, args.ToList()).WithPos(pos).WithComments(doc),
            DocComment.Optional(),
            CurrentPos,
            String("attribute").Before(SkipWhitespaces),
            Char('@').Then(Identifier).Before(SkipWhitespaces),
            ArgList.Before(Char(';'))
        );
}