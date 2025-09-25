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
            (lhs, op, rhs) =>
            {
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

    private static Parser<char, IonFlagEntrySyntax> FlagEntry =>
        Map(
            (pos, name, exprOpt) => new IonFlagEntrySyntax(name, exprOpt).WithPos(pos),
            CurrentPos,
            Identifier.Before(SkipWhitespaces),
            Try(
                Char('=')
                    .Before(SkipWhitespaces)
                    .Then(Expression)
                    .Before(SkipWhitespaces)
            ).Optional()
        );

    private static Parser<char, IonExpression> Expression =>
        Map((startPos, exp, endPos) => new IonExpression(exp).WithPos(startPos, endPos),
            CurrentPos,
            AnyCharExcept(',', '}').AtLeastOnceString(),
            CurrentPos
        );
    public static Parser<char, IonSyntaxMember> Flags =>
        EnumLike("flags", (identifier, syntax, members) => new IonFlagsSyntax(identifier, syntax, members.ToList()));

    public static Parser<char, IonSyntaxMember> Enums =>
        EnumLike("enum", (identifier, syntax, members) => new IonEnumSyntax(identifier, syntax, members.ToList()));


    public static Parser<char, IonSyntaxMember> EnumLike(string keyword, Func<IonIdentifier, IonUnderlyingTypeSyntax, IEnumerable<IonFlagEntrySyntax>, IonSyntaxMember> ctor) =>
        Map(IonSyntaxMember (pos, doc, attrs, name, baseType, entries) =>
                ctor(name, baseType.HasValue
                        ? baseType.Value
                        : new IonUnderlyingTypeSyntax(new IonIdentifier("u4"), [], false, false, false), entries)
                    .WithComments(doc)
                    .WithAttributes(attrs)
                    .WithPos(pos),
            CurrentPos,
            LeadingDoc,
            Attributes,
            String(keyword).Before(SkipWhitespaces).Then(Identifier),
            Try(Char(':').Before(SkipWhitespaces).Then(Type)).Optional(),
            FlagEntry
                .Separated(Char(',').Before(SkipWhitespaces))
                .Between(Char('{').Before(SkipWhitespaces), Char('}').Before(SkipWhitespaces))
        );


    
}