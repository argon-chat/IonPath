namespace IonTestClientServer;

using System.Text.Json;
using IonCompat;
using ion.runtime;
using System.Formats.Cbor;

/// <summary>
/// Cross-runtime backward/forward-compatibility vectors — <c>/tests/golden/compat.golden.json</c>.
/// <para>
/// The same file is read by <c>packages/ion.webcore.js/test/compat.golden.test.ts</c> and
/// <c>packages/ion.rustcore/tests/compat_golden.rs</c>. The schemas under test are <c>ionc</c>'s
/// own output, vendored under <c>Compat/</c> — nothing here re-implements a formatter, so every
/// read goes through the code a real client would run.
/// </para>
/// <para>
/// <b>Read the vector's <c>verdict</c> before its <c>expect</c>.</b> An <c>expect</c> block
/// records what this runtime does <i>today</i>. Where <c>verdict</c> is <c>defect</c> or
/// <c>divergence</c>, the assertion pins wrong behaviour on purpose: fixing the runtime turns the
/// test red and forces the pin to be updated, which is the only way the gap stays visible.
/// </para>
/// <para>
/// <b>Record mode.</b> <c>ION_COMPAT_RECORD=1 dotnet test …</c> writes what this runtime actually
/// did to <c>/tests/golden/.dump/compat.cs.json</c> instead of asserting it;
/// <c>tests/golden/compat.record.py</c> merges the three runtimes' observations back into the
/// golden file.
/// </para>
/// </summary>
[TestFixture]
public class CompatGoldenTests
{
    private const string File = "compat.golden.json";
    private const string RuntimeKey = "cs";

    /// <summary>
    /// A re-encoded value longer than this is summarised rather than spelled out, so one
    /// 100000-deep recursion vector cannot add hundreds of kilobytes of hex to a file whose job
    /// is to be read. FNV-1a/64 rather than a real digest because all three runtimes have to
    /// compute it identically and Rust's test crate has no hash dependency.
    /// </summary>
    private const int DigestAbove = 128;

    /// <summary>
    /// Every observation runs on a thread with a 64 MB stack. Two of the vectors are deep
    /// recursion probes, and a .NET <see cref="StackOverflowException"/> cannot be caught — it
    /// takes the whole test process with it. The large stack is what makes the depth reachable as
    /// a result rather than as a crash; that a depth limit has to be supplied by the *test* is
    /// itself one of the findings.
    /// </summary>
    private const int ProbeStackBytes = 64 * 1024 * 1024;

    private static readonly bool Record =
        Environment.GetEnvironmentVariable("ION_COMPAT_RECORD") == "1";

    private static readonly Dictionary<string, object> Recorded = new();

    private static JsonElement Golden => GoldenFile.Load(File);

    private static string TrailerHex => Golden.GetProperty("trailerHex").GetString()!;

    public static IEnumerable<TestCaseData> EvolutionVectors() => Cases("evolution", trailer: true);

    public static IEnumerable<TestCaseData> MalformedVectors() => Cases("malformed", trailer: false);

    private static IEnumerable<TestCaseData> Cases(string section, bool trailer)
    {
        foreach (var v in Golden.GetProperty(section).EnumerateArray())
        {
            var name = v.GetProperty("name").GetString()!;
            yield return new TestCaseData(name, trailer)
                .SetName($"{section}: {name} [{v.Str("verdict")}]");
        }
    }

    private static JsonElement Vector(string name)
    {
        foreach (var section in new[] { "evolution", "malformed" })
            foreach (var v in Golden.GetProperty(section).EnumerateArray())
                if (v.GetProperty("name").GetString() == name)
                    return v;
        throw new InvalidOperationException($"no vector named '{name}'");
    }

    [Test]
    [TestCaseSource(nameof(EvolutionVectors))]
    public void Evolution(string name, bool trailer) => Run(name, trailer);

    [Test]
    [TestCaseSource(nameof(MalformedVectors))]
    public void Malformed(string name, bool trailer) => Run(name, trailer);

