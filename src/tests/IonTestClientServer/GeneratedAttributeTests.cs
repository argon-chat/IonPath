namespace IonTestClientServer;

using ion.runtime;
using System.Reflection;
using TestContracts;
using static Assert;

// Naming a deprecated declaration warns — CS0618 for `[Obsolete("…")]`, CS0612 for the bare
// `[Obsolete]` — and every test below does exactly that. That is the feature working, not a
// problem: with these pragmas removed the build reports both here and stays green, because the
// generator emits `[Obsolete(message)]` and never `[Obsolete(message, error: true)]`, whose CS0619
// would turn every consumer's build into a failure. Suppressed only in this file, whose job is to
// inspect deprecated declarations rather than to consume them.
#pragma warning disable CS0612, CS0618

/// <summary>
/// Pins what the C# generator emits for Ion attributes, read back off the compiled metadata.
/// </summary>
/// <remarks>
/// <para>
/// Reflection rather than a golden text comparison, because the interesting property is not "the
/// generator wrote these characters" but "the C# compiler bound them to the attribute the schema
/// meant". Two of the defects these guard were invisible to a text diff:
/// </para>
/// <list type="bullet">
/// <item><c>@deadline(30)</c> was emitted verbatim as <c>[deadline(30)]</c>, which resolves against
/// nothing — the runtime class is <see cref="DeadlineAttribute"/>. Text-identical output, no
/// attribute in the metadata.</item>
/// <item>Argument values were <c>string.Join</c>ed raw, so a string argument arrived unquoted and an
/// omitted trailing optional arrived as an empty slot — <c>[Cache(30,users,)]</c>. That is a
/// compile error, but the shape it degrades into once the quoting is fixed (a value of the wrong
/// CLR type, e.g. <c>"30"</c> for an <c>i4</c> parameter) is not, and is exactly what
/// <c>IonAttributeBinder</c>'s widening of every integer to <see cref="System.Numerics.BigInteger"/>
/// would have produced.</item>
/// </list>
/// <para>
/// The declarations under test live in <c>Contracts/AttributeInteraction.ion</c> and are referenced
/// by nothing else, so a change here is always a deliberate change to attribute emission.
/// </para>
/// </remarks>
[TestFixture]
public sealed class GeneratedAttributeTests
{
    private static T Single<T>(MemberInfo member) where T : Attribute
        => member.GetCustomAttribute<T>()
           ?? throw new AssertionException($"{member.Name} carries no [{typeof(T).Name}]");

    private static MethodInfo Method(string name)
        => typeof(ICacheInteraction).GetMethod(name)
           ?? throw new AssertionException($"ICacheInteraction.{name} was not generated");

    // ── @deprecated → [Obsolete] ───────────────────────────────────────

    /// <summary>Bare <c>@deprecated</c> carries no message, not an empty one.</summary>
    [Test]
    public void Deprecated_NoArguments_IsBareObsolete()
        => That(Single<ObsoleteAttribute>(Method("Bare")).Message, Is.Null);

    /// <summary><c>since</c> and <c>reason</c> are folded into the one message slot C# has.</summary>
    [Test]
    public void Deprecated_BothArguments_FoldsSinceIntoMessage()
        => That(Single<ObsoleteAttribute>(Method("Legacy")).Message,
            Is.EqualTo("Since 0.6: use Current instead"));

    /// <summary>
    /// A named <c>reason:</c> leaves <c>since</c> null in the <em>leading</em> slot; the message
    /// must be the reason alone, with no stray separator from the missing half.
    /// </summary>
    [Test]
    public void Deprecated_ReasonOnly_OmitsSince()
        => That(Single<ObsoleteAttribute>(Method("Renamed")).Message, Is.EqualTo("Renamed to Current"));

    /// <summary><c>@deprecated</c> on a msg lands on the generated record.</summary>
    [Test]
    public void Deprecated_OnMessage_MarksTheRecord()
        => That(Single<ObsoleteAttribute>(typeof(LegacyRequest)).Message, Is.Null);

    /// <summary>
    /// <c>@deprecated</c> on a field lands on the generated <em>property</em>, not on the
    /// constructor parameter, where nothing would observe it.
    /// </summary>
    [Test]
    public void Deprecated_OnField_MarksTheProperty()
    {
        var property = typeof(LegacyRequest).GetProperty(nameof(LegacyRequest.oldId))!;
        That(Single<ObsoleteAttribute>(property).Message, Is.EqualTo("Since 0.4."));
        That(typeof(LegacyRequest).GetProperty(nameof(LegacyRequest.name))!
            .GetCustomAttribute<ObsoleteAttribute>(), Is.Null, "the neighbouring field is not deprecated");
    }

