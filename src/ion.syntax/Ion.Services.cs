namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public partial class IonParser
{
    /// <summary>
    /// What makes a method recognisable as one: its leading section, its modifiers, its name and a
    /// <c>(</c> in sight. Everything after that is the method's <em>signature</em>, and a mistake
    /// there is a mistake in a method that was written — see <see cref="IonMemberSlot{T}"/>.
    /// </summary>
    /// <remarks>
    /// The <c>(</c> is looked at rather than consumed so that <see cref="ArgList"/> in the tail
    /// remains exactly the production it was, argument list errors included: an argument list is
    /// never a recovery point.
    /// </remarks>
    private readonly record struct IonMethodHead(
        IonLeading Lead,
        SourcePos Position,
        List<IonMethodModifiers> Modifiers,
        IonIdentifier Name);

    private static Parser<char, IonMethodHead> ServiceMethodHead =>
        Map(
            (lead, pos, modifiers, name, _) => new IonMethodHead(lead, pos, modifiers.ToList(), name),
            LeadingSection,
            CurrentPos.Labelled("currentPos"),
            MethodModifiers.Labelled("mod"),
            Identifier.Labelled("identifier"),
            Lookahead(Char('(')).Labelled("'(' of a method argument list")
        );

    private static Parser<char, (List<IonArgumentSyntax> Args, Maybe<IonUnderlyingTypeSyntax> ReturnType)>
        ServiceMethodTail =>
        Map(
            (parameters, returnType, _) => (parameters.ToList(), returnType),
            ArgList.Labelled("args"),
            Char(':').Before(SkipTrivia).Then(Type.Labelled("returnType")).Optional(),
            Char(';').Or(Char(',')).Before(SkipTrivia)
        );

    private static IonMethodSyntax BuildMethod(
        IonMethodHead head,
        (List<IonArgumentSyntax> Args, Maybe<IonUnderlyingTypeSyntax> ReturnType) tail) =>
        new IonMethodSyntax(head.Name, head.Modifiers, tail.Args, tail.ReturnType.GetValueOrDefault())
            .WithPos(head.Position)
            .WithComments(head.Lead.Doc)
            .WithAttributes(head.Lead.Attributes);

    public static Parser<char, IonMethodSyntax> ServiceMethod =>
        Map(BuildMethod, ServiceMethodHead, ServiceMethodTail);

    /// <summary>
    /// <see cref="ServiceMethod"/> with the recognition prefix made atomic, for the recovering
    /// grammar. See <see cref="RecoverableFieldOf"/>.
    /// </summary>
    private static Parser<char, IonMethodSyntax> RecoverableServiceMethod =>
        Map(BuildMethod, Try(ServiceMethodHead), ServiceMethodTail);

    /// <summary>
    /// One method modifier keyword, terminated by a word boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="Keyword"/>, not a bare <c>String</c>. <c>String("internal")</c> happily matches the
    /// first eight characters of <c>internalThing</c>, so <c>internalThing(): i4;</c> parsed as an
    /// <c>internal</c>-modified method called <c>Thing</c> — a wire-visible rename, emitted into the
    /// generated interface and the server dispatcher, with no diagnostic anywhere. The same held for
    /// <c>stream…</c> and <c>unary…</c>. <see cref="Keyword"/> also supplies the <c>Try</c>, so a
    /// rejected match consumes nothing and the identifier parser downstream still sees the full name.
    /// </remarks>
    private static Parser<char, IonMethodModifiers> MethodModifierOne =>
        OneOf(
            Keyword("stream").ThenReturn(IonMethodModifiers.Stream),
            Keyword("unary").ThenReturn(IonMethodModifiers.Unary),
            Keyword("internal").ThenReturn(IonMethodModifiers.Internal)
        );

    private static Parser<char, IEnumerable<IonMethodModifiers>> MethodModifiers =>
        Try(MethodModifierOne).Many();

    /// <remarks>
    /// The base argument list is <em>not</em> a recovery point: it is an argument list, so
    /// <c>service S(validArg: i2, brokenArg, …)</c> still fails outright rather than silently
    /// dropping a parameter from the service's wire contract. Only the method list recovers.
    /// </remarks>
    private static Parser<char, IonServiceSyntax> ServiceCore(bool recover) =>
        Map(
            (pos, name, parameters, methods, endPos) =>
                new IonServiceSyntax(name, parameters.Value.ToList(), methods.Members)
                    .WithPos(pos, endPos)
                    .WithInvalidMembers(methods.Invalid),
            CurrentPos,
            String("service").Before(SkipTrivia).Then(Identifier),
            ArgList.Optional().Assert(maybe => maybe.HasValue, "Argument list required"),
            Braced(TerminatedMembers(ServiceMethod, RecoverableServiceMethod, recover)),
            CurrentPos
        );

    public static Parser<char, IonServiceSyntax> Service => WithLeading(ServiceCore(recover: true));
}
