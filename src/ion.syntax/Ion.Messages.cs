namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

/// <summary>
/// Messages, the field production, and the type reference grammar they are built from.
/// <code>
/// message   := "msg" identifier withClause? fieldList
/// mixin     := "mixin" identifier withClause? fieldList        // Ion.Mixins.cs
/// withClause:= "with" identifier ( ',' identifier )*
/// fieldList := '{' field* '}'
/// field     := leading identifier ':' type ';'
///
/// type      := ( inlineMsg | identifier genericArgs? ) modifier*
/// inlineMsg := "msg" &amp;'{' fieldList                            // anonymous, hoisted by the compiler
/// genericArgs := '&lt;' ( typeArg ( ',' typeArg )* )? '&gt;'
/// typeArg   := type ( ':' type ( ',' type )* )?                // the constraint tail is discarded
/// modifier  := '?' | '~' | array
/// array     := '[' size? ']'
/// size      := '-'? digit+                                     // carried, not validated
/// </code>
/// </summary>
public partial class IonParser
{
    /// <summary>A character that may continue an identifier.</summary>
    private static Parser<char, char> IdentifierChar => LetterOrDigit.Or(Char('_'));

    /// <summary>
    /// A declaration keyword, terminated by a word boundary.
    /// <para>
    /// A bare <c>String("msg")</c> happily matches the first three characters of
    /// <c>msgFoo</c>, so <c>msgFoo { }</c> used to parse as <c>msg Foo</c>. The trailing
    /// <see cref="Parser.Not{TToken,T}"/> rejects that, and the enclosing <c>Try</c> makes the
    /// whole keyword atomic so a rejected match consumes nothing and the surrounding
    /// <c>OneOf</c> can still try the remaining alternatives.
    /// </para>
    /// </summary>
    private static Parser<char, Unit> Keyword(string text) =>
        Try(String(text).Then(Not(Lookahead(IdentifierChar))))
            .Then(SkipTrivia);

    private static Parser<char, Unit> MsgKeyword => Keyword("msg");

    private static Parser<char, IonIdentifier> Identifier =>
        Map(
            (start, first, rest, end) => new IonIdentifier(first + new string(rest)).WithPos(start, end),
            CurrentPos,
            Letter.Or(Char('_')),
            LetterOrDigit.Or(Char('_')).ManyString(),
            CurrentPos
        ).Before(SkipTrivia);

    #region type references

    /// <summary>
    /// How deeply type references may nest — through generic arguments
    /// (<c>A&lt;A&lt;A&lt;…&gt;&gt;&gt;</c>), through inline anonymous message bodies
    /// (<c>msg { a: msg { … }; }</c>), or through any mix of the two.
    /// </summary>
    /// <remarks>
    /// The type parser is built as a finite chain of this many levels rather than as a
    /// self-referential parser, for the same reason <see cref="MaxLiteralNestingDepth"/> exists: a
    /// pathological input such as 100 000 consecutive <c>&lt;</c> fails with an ordinary parse error
    /// instead of overflowing the stack, which is not a catchable exception on .NET and would take
    /// the process (and the test host) down with it.
    /// <para>
    /// One budget covers both kinds of nesting deliberately. They compose — a generic argument may
    /// be an inline type whose field is a generic — so two independent budgets would not bound
    /// anything.
    /// </para>
    /// </remarks>
    public const int MaxTypeNestingDepth = 32;

    private static readonly string TypeTooDeep =
        $"type nested more than {MaxTypeNestingDepth} levels deep";

    /// <summary>
    /// The head of a type reference: everything before the modifier suffixes. Exactly one of
    /// <paramref name="Inline"/> / (<paramref name="Name"/> + <paramref name="Generics"/>) is real.
    /// </summary>
    private readonly record struct IonTypeHead(
        SourcePos Position,
        IonIdentifier Name,
        IReadOnlyList<IonTypeParameterSyntax> Generics,
        IonInlineMessageSyntax? Inline);

