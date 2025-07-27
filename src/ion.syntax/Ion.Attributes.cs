namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, IonAttribute> Attribute =>
        Map(
            (pos, name, args) => new IonAttribute(name, args).WithPos(pos),
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
        );

    private static Parser<char, IEnumerable<IonAttribute>> Attributes =>
        Attribute.Many().Before(SkipWhitespaces);
}