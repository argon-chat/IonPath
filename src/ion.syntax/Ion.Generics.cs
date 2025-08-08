namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    public static Parser<char, IReadOnlyList<IonTypeParameterSyntax>> GenericParameterList =>
        Char('<')
            .Then(TypeParameterSyntax.Separated(Char(',').Then(SkipWhitespaces)))
            .Before(Char('>')).Select(x => x.ToList()).OfType<IReadOnlyList<IonTypeParameterSyntax>>();

    public static Parser<char, IonTypeParameterSyntax> TypeParameterSyntax =>
        from startPos in CurrentPos
        from name in Identifier.Before(SkipWhitespaces)
        from constraints in Char(':')
            .Then(SkipWhitespaces)
            .Then(Type.Separated(Char(',').Then(SkipWhitespaces)))
            .Optional()
        from endPos in CurrentPos
        select new IonTypeParameterSyntax(name).WithPos(startPos, endPos);
}