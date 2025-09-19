namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, string> MsgKeyword =>
        String("msg").Before(SkipWhitespaces);

    private static Parser<char, IonIdentifier> Identifier =>
        Map(
            (start, first, rest, end) => new IonIdentifier(first + new string(rest)).WithPos(start, end),
            CurrentPos,
            Letter.Or(Char('_')),
            LetterOrDigit.Or(Char('_')).ManyString(),
            CurrentPos
        ).Before(SkipWhitespaces);

    private static readonly Parser<char, IonUnderlyingTypeSyntax> Type =
        Map(
            (pos, name, generics, isOptional, isArray) => new IonUnderlyingTypeSyntax(name,
                    generics.GetValueOrDefault() ?? [], isArray.HasValue, isOptional.HasValue)
                .WithPos(pos),
            CurrentPos,
            Identifier.Before(SkipWhitespaces),
            GenericParameterList.Optional(),
            Char('?').Before(SkipWhitespaces).Optional(),
            Try(String("[]")).Before(SkipWhitespaces).Optional()
        );

    public static Parser<char, Maybe<Unit>> ForbidNext(char c, string message) =>
        Lookahead(
            Char(c).Then(Fail<Unit>(message))
        ).Optional();

    private static Parser<char, IonFieldSyntax> Field =>
        Map(
            (doc, attrs, pos, name, _, _, type, __) => new IonFieldSyntax(name, type)
                .WithComments(doc)
                .WithAttributes(attrs)
                .WithPos(pos),
            LeadingDoc,
            Attributes,
            CurrentPos,
            Identifier.Labelled("field name").Before(SkipWhitespaces),
            ForbidNext('?', "'?' is not allowed after field name"),
            Char(':').Labelled("':' after field name").Before(SkipWhitespaces),
            Type,
            Char(';').Before(SkipWhitespaces)
        );

    private static Parser<char, IEnumerable<IonFieldSyntax>> FieldList =>
        Field.ManyBetween(Char('{').Before(SkipWhitespaces), Char('}').Before(SkipWhitespaces));


    public static Parser<char, IonSyntaxMember> Message =>
        Map(IonSyntaxMember
                (doc, attrs, pos, msgName, fields) =>
                new IonMessageSyntax(msgName, fields.ToList()).WithComments(doc).WithAttributes(attrs).WithPos(pos),
            LeadingDoc,
            Attributes,
            CurrentPos,
            MsgKeyword.Then(Identifier),
            FieldList
        );
}