namespace ion.syntax;


using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, Unit> UnionKeyword => Keyword("union");

    /// <remarks>
    /// As with a service, the base argument list is not a recovery point — only the case list is.
    /// A case is a type reference plus an optional argument list, so like an enum entry it usually
    /// parses and it is the <c>,</c> after it that is missing: <c>InvalidCase without args or
    /// parens,</c> reads as the case <c>InvalidCase</c> plus an unreadable span.
    /// </remarks>
    private static Parser<char, IonUnionSyntax> UnionCore(bool recover) =>
        Map(IonUnionSyntax
                (pos, name, baseFields, cases, endPos) =>
                new IonUnionSyntax(name, baseFields.GetValueOrDefault([]).ToList(), cases.Members)
                    .WithPos(pos, endPos)
                    .WithInvalidMembers(cases.Invalid),
            CurrentPos,
            UnionKeyword.Then(Identifier),
            ArgList.Labelled("args").Optional(),
            Braced(SeparatedMembers(UnionCase, ',', recover)),
            CurrentPos
        );

    public static Parser<char, IonUnionSyntax> Union => WithLeading(UnionCore(recover: true));

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