    /// <summary>One written modifier suffix, with the span it occupies.</summary>
    /// <param name="Token">
    /// The <em>normalized</em> token: <c>"?"</c>, <c>"~"</c> or <c>"[]"</c>. A sized array suffix
    /// normalizes to <c>"[]"</c> and puts its size in <paramref name="ArraySize"/> — see
    /// <see cref="IonUnderlyingTypeSyntax.ModifierTokens"/> for why that matters.
    /// </param>
    private readonly record struct IonTypeModifier(
        string Token,
        int? ArraySize,
        SourcePos Start,
        SourcePos End);

    /// <summary>
    /// Built lazily, once. It must not be a plain static field initializer: constructing the chain
    /// walks into <see cref="LeadingSection"/> and therefore into <see cref="Literal"/>, whose own
    /// <c>static readonly</c> may or may not have run yet depending on the order the compiler
    /// happens to emit the partial class's initializers. A <see cref="Lazy{T}"/> defers the whole
    /// graph to first parse, by which point every static field is set.
    /// <para>
    /// Nothing reachable from <see cref="BuildType"/> may read <see cref="Type"/> back, or the
    /// <see cref="Lazy{T}"/> throws on re-entry. That is why the field list and the generic argument
    /// list are parameterised by the inner parser instead of referring to the shared one.
    /// </para>
    /// </summary>
    private static readonly Lazy<Parser<char, IonUnderlyingTypeSyntax>> TypeChain =
        new(() => BuildType(MaxTypeNestingDepth));

    /// <summary>
    /// A written type reference.
    /// </summary>
    /// <remarks>
    /// The three <see cref="bool"/>s are a lossy reduction — <c>.Contains</c> cannot tell
    /// <c>Data~</c> from <c>Data~~</c>, nor <c>Data~?</c> from <c>Data?~</c> — so the raw suffix
    /// sequence is handed through untouched as
    /// <see cref="IonUnderlyingTypeSyntax.ModifierTokens"/>. Repeats and non-canonical order are a
    /// diagnostic, not a parse error (<c>ion.compiler.TypeModifierValidationStage</c>): failing here
    /// would abort the enclosing declaration and lose error recovery for everything after it. A
    /// fixed array size of <c>0</c> or below is carried in
    /// <see cref="IonUnderlyingTypeSyntax.ArraySize"/> for exactly the same reason.
    /// </remarks>
    private static Parser<char, IonUnderlyingTypeSyntax> Type => TypeChain.Value;

    /// <summary>
    /// One level of the type chain. <paramref name="depth"/> is how many further levels of nesting
    /// are still affordable; at zero the two recursive positions are replaced by parsers that
    /// consume their opening token and fail, so the error lands on the offending <c>&lt;</c> or
    /// <c>msg</c> rather than being reported as "no type here".
    /// </summary>
    private static Parser<char, IonUnderlyingTypeSyntax> BuildType(int depth)
    {
        var inner = depth > 0 ? BuildType(depth - 1) : null;

        var generics = inner is null
            ? Char('<').Then(Fail<IReadOnlyList<IonTypeParameterSyntax>>(TypeTooDeep))
            : GenericArgumentListOf(inner);

        var named = Map(
            (pos, name, gen) => new IonTypeHead(pos, name, gen.GetValueOrDefault() ?? [], null),
            CurrentPos,
            Identifier.Before(SkipTrivia),
            generics.Optional());

        var inline = inner is null
            ? InlineTypeStart.Then(Fail<IonTypeHead>(TypeTooDeep))
            : Map(
                (pos, _, fields, endPos) =>
                {
                    var body = new IonInlineMessageSyntax(fields.ToList()).WithPos(pos, endPos);
                    return new IonTypeHead(
                        pos,
                        new IonIdentifier(IonUnderlyingTypeSyntax.InlineTypeName).WithPos(pos, endPos),
                        [],
                        body);
                },
                CurrentPos,
                InlineTypeStart,
                FieldListOf(inner),
                CurrentPos)
                // The named head ends on Identifier's own SkipTrivia; the inline head has to do it
                // itself, or `msg { } []` and `msg { } ;` both die on the space after the '}'.
                .Before(SkipTrivia);

        return Map(
            BuildTypeNode,
            OneOf(inline, named),
            ModifierOfType.Many().Select(m => m.ToArray()));
    }

