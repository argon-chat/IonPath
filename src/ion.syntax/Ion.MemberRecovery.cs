namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

/// <summary>
/// Member level error recovery: the inside-a-declaration counterpart of
/// <see cref="IonParser.RecoverToNextDefinition"/>.
/// <para>
/// File level recovery resynchronises on a declaration keyword at the start of a line. Inside a
/// declaration body the equivalent landmark is the <em>member boundary</em>: the <c>;</c> that ends
/// a field or a method, the <c>,</c> that separates two union cases or enum members, the closing
/// <c>}</c>, and — because Ion is written one member per line — the end of the line. A run of source
/// that cannot be read as a member is consumed up to the next such landmark and reported as an
/// <see cref="InvalidIonBlock"/> hung off the enclosing declaration
/// (<see cref="IonSyntaxMember.InvalidMembers"/>), which <c>IonParser.BuildFileSyntax</c> lifts into
/// <see cref="IonFileSyntax.allTokens"/> so <c>ionc</c> and the language server report it exactly as
/// they already report a file level one.
/// </para>
/// <para>
/// <b>What is and is not a recovery point.</b> A <em>member list</em> is: its elements are delimited,
/// so there is a well defined place to resume and losing one element still leaves a meaningful
/// declaration. An <em>argument list</em> is not: <c>(a: i4, b)</c> has no line structure, its commas
/// are also the commas of a generic argument, and a method whose parameters were silently dropped is
/// a wire contract that quietly disagrees with its own source. So <c>service S(…)</c>, a method's
/// <c>(…)</c>, an attribute's argument list and a <c>with</c> clause all still fail hard, and the
/// failure is caught one level up by file level recovery, which reports the whole declaration.
/// </para>
/// <para>
/// The same reasoning draws the line <em>within</em> a member. Recovery is offered only while the
/// member is still unrecognisable — before <c>name :</c> for a field, before the <c>(</c> for a
/// method. Once that prefix has matched, the member is committed and a failure after it is a real
/// error that propagates: that is what keeps <c>f4[99999999999]</c> reporting "does not fit in a 32
/// bit integer" instead of quietly becoming an unparsed span. Hence <see cref="Parser.Try{T,U}"/>
/// wraps the recognition prefix only, never the whole member.
/// </para>
/// </summary>
public partial class IonParser
{
    /// <summary>
    /// One slot of a recovering member list: either a member that parsed or a span that did not.
    /// </summary>
    private readonly record struct IonMemberSlot<T>(T? Member, InvalidIonBlock? Invalid)
        where T : class;

    /// <summary>The members of a declaration body, and the spans of it that could not be read.</summary>
    private readonly record struct IonMembers<T>(List<T> Members, List<InvalidIonBlock> Invalid);

    #region where a member list ends

    /// <summary>
    /// True when nothing but trivia stands between here and the closing <c>}</c> or end of input.
    /// <para>
    /// Consumes what it tests, so every use is wrapped in <see cref="Parser.Try{T,U}"/>. The
    /// <see cref="SkipTriviaAll"/> is what keeps a dangling <c>///</c> in front of a <c>}</c> from
    /// looking like a member: recovery declines it, the enclosing <c>Many</c> ends cleanly and the
    /// comment is flushed by the closing token's own <see cref="SkipTriviaAll"/> — the property
    /// <c>LeadingSection</c> was built to guarantee, and which recovery must not undo.
    /// </para>
    /// </summary>
    private static Parser<char, Unit> AtMemberListEnd =>
        SkipTriviaAll.Then(OneOf(Lookahead(Char('}')).ThenReturn(Unit.Value), End));

