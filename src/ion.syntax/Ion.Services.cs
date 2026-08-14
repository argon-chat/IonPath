namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, IonMethodSyntax> ServiceMethodCore =>
        Map(
            (pos, modifiers, name, parameters, returnType, _) =>
                new IonMethodSyntax(name, modifiers.ToList(), parameters.ToList(), returnType.GetValueOrDefault())
                    .WithPos(pos),
            CurrentPos.Labelled("currentPos"),
            MethodModifiers.Labelled("mod"),
            Identifier.Labelled("identifier"),
            ArgList.Labelled("args"),
            Char(':').Before(SkipTrivia).Then(Type.Labelled("returnType")).Optional(),
            Char(';').Or(Char(',')).Before(SkipTrivia)
        );

    public static Parser<char, IonMethodSyntax> ServiceMethod => WithLeading(ServiceMethodCore);

    private static Parser<char, IonMethodModifiers> MethodModifierOne =>
        OneOf(
            String("stream").ThenReturn(IonMethodModifiers.Stream),
            String("unary").ThenReturn(IonMethodModifiers.Unary),
            String("internal").ThenReturn(IonMethodModifiers.Internal)
        ).Before(SkipTrivia);

    private static Parser<char, IEnumerable<IonMethodModifiers>> MethodModifiers =>
        Try(MethodModifierOne).Many();

    private static Parser<char, IonServiceSyntax> ServiceCore =>
        Map(
            (pos, name, parameters, methods, endPos) =>
                new IonServiceSyntax(name, parameters.Value.ToList(), methods.ToList()).WithPos(pos, endPos),
            CurrentPos,
            String("service").Before(SkipTrivia).Then(Identifier),
            ArgList.Optional().Assert(maybe => maybe.HasValue, "Argument list required"),
            ServiceMethod.Many().Between(Char('{').Before(SkipTrivia), SkipTriviaAll.Then(Char('}'))),
            CurrentPos
        );

    public static Parser<char, IonServiceSyntax> Service => WithLeading(ServiceCore);
}
