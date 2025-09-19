namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    public static Parser<char, IonMethodSyntax> ServiceMethod =>
        Map(
            (doc, attrs, pos, modifiers, name, parameters, returnType, _) =>
                new IonMethodSyntax(name, modifiers.ToList(), parameters.ToList(), returnType.GetValueOrDefault())
                    .WithAttributes(attrs.ToList())
                    .WithComments(doc)
                    .WithPos(pos),
            LeadingDoc.Labelled("doc"),
            Attributes.Labelled("attributes"),
            CurrentPos.Labelled("currentPos"),
            MethodModifiers.Labelled("mod"),
            Identifier.Labelled("identifier"),
            ArgList.Labelled("args"),
            Char(':').Before(SkipWhitespaces).Then(Type.Labelled("returnType")).Optional(),
            Char(';').Or(Char(',')).Before(SkipWhitespaces)
        );

    private static Parser<char, IonMethodModifiers> MethodModifierOne =>
        OneOf(
            String("stream").ThenReturn(IonMethodModifiers.Stream),
            String("unary").ThenReturn(IonMethodModifiers.Unary),
            String("internal").ThenReturn(IonMethodModifiers.Internal)
        ).Before(SkipWhitespaces);

    private static Parser<char, IEnumerable<IonMethodModifiers>> MethodModifiers =>
        MethodModifierOne.Many();

    public static Parser<char, IonServiceSyntax> Service =>
        Map(
            (doc, attrs, pos, name, parameters, methods) =>
                new IonServiceSyntax(name, parameters.Value.ToList(), methods.ToList()).WithAttributes(attrs.ToList()).WithPos(pos).WithComments(doc),
            LeadingDoc,
            Attributes,
            CurrentPos,
            String("service").Before(SkipWhitespaces).Then(Identifier),
            ArgList.Optional().Assert(maybe => maybe.HasValue, "Argument list required"),
            ServiceMethod.Many().Between(Char('{').Before(SkipWhitespaces), Char('}').Before(SkipWhitespaces))
        );
}