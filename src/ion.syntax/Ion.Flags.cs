namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    public static Parser<char, int> Integer =>
        Digit.AtLeastOnceString()
            .Select(int.Parse)
            .Before(SkipTrivia);

    // `IntExpression` — a `<<` folding parser — lived here and was referenced by nothing. The
    // grammar never used it: an enum/flags member value is captured verbatim by `Expression` below
    // and folded later by `TransformStage.EvaluateConstantExpression`, which is also the only place
    // that can report ION0007 instead of throwing. The dead copy could only ever drift from it, and
    // it threw a bare `Exception` on an operator its own `Try` had already guaranteed.

    private static Parser<char, IonFlagEntrySyntax> FlagEntry =>
        Map(
            (lead, pos, name, exprOpt) => new IonFlagEntrySyntax(name, exprOpt)
                .WithComments(lead.Doc)
                .WithAttributes(lead.Attributes)
                .WithPos(pos),
            LeadingSection,
            CurrentPos,
            Identifier.Before(SkipTrivia),
            Try(
                Char('=')
                    .Before(SkipTrivia)
                    .Then(Expression)
                    .Before(SkipTrivia)
            ).Optional()
        );

    /// <summary>A run of value characters. Stops at <c>,</c>, <c>}</c>, end of line and at <c>/</c>.</summary>
    private static Parser<char, string> ExpressionChunk =>
        AnyCharExcept(',', '}', '\r', '\n', '/').AtLeastOnceString();

    /// <summary>A block comment embedded in a value, e.g. <c>A = 1 /*bit 0*/ &lt;&lt; 1</c>. Collapses to a space.</summary>
    private static Parser<char, string> ExpressionBlockComment =>
        Try(CommentTrivia.Assert(t => t.Kind is IonTriviaKind.BlockComment or IonTriviaKind.DocBlockComment))
            .ThenReturn(" ");

    /// <summary>
    /// The value of an enum/flags member. A block comment inside the value collapses to a space;
    /// a <c>//</c> line comment matches neither alternative and therefore ends the value, so a
    /// trailing <c>// note</c> is no longer swallowed into it (which used to surface as a bogus
    /// ION0007_InvalidEnumValue).
    /// </summary>
    private static Parser<char, IonExpression> Expression =>
        Map((startPos, parts, endPos) =>
                new IonExpression(string.Concat(parts).Trim()).WithPos(startPos, endPos),
            CurrentPos,
            OneOf(ExpressionChunk, ExpressionBlockComment).AtLeastOnce(),
            CurrentPos
        );

    private static Parser<char, IonSyntaxMember> FlagsCore(bool recover) =>
        EnumLikeCore("flags", (identifier, syntax, members) => new IonFlagsSyntax(identifier, syntax, members.ToList()),
            recover);

    private static Parser<char, IonSyntaxMember> EnumsCore(bool recover) =>
        EnumLikeCore("enum", (identifier, syntax, members) => new IonEnumSyntax(identifier, syntax, members.ToList()),
            recover);

    public static Parser<char, IonSyntaxMember> Flags => WithLeading(FlagsCore(recover: true));

    public static Parser<char, IonSyntaxMember> Enums => WithLeading(EnumsCore(recover: true));

    public static Parser<char, IonSyntaxMember> EnumLike(string keyword,
        Func<IonIdentifier, IonUnderlyingTypeSyntax, IEnumerable<IonFlagEntrySyntax>, IonSyntaxMember> ctor) =>
        WithLeading(EnumLikeCore(keyword, ctor, recover: true));

    /// <remarks>
    /// An entry is just a name and an optional value, so the member itself hardly ever fails — it is
    /// the <em>separator</em> that goes missing, which is why
    /// <see cref="SeparatedMembers{T}"/> attaches the <c>,</c> to the slot that follows it rather
    /// than parsing it between slots. <c>Invalid Entry With Spaces,</c> reads as the entry
    /// <c>Invalid</c> plus an unreadable span where its <c>,</c> should have been.
    /// </remarks>
    private static Parser<char, IonSyntaxMember> EnumLikeCore(string keyword,
        Func<IonIdentifier, IonUnderlyingTypeSyntax, IEnumerable<IonFlagEntrySyntax>, IonSyntaxMember> ctor,
        bool recover) =>
        Map(IonSyntaxMember (pos, name, baseType, entries, endPos) =>
                ctor(name, baseType.HasValue
                        ? baseType.Value
                        : new IonUnderlyingTypeSyntax(new IonIdentifier("u4"), [], false, false, false),
                        entries.Members)
                    .WithPos(pos, endPos)
                    .WithInvalidMembers(entries.Invalid),
            CurrentPos,
            String(keyword).Before(SkipTrivia).Then(Identifier),
            Try(Char(':').Before(SkipTrivia).Then(Type)).Optional(),
            Braced(SeparatedMembers(FlagEntry, ',', recover)),
            CurrentPos
        );
}