    private static void Run(string name, bool trailer)
    {
        var vector = Vector(name);
        var actual = Observe(vector, trailer);

        if (Record)
        {
            lock (Recorded) Recorded[name] = actual;
            return;
        }

        var expect = vector.GetProperty("expect");
        if (!expect.TryGetProperty(RuntimeKey, out var want))
            Assert.Fail($"no recorded C# expectation for vector '{name}'; re-run with ION_COMPAT_RECORD=1");

        Assert.Multiple(() =>
        {
            Assert.That(actual.Outcome, Is.EqualTo(want.GetProperty("outcome").GetString()),
                $"{name}: outcome");
            Assert.That(actual.ReencodedHex, Is.EqualTo(want.Str("reencodedHex") is "" ? null : want.Str("reencodedHex")),
                $"{name}: re-encoded value");
            Assert.That(actual.Consumed,
                Is.EqualTo(want.TryGetProperty("consumed", out var c) ? c.GetInt32() : (int?)null),
                $"{name}: bytes consumed");
            Assert.That(actual.Error, Is.EqualTo(want.Str("error") is "" ? null : want.Str("error")),
                $"{name}: error class");
            Assert.That(actual.Message, Is.EqualTo(want.Str("message") is "" ? null : want.Str("message")),
                $"{name}: error message");
        });
    }