    /// <summary>As <see cref="AtMemberListEnd"/>, plus the separator of a <c>,</c>-separated list.</summary>
    /// <remarks>
    /// Declining to start on the separator is what makes <c>A,,B</c> and a trailing <c>A,</c> stay
    /// parse errors: the list has already consumed the <c>,</c> and there is nothing for recovery to
    /// stand in for, so the slot fails having consumed and the declaration fails, exactly as today.
    /// </remarks>
    private static Parser<char, Unit> AtSeparatorOrMemberListEnd(char separator) =>
        SkipTriviaAll.Then(OneOf(
            Lookahead(Char(separator)).ThenReturn(Unit.Value),
            Lookahead(Char('}')).ThenReturn(Unit.Value),
            End));

    #endregion

    #region where recovery stops

    /// <summary>
    /// The end of the line the unreadable span started on. Ion is written one member per line, so
    /// this is what lets a second mistake further down the body be reported on its own instead of
    /// being swallowed together with the first — and it is the only landmark available when the
    /// broken member never wrote its <c>;</c>.
    /// </summary>
    private static Parser<char, Unit> AtEndOfLine =>
        Lookahead(OneOf(Char('\r'), Char('\n'))).ThenReturn(Unit.Value);

    /// <summary>
    /// Resync for a list whose members carry their own terminator (fields, service methods). The
    /// <c>;</c> belongs to the broken member, so it is consumed: the next member starts after it.
    /// </summary>
    private static Parser<char, Unit> TerminatedMemberResync =>
        OneOf(
            Lookahead(Char('}')).ThenReturn(Unit.Value),
            End,
            Char(';').ThenReturn(Unit.Value),
            AtEndOfLine);

    /// <summary>
    /// Resync for a <c>,</c>-separated list (union cases, enum/flags entries). The <c>,</c> belongs
    /// to the <em>list</em>, so it is deliberately left in place for the list to consume as the
    /// separator of the next slot.
    /// </summary>
    private static Parser<char, Unit> SeparatedMemberResync(char separator) =>
        OneOf(
            Lookahead(Char('}')).ThenReturn(Unit.Value),
            End,
            Lookahead(Char(separator)).ThenReturn(Unit.Value),
            AtEndOfLine);

    /// <summary>
    /// The unreadable span itself, positioned so a diagnostic can point at it.
    /// </summary>
    /// <param name="resync">Where to stop. See <see cref="TerminatedMemberResync"/>.</param>
    /// <param name="loneTerminator">
    /// For a <c>;</c>-terminated list, the terminator: a stray <c>;</c> is reported on its own rather
    /// than being consumed as the first character of a run that then eats the next good member.
    /// </param>
    /// <remarks>
    /// <see cref="RecoverUnit"/> — shared with file level recovery — consumes comments and string
    /// literals whole, so a <c>;</c>, a <c>,</c> or a newline that only occurs inside one is never
    /// mistaken for a member boundary. <c>AtLeastOnceUntil</c> guarantees at least one character is
    /// consumed, which is what stops the enclosing <c>Many</c> from spinning on an empty match.
    /// </remarks>
    private static Parser<char, InvalidIonBlock> RecoveredSpan(Parser<char, Unit> resync, char? loneTerminator)
    {
        var run = RecoverUnit.AtLeastOnceUntil(resync).Select(string.Concat);

        return Map(
            (start, text, end) => new InvalidIonBlock(text).WithPos(start, end),
            CurrentPos,
            loneTerminator is { } terminator
                ? OneOf(Char(terminator).Select(char.ToString), run)
                : run,
            CurrentPos);
    }

    /// <summary>An unreadable member of a <c>;</c>-terminated list.</summary>
    private static Parser<char, InvalidIonBlock> InvalidTerminatedMember =>
        Try(Not(Try(AtMemberListEnd)))
            .Then(SkipTriviaAll)
            .Then(RecoveredSpan(TerminatedMemberResync, ';'));

    /// <summary>An unreadable member of a <c>,</c>-separated list.</summary>
    private static Parser<char, InvalidIonBlock> InvalidSeparatedMember(char separator) =>
        Try(Not(Try(AtSeparatorOrMemberListEnd(separator))))
            .Then(SkipTriviaAll)
            .Then(RecoveredSpan(SeparatedMemberResync(separator), null));

