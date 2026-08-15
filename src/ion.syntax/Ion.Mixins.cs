namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

/// <summary>
/// Mixin layer of the Ion grammar.
/// <code>
/// mixin Audited { createdAt: datetime; createdBy: guid; }
/// mixin Traced with Audited { traceId: guid; }
/// msg Document with Audited, Traced { title: string; }
/// </code>
/// <para>
/// A mixin is a field-set template, not a type. It has no wire identity, cannot be referenced in
/// type position, and exists only to be spliced into the messages (and mixins) that name it in a
/// <c>with</c> clause. Everything about that — resolution, cycles, field collisions, use in type
/// position — is the compiler's; the grammar's whole job is to produce the node.
/// </para>
/// <para>
/// The body is <see cref="IonParser.FieldList"/>, the same production a <c>msg</c> body is, so doc
/// comments and attributes on a mixin's fields behave identically and cannot drift. The
/// <c>with</c> clause is <see cref="IonParser.WithClause"/>, shared with <c>msg</c>. Neither is
/// available on a union, service, enum, flags or typedef: none of them has a field list to mix into.
/// </para>
/// </summary>
public partial class IonParser
{
    private static Parser<char, Unit> MixinKeyword => Keyword("mixin");

    /// <remarks>
    /// Reached through <see cref="Definition"/>'s hoisted leading section like every other
    /// declaration, so a doc comment or an attribute in front of a <c>mixin</c> attaches to it
    /// instead of turning it into a parse error.
    /// </remarks>
    private static Parser<char, IonMixinSyntax> MixinCore =>
        Map(
            (pos, name, mixins, fields, endPos) =>
                new IonMixinSyntax(name, fields.ToList(), mixins.GetValueOrDefault()).WithPos(pos, endPos),
            CurrentPos,
            MixinKeyword.Then(Identifier),
            WithClause.Optional(),
            FieldList,
            CurrentPos
        );

    public static Parser<char, IonMixinSyntax> Mixin => WithLeading(MixinCore);
}
