namespace ion.compiler;

using ion.runtime;
using syntax;

/// <summary>One attribute as written, together with the kind of declaration it was written on.</summary>
public readonly record struct IonAttributeSite(
    IonAttributeSyntax Attribute,
    IonAttributeTarget Target,
    IonSyntaxMember Owner);

/// <summary>
/// The single traversal of "every position in a file where an attribute can be <em>written</em>",
/// paired with the <see cref="IonAttributeTarget"/> that position represents.
/// </summary>
/// <remarks>
/// <para>
/// The companion of <see cref="IonTypeSites"/>, and separate from it on purpose: the two sets do not
/// coincide. A method can carry an attribute but is not a type position; a method's return type is a
/// type position but cannot carry an attribute.
/// </para>
/// <para>
/// The walk is over the <em>syntax</em> tree. That is what makes the target known — the IR has
/// flattened a union case and a msg into the same <see cref="IonType"/>, and an attribute
/// declaration's parameters into the same <see cref="IonArgument"/> as a method's — and it is also
/// the only place a service's base arguments appear once rather than once per method
/// (<c>TransformStage.PrependMethods</c> copies them into every method, so an IR walk reported the
/// same written declaration N times).
/// </para>
/// <para>
/// Two positions are visited whose attributes the IR currently discards: enum / flags members, and
/// the parameters of an attribute declaration. They are still checked, because a rule that is
/// enforced in some positions and silently ignored in others is worse than either.
/// </para>
/// </remarks>
public static class IonAttributeSites
{
    public static IEnumerable<IonAttributeSite> Of(IonFileSyntax file)
    {
        foreach (var msg in file.messageSyntaxes)
        {
            foreach (var site in At(msg, IonAttributeTarget.Msg))
                yield return site;

            foreach (var field in msg.Fields)
            foreach (var site in At(field, IonAttributeTarget.Field))
                yield return site;
        }

        // A mixin's fields carry attributes that really do survive — the expansion reuses these
        // exact syntax nodes, so `@internal createdBy: guid;` on a mixin lands on the field of every
        // message that includes it — and they are validated here, once, at the declaration.
        //
        // The mixin *declaration* is visited as `Msg` because a mixin body is a msg body, so a
        // target clause that forbids `msg` forbids this too rather than silently accepting it. Note
        // that an attribute on the declaration itself is nonetheless dropped during lowering: a
        // mixin produces no IonType for it to attach to. Checking it is still better than not —
        // a rule enforced in some positions and ignored in others is worse than either.
        foreach (var mixin in file.mixinSyntaxes)
        {
            foreach (var site in At(mixin, IonAttributeTarget.Msg))
                yield return site;

            foreach (var field in mixin.Fields)
            foreach (var site in At(field, IonAttributeTarget.Field))
                yield return site;
        }

        foreach (var @enum in file.enumSyntaxes)
        {
            foreach (var site in At(@enum, IonAttributeTarget.Enum))
                yield return site;

            foreach (var entry in @enum.Entries)
            foreach (var site in At(entry, IonAttributeTarget.EnumMember))
                yield return site;
        }

        foreach (var flags in file.flagsSyntaxes)
        {
            foreach (var site in At(flags, IonAttributeTarget.Flags))
                yield return site;

            foreach (var entry in flags.Entries)
            foreach (var site in At(entry, IonAttributeTarget.EnumMember))
                yield return site;
        }

        foreach (var service in file.serviceSyntaxes)
        {
            foreach (var site in At(service, IonAttributeTarget.Service))
                yield return site;

            // Declared once, prepended to every method — yield once.
            foreach (var argument in service.BaseArguments)
            foreach (var site in At(argument, IonAttributeTarget.Argument))
                yield return site;

            foreach (var method in service.Methods)
            {
                foreach (var site in At(method, IonAttributeTarget.Method))
                    yield return site;

                foreach (var argument in method.arguments)
                foreach (var site in At(argument, IonAttributeTarget.Argument))
                    yield return site;
            }
        }

        foreach (var union in file.unionSyntaxes)
        {
            foreach (var site in At(union, IonAttributeTarget.Union))
                yield return site;

            // Shared fields and a case's own arguments both lower to IonField (see
            // TransformStage.PrependFields), so they are fields, not arguments.
            foreach (var shared in union.baseFields)
            foreach (var site in At(shared, IonAttributeTarget.Field))
                yield return site;

            foreach (var @case in union.cases)
            {
                foreach (var site in At(@case, IonAttributeTarget.UnionCase))
                    yield return site;

                foreach (var argument in @case.arguments)
                foreach (var site in At(argument, IonAttributeTarget.Field))
                    yield return site;
            }
        }

        foreach (var typedef in file.typedefSyntaxes)
        foreach (var site in At(typedef, IonAttributeTarget.Typedef))
            yield return site;

        foreach (var attribute in file.attributeDefSyntaxes)
        {
            foreach (var site in At(attribute, IonAttributeTarget.Attribute))
                yield return site;

            foreach (var parameter in attribute.Args)
            foreach (var site in At(parameter, IonAttributeTarget.Argument))
                yield return site;
        }
    }

    private static IEnumerable<IonAttributeSite> At(IonSyntaxMember owner, IonAttributeTarget target) =>
        owner.Attributes.Select(attribute => new IonAttributeSite(attribute, target, owner));
}
