namespace IonTestClientServer;

using System.Formats.Cbor;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;
using TestContracts;
using static Assert;

/// <summary>
/// A payload shorter than the reader's schema by <b>nullable</b> trailing items decodes, with the
/// missing items null; a payload short by a <b>required</b> item is still the typed
/// <see cref="IonFieldCountException"/>.
/// </summary>
/// <remarks>
/// <para>
/// The message and union-case halves of this are pinned across all three runtimes by the
/// <c>nullable-appended.*</c> vectors in <c>tests/golden/compat.golden.json</c>. What those cannot
/// reach is a generated <i>executor</i>: the compat module is compiled with <c>models</c> only, and
/// its <c>Args&lt;…&gt;</c> reader reproduces the executor prologue rather than running it. So the
/// argument half is driven here through <c>Ion_MathInteraction_ServiceExecutor</c> — the code
/// <c>MapRpcEndpoints</c> calls — with hand-built argument arrays.
/// </para>
/// <para>
/// <c>MathInteraction(leftOperand: i4)</c> puts its service argument first, so
/// <c>ToPositive(rightOperand: i4?)</c> reads <c>[leftOperand, rightOperand]</c>: one required
/// item and one nullable. A client from before <c>rightOperand</c> existed would send <c>[1]</c>.
/// </para>
/// </remarks>
[TestFixture]
public class NullableTailTests
{
    private IonTestFactoryAsp _factory = null!;

    [SetUp]
    public void Setup() => _factory = new IonTestFactoryAsp();

    [TearDown]
    public void Teardown() => _factory.Dispose();

    /// <summary>Runs one call through the generated executor and returns the response bytes.</summary>
    private async Task<string> Execute(string method, string argumentArrayHex)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var executor = new Ion_MathInteraction_ServiceExecutor(scope);

        var reader = new CborReader(Convert.FromHexString(argumentArrayHex));
        var writer = new CborWriter();
        await executor.RouteExecuteAsync(method, reader, writer);

        That(reader.BytesRemaining, Is.Zero, "the executor consumes exactly the argument array");
        return Convert.ToHexString(writer.Encode()).ToLowerInvariant();
    }

    [Test]
    public async Task Executor_MissingTrailingNullableArgument_ReadsAsNull()
        // [1] → ToPositive(1, null) → null
        => That(await Execute("ToPositive", "8101"), Is.EqualTo("f6"));

    [Test]
    public async Task Executor_PresentTrailingNullableArgument_IsRead()
        // [1, -5] → ToPositive(1, -5) → 5
        => That(await Execute("ToPositive", "820124"), Is.EqualTo("05"));

    [Test]
    public async Task Executor_NewerClientsExtraArgument_IsStillSkipped()
        // [1, -5, true] — a third argument this revision does not declare.
        => That(await Execute("ToPositive", "830124f5"), Is.EqualTo("05"));

    [Test]
    public void Executor_MissingRequiredArgument_IsStillATypedFieldCountError()
    {
        // [] — leftOperand is required; there is no value to call the service with.
        var e = ThrowsAsync<IonFieldCountException>(() => Execute("ToPositive", "80"))!;

        Multiple(() =>
        {
            That(e.ExpectedFields, Is.EqualTo(1), "the required count, not the declared 2");
            That(e.ActualFields, Is.EqualTo(0));
            That(e.Context, Is.EqualTo("MathInteraction.ToPositive"));
        });
    }

    [Test]
    public void Executor_AllRequiredArguments_StillRejectsAShortArray()
    {
        // Add(rightOperand: i4): [1] lacks the required rightOperand.
        var e = ThrowsAsync<IonFieldCountException>(() => Execute("Add", "8101"))!;

        That(e.ExpectedFields, Is.EqualTo(2));
        That(e.ActualFields, Is.EqualTo(1));
    }

    // ── the runtime entry points on their own ───────────────────────────────

    [Test]
    public void ReadStartMessage_RequiredCount_IsTheThreshold()
    {
        // [1] against a three-field schema that requires one.
        var reader = new CborReader(Convert.FromHexString("8101"));
        var declared = reader.ReadStartMessage(3, "Msg", requiredFields: 1);
        That(declared, Is.EqualTo(1));
        That(reader.ReadInt32(), Is.EqualTo(1));
        reader.ReadEndMessage(declared, 3);
        That(reader.BytesRemaining, Is.Zero);

        // The same bytes against a schema that requires two.
        var e = Throws<IonFieldCountException>(
            () => new CborReader(Convert.FromHexString("8101")).ReadStartMessage(3, "Msg", requiredFields: 2))!;
        That(e.ExpectedFields, Is.EqualTo(2));
        That(e.ActualFields, Is.EqualTo(1));
    }

    [Test]
    public void ReadEndMessage_SkipsALongerPayloadsTail()
    {
        // [1, true, true] against a two-field schema.
        var reader = new CborReader(Convert.FromHexString("8301f5f5"));
        var declared = reader.ReadStartMessage(2, "Msg", requiredFields: 1);
        That(reader.ReadInt32(), Is.EqualTo(1));
        That(reader.ReadBoolean(), Is.True);
        reader.ReadEndMessage(declared, 2);
        That(reader.BytesRemaining, Is.Zero);
    }

    [Test]
    public void ReadStartMessage_TwoArgumentForm_StillRequiresEveryField()
    {
        // What code generated before 2.0.3 calls; its behaviour must not move.
        var e = Throws<IonFieldCountException>(
            () => new CborReader(Convert.FromHexString("8101")).ReadStartMessage(2, "Msg"))!;
        That(e.ExpectedFields, Is.EqualTo(2));
    }

    [Test]
    public void ReadStartMessage_RequiredCountOutsideTheDeclaredRange_IsAProgrammingError()
    {
        Throws<ArgumentOutOfRangeException>(
            () => new CborReader(Convert.FromHexString("8101")).ReadStartMessage(1, "Msg", requiredFields: 2));
        Throws<ArgumentOutOfRangeException>(
            () => new CborReader(Convert.FromHexString("8101")).ReadStartMessage(1, "Msg", requiredFields: -1));
    }
}
