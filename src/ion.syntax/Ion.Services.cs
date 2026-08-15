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

    /// <summary>
    /// One method modifier keyword, terminated by a word boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="Keyword"/>, not a bare <c>String</c>. <c>String("internal")</c> happily matches the
    /// first eight characters of <c>internalThing</c>, so <c>internalThing(): i4;</c> parsed as an
    /// <c>internal</c>-modified method called <c>Thing</c> — a wire-visible rename, emitted into the
    /// generated interface and the server dispatcher, with no diagnostic anywhere. The same held for
    /// <c>stream…</c> and <c>unary…</c>. <see cref="Keyword"/> also supplies the <c>Try</c>, so a
    /// rejected match consumes nothing and the identifier parser downstream still sees the full name.
    /// </remarks>
    private static Parser<char, IonMethodModifiers> MethodModifierOne =>
        OneOf(
            Keyword("stream").ThenReturn(IonMethodModifiers.Stream),
            Keyword("unary").ThenReturn(IonMethodModifiers.Unary),
            Keyword("internal").ThenReturn(IonMethodModifiers.Internal)
        );

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