    /// <summary>
    /// The <c>msg</c> of an inline anonymous type, committed only once a <c>{</c> is in sight.
    /// </summary>
    /// <remarks>
    /// Atomic on purpose. Without the lookahead a bare <c>a: msg;</c> — a reference to a type
    /// spelled <c>msg</c>, which parsed fine before inline types existed — would consume the keyword
    /// and then die demanding a body. With it, the alternative backtracks and the named head parses
    /// it exactly as it always did. The lookahead is also why an inline type takes no <c>with</c>
    /// clause: keeping the commit point one character wide is what makes that guarantee cheap.
    /// <para>
    /// <see cref="Keyword"/> already consumes the trivia after the keyword, so
    /// <c>msg /* c */ { … }</c> is inside the <c>Try</c> and works.
    /// </para>
    /// </remarks>
    private static Parser<char, Unit> InlineTypeStart =>
        Try(MsgKeyword.Then(Lookahead(Char('{')))).ThenReturn(Unit.Value);

    private static IonUnderlyingTypeSyntax BuildTypeNode(IonTypeHead head, IonTypeModifier[] modifiers)
    {
        var isOptional = false;
        var isArray = false;
        var isPartial = false;
        int? arraySize = null;
        IonTypeModifier? sized = null;

        var tokens = new string[modifiers.Length];
        for (var i = 0; i < modifiers.Length; i++)
        {
            var modifier = modifiers[i];
            tokens[i] = modifier.Token;

            switch (modifier.Token)
            {
                case "?":
                    isOptional = true;
                    break;
                case "~":
                    isPartial = true;
                    break;
                default:
                    isArray = true;
                    // Only the first size survives. More than one array suffix is already an
                    // ION0019 repeat, so there is no correct answer to preserve.
                    if (arraySize is null && modifier.ArraySize is not null)
                    {
                        arraySize = modifier.ArraySize;
                        sized = modifier;
                    }

                    break;
            }
        }

        var node = new IonUnderlyingTypeSyntax(
                head.Name, head.Generics, isArray, isOptional, isPartial, tokens, arraySize, head.Inline)
            .WithPos(head.Position);

        if (sized is { } size)
        {
            node.ArraySizeStart = size.Start;
            node.ArraySizeEnd = size.End;
        }

        return node;
    }

    private static Parser<char, IonTypeModifier> ModifierOfType =>
        OneOf(
            Map((start, _, end) => new IonTypeModifier("?", null, start, end), CurrentPos, Char('?'), CurrentPos),
            ArrayModifier,
            Map((start, _, end) => new IonTypeModifier("~", null, start, end), CurrentPos, Char('~'), CurrentPos)
        ).Before(SkipTrivia);

    /// <summary>
    /// <c>[]</c> or <c>[N]</c>.
    /// </summary>
    /// <remarks>
    /// Not atomic beyond the opening bracket: once <c>[</c> is consumed the suffix is committed, so
    /// <c>f4[x]</c> reports a missing <c>]</c> instead of the old grammar's "expected ';'". Nothing
    /// else in type position can start with <c>[</c>, so there is no alternative to backtrack for,
    /// and file level recovery still resynchronises on the next declaration either way.
    /// </remarks>
    private static Parser<char, IonTypeModifier> ArrayModifier =>
        Map(
            (start, size, end) => new IonTypeModifier("[]", size.HasValue ? size.Value : null, start, end),
            CurrentPos,
            Char('[')
                .Then(SkipTrivia)
                .Then(FixedArraySize.Before(SkipTrivia).Optional())
                .Before(SkipTriviaAll.Then(Char(']').Labelled("closing ']' of an array modifier"))),
            CurrentPos);

