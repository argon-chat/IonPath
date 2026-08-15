namespace ion.syntax;


using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, Unit> UnionKeyword => Keyword("union");

    private static Parser<char, IonUnionSyntax> UnionCore =>
        Map(IonUnionSyntax
                (pos, name, baseFields, cases, endPos) =>
                new IonUnionSyntax(name, baseFields.GetValueOrDefault([]).ToList(), cases.ToList())
                    .WithPos(pos, endPos),
            CurrentPos,
            UnionKeyword.Then(Identifier),
            ArgList.Labelled("args").Optional(),
            UnionCase
                .Separated(Char(',').Before(SkipTrivia))
                .Between(Char('{').Before(SkipTrivia), SkipTriviaAll.Then(Char('}'))),
            CurrentPos
        );

    public static Parser<char, IonUnionSyntax> Union => WithLeading(UnionCore);

    private static Parser<char, IonUnionTypeCaseSyntax> UnionCaseCore =>
        Map(
            (pos, typeName, parameters) =>
                new IonUnionTypeCaseSyntax(typeName, parameters.GetValueOrDefault([]).ToList(), !parameters.HasValue)
                    .WithPos(pos),
            CurrentPos.Labelled("currentPos"),
            Type.Labelled("identifier"),
            ArgList.Labelled("args").Optional()
        );

    public static Parser<char, IonUnionTypeCaseSyntax> UnionCase => WithLeading(UnionCaseCore);
}
