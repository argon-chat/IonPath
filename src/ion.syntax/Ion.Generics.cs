namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    public static Parser<char, IReadOnlyList<IonTypeParameterSyntax>> GenericParameterList =>
        Char('<')
            .Before(SkipTrivia)
            .Then(TypeParameterSyntax.Separated(Char(',').Then(SkipTrivia)))
            .Before(SkipTriviaAll)
            .Before(Char('>').Before(SkipTrivia))
            .Select(x => x.ToList()).OfType<IReadOnlyList<IonTypeParameterSyntax>>();

    public static Parser<char, IonTypeParameterSyntax> TypeParameterSyntax =>
        from startPos in CurrentPos
        from name in Identifier.Before(SkipTrivia)
        from constraints in Char(':')
            .Then(SkipTrivia)
            .Then(Type.Separated(Char(',').Then(SkipTrivia)))
            .Optional()
        from endPos in CurrentPos
        select new IonTypeParameterSyntax(name).WithPos(startPos, endPos);
}
