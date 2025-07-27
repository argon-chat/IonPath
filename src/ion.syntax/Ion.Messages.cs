namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, string> MsgKeyword =>
        String("msg").Before(SkipWhitespaces);

    private static Parser<char, string> Identifier =>
        Map(
            (first, rest) => first + new string(rest),
            Letter,
            LetterOrDigit.ManyString()
        ).Before(SkipWhitespaces);

    private static readonly Parser<char, IonUnderlyingType> Type =
        Map(
            (pos, name, isOptional, isArray) =>
                new IonUnderlyingType(name, isOptional.HasValue, isArray.HasValue).WithPos(pos),
            CurrentPos,
            Identifier.Before(SkipWhitespaces),
            Char('?').Before(SkipWhitespaces).Optional(),
            Try(String("[]")).Before(SkipWhitespaces).Optional()
        );


    private static Parser<char, IonField> Field =>
        Map(
            (doc, attrs, pos, name, _, type, __) => new IonField(name, type)
                .WithComments(doc)
                .WithAttributes(attrs)
                .WithPos(pos),
            LeadingDoc,
            Attributes,
            CurrentPos,
            Identifier.Before(SkipWhitespaces),
            Char(':').Before(SkipWhitespaces),
            Type,
            Char(';').Before(SkipWhitespaces)
        );

    private static Parser<char, IEnumerable<IonField>> FieldList =>
        Field.ManyBetween(Char('{').Before(SkipWhitespaces), Char('}').Before(SkipWhitespaces));


    public static Parser<char, IonDefinition> Message =>
        Map(IonDefinition
            (doc, attrs, pos, msgName, fields) =>
                new IonMessage(msgName, fields.ToList()).WithComments(doc).WithAttributes(attrs).WithPos(pos),
            LeadingDoc,
            Attributes,
            CurrentPos,
            MsgKeyword.Then(Identifier),
            FieldList
        );
}