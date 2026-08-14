namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, IonTypedefSyntax> TypedefCore =>
        Map((pos, name, baseType, _) =>
                new IonTypedefSyntax(name, baseType.GetValueOrDefault()).WithPos(pos),
            CurrentPos,
            String("typedef").Before(SkipTrivia).Then(Type),
            Char('=').Then(SkipTrivia).Then(Type).Optional(),
            Char('{').Then(AnyCharExcept('}').Many()).Before(Char('}')).Then(SkipTrivia).Then(Char(';').Optional())
        );

    public static Parser<char, IonTypedefSyntax> Typedef => WithLeading(TypedefCore);
}