    /// <summary>
    /// Behavioural equality across the three runtimes is the acceptance criterion, so it is
    /// counted rather than left implicit: this recomputes the census from the per-runtime
    /// <c>expect</c> blocks and checks it against the <c>crossRuntime</c> section, so no
    /// expectation can be edited without the divergence list being redone.
    /// <para>
    /// <c>divergentValue</c> must stay empty — all three accepting the same bytes and disagreeing
    /// about what they mean is the worst outcome available.
    /// </para>
    /// </summary>
    [Test]
    public void CrossRuntimeCensus()
    {
        if (Record) Assert.Ignore("record mode");

        var runtimes = new[] { "cs", "ts", "rust" };
        List<string> divergentAcceptance = [], divergentTyping = [], divergentValue = [];
        var agree = 0;

        foreach (var section in new[] { "evolution", "malformed" })
            foreach (var v in Golden.GetProperty(section).EnumerateArray())
            {
                var name = v.GetProperty("name").GetString()!;
                var e = v.GetProperty("expect");
                var outcomes = runtimes.Select(r => e.GetProperty(r).GetProperty("outcome").GetString()).ToList();
                var accepted = outcomes.Count(o => o == "ok");

                if ((accepted > 0 && accepted < runtimes.Length) || outcomes.Contains("panic"))
                    divergentAcceptance.Add(name);
                else if (outcomes.Distinct().Count() > 1)
                    divergentTyping.Add(name);
                else if (outcomes[0] == "ok" &&
                         (runtimes.Select(r => e.GetProperty(r).Str("reencodedHex")).Distinct().Count() > 1 ||
                          runtimes.Select(r => e.GetProperty(r).GetProperty("consumed").GetInt32()).Distinct().Count() > 1))
                    divergentValue.Add(name);
                else
                    agree++;
            }

        var census = Golden.GetProperty("crossRuntime");
        List<string> Stored(string key) =>
            census.GetProperty(key).EnumerateArray().Select(x => x.GetString()!).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(divergentAcceptance, Is.EqualTo(Stored("divergentAcceptance")),
                "vectors on which at least one runtime decodes and at least one refuses or panics");
            Assert.That(divergentValue, Is.EqualTo(Stored("divergentValue")),
                "vectors all three accept and decode differently — this list must stay empty");
            Assert.That(divergentTyping, Is.EqualTo(Stored("divergentErrorTyping")),
                "vectors all three reject, with different error typing");
            Assert.That(agree, Is.EqualTo(census.GetProperty("agreeCount").GetInt32()));
        });
    }

    [OneTimeTearDown]
    public void WriteRecording()
    {
        if (!Record) return;
        var dir = Path.Combine(GoldenFile.Directory, ".dump");
        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(
            Path.Combine(dir, "compat.cs.json"),
            JsonSerializer.Serialize(Recorded, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }) + "\n");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Observation
    // ═══════════════════════════════════════════════════════════════════════

    private sealed record Observation(string Outcome, string? ReencodedHex, int? Consumed, string? Error, string? Message);

    private static Observation Observe(JsonElement vector, bool trailer)
    {
        var hex = VectorHex(vector) + (trailer ? TrailerHex : "");
        var bytes = GoldenFile.Bytes(hex);
        var codec = CodecFor(vector.GetProperty("reader").GetString()!);

        Observation? result = null;
        var thread = new Thread(() => result = Observe(codec, bytes), ProbeStackBytes);
        thread.Start();
        thread.Join();
        return result!;
    }

    private static Observation Observe(Codec codec, byte[] bytes)
    {
        var reader = new CborReader(bytes);
        object? value;
        try
        {
            value = codec.Read(reader);
        }
        catch (IonDecodeException e)
        {
            return new Observation("error", null, null, e.GetType().Name, null);
        }
        catch (Exception e)
        {
            return new Observation("untyped-error", null, null, e.GetType().Name, Truncate(e.Message));
        }

        var consumed = bytes.Length - reader.BytesRemaining;
        string reencoded;
        try
        {
            var writer = new CborWriter();
            codec.Write(writer, value);
            reencoded = Summarise(writer.Encode());
        }
        catch (Exception e)
        {
            // The decode "succeeded" and produced something the same schema cannot write back.
            // That is not a decode failure — the un-writable result is the finding.
            reencoded = $"<re-encode failed: {e.GetType().Name}>";
        }

        return new Observation("ok", reencoded, consumed, null, null);
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120];

    private static string Summarise(byte[] bytes)
    {
        if (bytes.Length <= DigestAbove) return Convert.ToHexString(bytes).ToLowerInvariant();
        var h = 0xcbf29ce484222325UL;
        foreach (var b in bytes)
        {
            h ^= b;
            h *= 0x100000001b3UL;
        }
        return $"fnv1a64:{h:x16}:{bytes.Length}";
    }

    private static string VectorHex(JsonElement vector)
    {
        if (vector.TryGetProperty("hex", out var hex)) return hex.GetString()!;
        var build = vector.GetProperty("build");
        var repeat = build.GetProperty("repeat").GetString()!;
        var count = build.GetProperty("count").GetInt32();
        var sb = new System.Text.StringBuilder(build.GetProperty("prefix").GetString());
        for (var i = 0; i < count; i++) sb.Append(repeat);
        sb.Append(build.GetProperty("suffix").GetString());
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Reader dispatch
    // ═══════════════════════════════════════════════════════════════════════
    // One entry per `reader` spelling in the golden file. Each is the call the generator emits
    // for that position: a message field goes through IonFormatterStorage<T>, a `T[]` field
    // through ReadArray, a method-argument envelope through the executor prologue.

    private sealed class Codec(Func<CborReader, object?> read, Action<CborWriter, object?> write)
    {
        public object? Read(CborReader r) => read(r);

        public void Write(CborWriter w, object? v) => write(w, v);
    }

    private static Codec Msg<T>() => new(
        r => IonFormatterStorage<T>.Read(r),
        (w, v) => IonFormatterStorage<T>.Write(w, (T)v!));

    private static Codec Arr<T>() => new(
        r => IonFormatterStorage<T>.ReadArray(r),
        (w, v) => IonFormatterStorage<T>.WriteArray(w, (IonArray<T>)v!));

    private static Codec Fix<T>(int n) => new(
        r => IonFormatterStorage<T>.ReadFixedArray(r, n),
        (w, v) => IonFormatterStorage<T>.WriteFixedArray(w, (IonArray<T>)v!, n));

    private static Codec MapOf<TKey, TValue>() where TKey : notnull => new(
        r => IonFormatterStorage<Dictionary<TKey, TValue>>.Read(r),
        (w, v) => IonFormatterStorage<Dictionary<TKey, TValue>>.Write(w, (Dictionary<TKey, TValue>)v!));

    private static Codec SetOf<T>() => new(
        r => IonFormatterStorage<HashSet<T>>.Read(r),
        (w, v) => IonFormatterStorage<HashSet<T>>.Write(w, (HashSet<T>)v!));

    private static Codec MaybeOf<T>() where T : class => new(
        r => r.ReadNullable<T>(),
        (w, v) => IonFormatterStorage<T>.WriteNullable(w, (T?)v));

    /// <summary>
    /// The generated executor prologue, verbatim:
    /// <code>
    /// var arraySize = reader.ReadStartArray() ?? throw new Exception("undefined len array not allowed");
    /// // one read per declared argument
    /// reader.ReadEndArrayAndSkip(arraySize - argumentSize);
    /// </code>
    /// </summary>
    private static Codec Args(params Codec[] args) => new(
        r =>
        {
            var arraySize = r.ReadStartArray() ?? throw new Exception("undefined len array not allowed");
            var values = new object?[args.Length];
            for (var i = 0; i < args.Length; i++) values[i] = args[i].Read(r);
            r.ReadEndArrayAndSkip(arraySize - args.Length);
            return values;
        },
        (w, v) =>
        {
            var values = (object?[])v!;
            w.WriteStartArray(args.Length);
            for (var i = 0; i < args.Length; i++) args[i].Write(w, values[i]);
            w.WriteEndArray();
        });

    private static readonly Dictionary<string, Codec> Codecs = new()
    {
        ["AppendedV1"] = Msg<AppendedV1>(),
        ["AppendedV2"] = Msg<AppendedV2>(),
        ["ShiftedV1"] = Msg<ShiftedV1>(),
        ["ShiftedV2"] = Msg<ShiftedV2>(),
        ["SwapV1"] = Msg<SwapV1>(),
        ["SwapV2"] = Msg<SwapV2>(),
        ["InsertedV1"] = Msg<InsertedV1>(),
        ["InsertedV2"] = Msg<InsertedV2>(),
        ["RemovedV1"] = Msg<RemovedV1>(),
        ["RemovedV2"] = Msg<RemovedV2>(),
        ["ReorderedV1"] = Msg<ReorderedV1>(),
        ["ReorderedV2"] = Msg<ReorderedV2>(),
        ["RetypedV1"] = Msg<RetypedV1>(),
        ["RetypedV2"] = Msg<RetypedV2>(),
        ["WidenedV1"] = Msg<WidenedV1>(),
        ["WidenedV2"] = Msg<WidenedV2>(),
        ["OptAddedV1"] = Msg<OptAddedV1>(),
        ["OptAddedV2"] = Msg<OptAddedV2>(),
        ["OptTightenedV1"] = Msg<OptTightenedV1>(),
        ["OptTightenedV2"] = Msg<OptTightenedV2>(),
        ["VarToFixedV1"] = Msg<VarToFixedV1>(),
        ["VarToFixedV2"] = Msg<VarToFixedV2>(),
        ["EnumHolderV1"] = Msg<EnumHolderV1>(),
        ["EnumHolderV2"] = Msg<EnumHolderV2>(),
        ["UnionHolderV1"] = Msg<UnionHolderV1>(),
        ["UnionHolderV2"] = Msg<UnionHolderV2>(),
        ["GrowHolderV1"] = Msg<GrowHolderV1>(),
        ["GrowHolderV2"] = Msg<GrowHolderV2>(),
        ["SetHolderV1"] = Msg<SetHolderV1>(),
        ["SetHolderV2"] = Msg<SetHolderV2>(),
        ["NestV1"] = Msg<NestV1>(),
        ["NestV2"] = Msg<NestV2>(),
        ["TreeV1"] = Msg<TreeV1>(),

        ["Array<AppendedV1>"] = Arr<AppendedV1>(),
        ["Array<AppendedV2>"] = Arr<AppendedV2>(),
        ["Array<ShiftedV1>"] = Arr<ShiftedV1>(),
        ["Array<TierV1>"] = Arr<TierV1>(),
        ["Array<i4>"] = Arr<i4>(),

        ["Fixed<AppendedV1,2>"] = Fix<AppendedV1>(2),
        ["Fixed<AppendedV2,2>"] = Fix<AppendedV2>(2),
        ["Fixed<ShiftedV1,2>"] = Fix<ShiftedV1>(2),
        ["Fixed<i4,3>"] = Fix<i4>(3),

        ["Map<string,AppendedV1>"] = MapOf<string, AppendedV1>(),
        ["Map<string,AppendedV2>"] = MapOf<string, AppendedV2>(),
        ["Map<string,ShiftedV1>"] = MapOf<string, ShiftedV1>(),
        ["Map<string,i4>"] = MapOf<string, i4>(),

        ["Set<TierV1>"] = SetOf<TierV1>(),
        ["Set<TierV2>"] = SetOf<TierV2>(),

        ["Maybe<AppendedV1>"] = MaybeOf<AppendedV1>(),
        ["Maybe<AppendedV2>"] = MaybeOf<AppendedV2>(),

        ["Union<ShapeV1>"] = Msg<IShapeV1>(),
        ["Union<ShapeV2>"] = Msg<IShapeV2>(),
        ["Union<GrowV1>"] = Msg<IGrowV1>(),
        ["Union<GrowV2>"] = Msg<IGrowV2>(),

        ["Args<AppendedV1>"] = Args(Msg<AppendedV1>()),
        ["Args<AppendedV2>"] = Args(Msg<AppendedV2>()),
        ["Args<ShiftedV1>"] = Args(Msg<ShiftedV1>()),
        ["Args<AppendedV2,i4>"] = Args(Msg<AppendedV2>(), Msg<i4>()),
        ["Args<NestV1,i4>"] = Args(Msg<NestV1>(), Msg<i4>()),
        ["Args<NestV2,i4>"] = Args(Msg<NestV2>(), Msg<i4>()),
    };

    private static Codec CodecFor(string spec) =>
        Codecs.TryGetValue(spec, out var c)
            ? c
            : throw new InvalidOperationException($"unknown reader spec '{spec}'");
}
