namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    private static Parser<char, string> MsgKeyword =>
        String("msg").Before(SkipTrivia);

    private static Parser<char, IonIdentifier> Identifier =>
        Map(
            (start, first, rest, end) => new IonIdentifier(first + new string(rest)).WithPos(start, end),
            CurrentPos,
            Letter.Or(Char('_')),
            LetterOrDigit.Or(Char('_')).ManyString(),
            CurrentPos
        ).Before(SkipTrivia);

    private static readonly Parser<char, IonUnderlyingTypeSyntax> Type =
        Map(
            (pos, name, generics, modifiers) =>
            {
                var isOptional = modifiers.Contains("?");
                var isArray = modifiers.Contains("[]");
                var isPartial = modifiers.Contains("~");

                return new IonUnderlyingTypeSyntax(name,
                        generics.GetValueOrDefault() ?? [], isArray, isOptional, isPartial)
                    .WithPos(pos);
            },
            CurrentPos,
            Identifier.Before(SkipTrivia),
            GenericParameterList.Optional(),
            ModifierOfType.Many().Select(m => m.ToArray())
        );

    private static Parser<char, string> ModifierOfType =>
        OneOf(
            Char('?').Select(_ => "?"),
            Try(String("[]")).Select(_ => "[]"),
            Char('~').Select(_ => "~")
        ).Before(SkipTrivia);

    public static Parser<char, Maybe<Unit>> ForbidNext(char c, string message) =>
        Lookahead(
            Char(c).Then(Fail<Unit>(message))
        ).Optional();

    private static Parser<char, IonFieldSyntax> Field =>
        Map(
            (lead, pos, name, _, _, type, __) => new IonFieldSyntax(name, type)
                .WithComments(lead.Doc)
                .WithAttributes(lead.Attributes)
                .WithPos(pos),
            LeadingSection,
            CurrentPos,
            Identifier.Labelled("field name").Before(SkipTrivia),
            ForbidNext('?', "'?' is not allowed after field name"),
            Char(':').Labelled("':' after field name").Before(SkipTrivia),
            Type,
            Char(';').Before(SkipTrivia)
        );

    private static Parser<char, IEnumerable<IonFieldSyntax>> FieldList =>
        Field.ManyBetween(Char('{').Before(SkipTrivia), SkipTriviaAll.Then(Char('}')));


    private static Parser<char, IonSyntaxMember> MessageCore =>
        Map(IonSyntaxMember
                (pos, msgName, fields, endPos) =>
                new IonMessageSyntax(msgName, fields.ToList()).WithPos(pos, endPos),
            CurrentPos,
            MsgKeyword.Then(Identifier),
            FieldList,
            CurrentPos
        );

    public static Parser<char, IonSyntaxMember> Message => WithLeading(MessageCore);
}