    #endregion

    #region the lists

    /// <summary><c>{ … }</c> around a member list, flushing dangling trivia before the <c>}</c>.</summary>
    private static Parser<char, IonMembers<T>> Braced<T>(Parser<char, IonMembers<T>> body) =>
        body.Between(Char('{').Before(SkipTrivia), SkipTriviaAll.Then(Char('}')));

    /// <summary>
    /// A list of members that carry their own terminator.
    /// </summary>
    /// <param name="strict">The member as the non-recovering grammar reads it.</param>
    /// <param name="recoverable">
    /// The same member with <see cref="Parser.Try{T,U}"/> around its recognition prefix only, so that
    /// an unrecognisable member backs out without consuming and a committed one still fails loudly.
    /// </param>
    /// <param name="recover">
    /// <see langword="false"/> reproduces <c>member.Many()</c> exactly — the strict grammar is
    /// untouched, and a well formed body parses through the identical parser it always did.
    /// </param>
    private static Parser<char, IonMembers<T>> TerminatedMembers<T>(
        Parser<char, T> strict,
        Parser<char, T> recoverable,
        bool recover) where T : class =>
        recover
            ? OneOf(
                    recoverable.Select(m => new IonMemberSlot<T>(m, null)),
                    InvalidTerminatedMember.Select(b => new IonMemberSlot<T>(null, b)))
                .Many()
                .Select(Collect)
            : strict.Many().Select(members => new IonMembers<T>(members.ToList(), []));

    /// <summary>
    /// A <c>,</c>-separated list of members.
    /// </summary>
    /// <remarks>
    /// A member is offered <em>only</em> where one is grammatically allowed — first in the list, or
    /// straight after a separator. That is what keeps the separator a real requirement:
    /// <c>Invalid Entry With Spaces,</c> parses as the entry <c>Invalid</c> and then, with no
    /// <c>,</c> in hand, <c>Entry</c> is not offered as an entry at all even though it would parse
    /// as one — the rest of the line is recovered as an unreadable span instead. A list whose
    /// commas are merely missing therefore reports every extra name rather than quietly accepting
    /// it, and <c>A,,B</c>, a leading <c>,</c> and a trailing <c>,</c> all stay the parse errors
    /// they are in the strict grammar.
    /// </remarks>
    private static Parser<char, IonMembers<T>> SeparatedMembers<T>(
        Parser<char, T> member,
        char separator,
        bool recover) where T : class
    {
        if (!recover)
            return member.Separated(Char(separator).Before(SkipTrivia))
                .Select(members => new IonMembers<T>(members.ToList(), []));

        // A member, or — where one cannot be read — the span standing in for it.
        var slot = OneOf(
            member.Select(m => new IonMemberSlot<T>(m, null)),
            InvalidSeparatedMember(separator).Select(b => new IonMemberSlot<T>(null, b)));

        // Everything after the first slot: either a separator and then a slot, or junk where the
        // separator should have been. Once the separator is consumed a slot is required, so a
        // dangling `A,` fails having consumed and the declaration fails, exactly as today.
        var rest = OneOf(
            Char(separator).Before(SkipTrivia).Then(slot),
            InvalidSeparatedMember(separator).Select(b => new IonMemberSlot<T>(null, b)));

        return OneOf(
            Map((first, more) => Collect(more.Prepend(first)), slot, rest.Many()),
            Return(new IonMembers<T>([], [])));
    }

    private static IonMembers<T> Collect<T>(IEnumerable<IonMemberSlot<T>> slots) where T : class
    {
        var members = new List<T>();
        var invalid = new List<InvalidIonBlock>();

        foreach (var slot in slots)
        {
            if (slot.Member is not null)
                members.Add(slot.Member);
            if (slot.Invalid is not null)
                invalid.Add(slot.Invalid);
        }

        return new IonMembers<T>(members, invalid);
    }

    #endregion
}
