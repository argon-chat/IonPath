namespace ion.syntax;


using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static readonly Parser<char, Unit> UnionKeyword =
        String("union").Before(SkipWhitespaces).Then(Return(Unit.Value));

    public static Parser<char, IonUnionSyntax> Union =>
        Map(IonUnionSyntax
                (doc, attrs, pos, name, baseFields, cases) =>
                new IonUnionSyntax(name, baseFields.GetValueOrDefault([]).ToList(), cases.ToList())
                    .WithComments(doc)
                    .WithAttributes(attrs)
                    .WithPos(pos),
            LeadingDoc,
            Attributes,
            CurrentPos,
            UnionKeyword.Then(Identifier),
            ArgList.Labelled("args").Optional(),
            UnionCase.Separated(Char(',').Before(SkipWhitespaces)).Between(Char('{').Before(SkipWhitespaces), Char('}'))
        );

    public static Parser<char, IonUnionTypeCaseSyntax> UnionCase =>
        Map(
            (doc, attrs, pos, typeName, parameters) =>
                new IonUnionTypeCaseSyntax(typeName, parameters.GetValueOrDefault([]).ToList(), !parameters.HasValue)
                    .WithAttributes(attrs.ToList())
                    .WithComments(doc)
                    .WithPos(pos),
            LeadingDoc.Labelled("doc"),
            Attributes.Labelled("attributes"),
            CurrentPos.Labelled("currentPos"),
            Type.Labelled("identifier"),
            ArgList.Labelled("args").Optional()
        );
}