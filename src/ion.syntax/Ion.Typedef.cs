namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    public static Parser<char, IonTypedef> Typedef =>
        Map((doc, attrs, pos, name, baseType, _) =>
                new IonTypedef(name, baseType.GetValueOrDefault()).WithAttributes(attrs).WithComments(doc).WithPos(pos),
            DocComment,
            Attribute.Many(),
            CurrentPos,
            String("typedef").Before(SkipWhitespaces).Then(Type),
            Char('=').Then(SkipWhitespaces).Then(Type).Optional(),
            Char('{').Then(AnyCharExcept('}').Many()).Before(Char('}')).Then(SkipWhitespaces).Then(Char(';').Optional())
        );
}