    /// <summary><c>@deprecated</c> on an enum and on a service.</summary>
    [Test]
    public void Deprecated_OnEnumAndService()
    {
        That(Single<ObsoleteAttribute>(typeof(CacheRegion)).Message,
            Is.EqualTo("Since 0.5: regions were replaced by explicit key parts"));
        That(Single<ObsoleteAttribute>(typeof(ILegacyCacheInteraction)).Message,
            Is.EqualTo("Since 0.6: use CacheInteraction"));
    }

    /// <summary>
    /// <c>@deprecated</c> on an enum <em>member</em> reaches the generator and emits.
    /// </summary>
    /// <remarks>
    /// This was a compiler-side gap, not a generator one: <c>ion.compiler.TransformStage</c> built
    /// every <c>IonConstant</c> with a hardcoded empty attribute list — the enum arm and the flags
    /// arm both passed <c>[]</c> — so a member's attributes were validated and then dropped during
    /// lowering, and the IR the generator received had none. The emission path was wired the whole
    /// time. Union shared fields and attribute-declaration parameters were dropped the same way.
    /// </remarks>
    [Test]
    public void Deprecated_OnEnumMember_Emits()
    {
        var member = typeof(CacheRegion).GetField(nameof(CacheRegion.Local))!;

        That(member.GetCustomAttribute<ObsoleteAttribute>(), Is.Not.Null,
            "enum member attributes used to be dropped in TransformStage");
    }

    // ── std attribute mapping ──────────────────────────────────────────

    /// <summary>
    /// <c>@deadline(30)</c> binds to <see cref="DeadlineAttribute"/> — the whole point of the
    /// std-name mapping, and the thing verbatim emission could never do.
    /// </summary>
    [Test]
    public void Deadline_ResolvesToTheRuntimeAttribute()
        => That(Single<DeadlineAttribute>(Method("Current")).Seconds, Is.EqualTo(30u));

    /// <summary>Std markers with no C# counterpart emit nothing at all.</summary>
    [Test]
    public void CompilerInternalStdAttributes_EmitNothing()
    {
        var names = Method("Ping").GetCustomAttributes().Select(a => a.GetType().Name).ToList();
        That(names, Does.Not.Contain("internalAttribute"), "@internal has no C# form");
        That(names, Contains.Item("AllowAnonymousAttribute"), "the user attribute beside it still lands");
    }

    // ── user-declared attribute arguments ──────────────────────────────

    /// <summary>
    /// Every argument shape at once: an integer that is not a quoted string, a real string, a
    /// bool, a typed array, and a written trailing optional.
    /// </summary>
    [Test]
    public void UserAttribute_AllArgumentKinds()
    {
        var cache = Single<CacheAttribute>(typeof(CacheProbe).GetProperty(nameof(CacheProbe.hits))!);

        That(cache.Duration, Is.EqualTo(5));
        That(cache.Region, Is.EqualTo("field"));
        That(cache.Shared, Is.False);
        That(cache.KeyParts, Is.EqualTo(new[] { 0, 1 }));
        That(cache.Note, Is.EqualTo("written on a field"));
    }

    /// <summary>
    /// An omitted trailing optional is dropped from the argument list rather than emitted as an
    /// empty slot, and an empty array still names its element type.
    /// </summary>
    [Test]
    public void UserAttribute_OmittedOptionalAndEmptyArray()
    {
        var cache = Single<CacheAttribute>(typeof(CacheProbe).GetProperty(nameof(CacheProbe.misses))!);

        That(cache.KeyParts, Is.Empty);
        That(cache.Note, Is.Null, "the omitted optional falls back to the constructor default");
    }

    /// <summary>The same attribute on a msg, a service and a method, not just on fields.</summary>
    [Test]
    public void UserAttribute_OnEveryTargetItWasWrittenOn()
    {
        That(Single<CacheAttribute>(typeof(CacheProbe)).Region, Is.EqualTo("requests"));
        That(Single<CacheAttribute>(typeof(ICacheInteraction)).Region, Is.EqualTo("service"));
        That(Single<CacheAttribute>(Method("Current")).Duration, Is.EqualTo(15));
    }

    /// <summary>A parameterless attribute is emitted as <c>[Name]</c> and still binds.</summary>
    [Test]
    public void UserAttribute_NoArguments()
        => That(Method("Ping").GetCustomAttribute<AllowAnonymousAttribute>(), Is.Not.Null);
}