    /// <summary>
    /// The size of a fixed-size array: a plain decimal digit run with an optional leading <c>-</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not the full <see cref="Literal"/> number grammar. A size is a count, not an
    /// expression: no hex, no <c>_</c> separators, no exponent. <c>0</c> and negatives are accepted
    /// and carried for the compiler to diagnose — see
    /// <see cref="IonUnderlyingTypeSyntax.ArraySize"/>. A literal too large for <see cref="int"/> is
    /// the sole shape that fails here, because there is nothing to carry.
    /// </remarks>
    private static Parser<char, int> FixedArraySize =>
        Map(
            (sign, digits) => (Negative: sign.HasValue, Digits: digits),
            Char('-').Optional(),
            DecDigit.AtLeastOnceString().Labelled("fixed array size"))
        .Assert(
            token => AsArraySize(token.Negative, token.Digits) is not null,
            token => $"fixed array size '{(token.Negative ? "-" : string.Empty)}{token.Digits}' " +
                     "does not fit in a 32 bit integer")
        .Select(token => AsArraySize(token.Negative, token.Digits)!.Value);

    /// <summary>The digit run as an <see cref="int"/>, or <see langword="null"/> if it does not fit.</summary>
    /// <remarks>
    /// Length-checked before any conversion so that a megabyte of digits costs a scan rather than a
    /// bignum parse.
    /// </remarks>
    private static int? AsArraySize(bool negative, string digits)
    {
        var significant = digits.TrimStart('0');
        if (significant.Length == 0)
            return 0;
        if (significant.Length > 10 || !long.TryParse(significant, out var value))
            return null;

        if (negative)
            value = -value;

        return value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
    }

    #endregion

    public static Parser<char, Maybe<Unit>> ForbidNext(char c, string message) =>
        Lookahead(
            Char(c).Then(Fail<Unit>(message))
        ).Optional();

    /// <summary>
    /// One field. Parameterised by the type parser so that the body of an inline anonymous type is
    /// the very same production one level down, rather than a copy that can drift from it.
    /// </summary>
    private static Parser<char, IonFieldSyntax> FieldOf(Parser<char, IonUnderlyingTypeSyntax> type) =>
        Map(
            (lead, pos, name, _, _, fieldType, __) => new IonFieldSyntax(name, fieldType)
                .WithComments(lead.Doc)
                .WithAttributes(lead.Attributes)
                .WithPos(pos),
            LeadingSection,
            CurrentPos,
            Identifier.Labelled("field name").Before(SkipTrivia),
            ForbidNext('?', "'?' is not allowed after field name"),
            Char(':').Labelled("':' after field name").Before(SkipTrivia),
            type,
            Char(';').Before(SkipTrivia)
        );

    private static Parser<char, IEnumerable<IonFieldSyntax>> FieldListOf(
        Parser<char, IonUnderlyingTypeSyntax> type) =>
        FieldOf(type).ManyBetween(Char('{').Before(SkipTrivia), SkipTriviaAll.Then(Char('}')));

    /// <summary>The field list of a declaration, at the top of the type chain.</summary>
    private static Parser<char, IEnumerable<IonFieldSyntax>> FieldList => FieldListOf(Type);

    /// <summary>
    /// <c>with Audited, Traced</c>. Shared by <c>msg</c> and <c>mixin</c>, and by nothing else — a
    /// union, service, enum, flags or typedef has no field list to mix into.
    /// </summary>
    /// <remarks>
    /// <see cref="Keyword"/> supplies the word boundary and the <c>Try</c>, so a message with no
    /// clause reaches its <c>{</c> untouched and a field named <c>within</c> is still a field. Once
    /// <c>with</c> matches, at least one name is required: <c>msg M with { }</c> is a parse error
    /// rather than a silently empty clause.
    /// </remarks>
    private static Parser<char, List<IonIdentifier>> WithClause =>
        Keyword("with")
            .Then(Identifier
                .Before(SkipTrivia)
                .Labelled("mixin name")
                .SeparatedAtLeastOnce(Char(',').Before(SkipTrivia)))
            .Select(names => names.ToList());

    private static Parser<char, IonSyntaxMember> MessageCore =>
        Map(IonSyntaxMember
                (pos, msgName, mixins, fields, endPos) =>
                new IonMessageSyntax(msgName, fields.ToList(), mixins.GetValueOrDefault()).WithPos(pos, endPos),
            CurrentPos,
            MsgKeyword.Then(Identifier),
            WithClause.Optional(),
            FieldList,
            CurrentPos
        );

    public static Parser<char, IonSyntaxMember> Message => WithLeading(MessageCore);
}
