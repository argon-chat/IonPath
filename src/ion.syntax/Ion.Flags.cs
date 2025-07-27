namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    public static Parser<char, int> Integer =>
        Digit.AtLeastOnceString()
            .Select(int.Parse)
            .Before(SkipWhitespaces);

    public static Parser<char, int> IntExpression =>
        Map(
            (lhs, op, rhs) => {
                return op switch
                {
                    "<<" => lhs << rhs,
                    _ => throw new Exception($"Unknown op {op}")
                };
            },
            Integer,
            Try(String("<<").Before(SkipWhitespaces)),
            Integer
        ).Or(Integer);

    private static Parser<char, IonFlagEntry> FlagEntry =>
        Map(
            (pos, name, expr) => new IonFlagEntry(name, expr.Trim()).WithPos(pos),
            CurrentPos,
            Identifier.Before(Char('=').Before(SkipWhitespaces)),
            AnyCharExcept(',', '}')
                .AtLeastOnceString()
                .Before(SkipWhitespaces)
        );

    public static Parser<char, IonDefinition> Flags =>
        Map(IonDefinition (pos, doc, attrs, name, baseType, entries) =>
                new IonFlags(name, baseType, entries.ToList()).WithComments(doc).WithAttributes(attrs).WithPos(pos),
            CurrentPos,
            LeadingDoc,
            Attributes,
            String("flags").Before(SkipWhitespaces).Then(Identifier),
            Char(':').Before(SkipWhitespaces).Then(Type),
            FlagEntry
                .Separated(Char(',').Before(SkipWhitespaces))
                .Between(Char('{').Before(SkipWhitespaces), Char('}').Before(SkipWhitespaces))
        );

    public static Parser<char, IonFlags> Enums =>
        Map(
            (pos, name, baseType, entries) => new IonFlags(name, baseType, entries.ToList()).WithPos(pos),
            CurrentPos,
            String("enum").Before(SkipWhitespaces).Then(Identifier),
            Char(':').Before(SkipWhitespaces).Then(Type),
            FlagEntry
                .Separated(Char(',').Before(SkipWhitespaces))
                .Between(Char('{').Before(SkipWhitespaces), Char('}').Before(SkipWhitespaces))
        );
}