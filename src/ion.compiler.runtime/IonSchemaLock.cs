namespace ion.runtime;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Root model for ion.lock.json — captures the wire-level layout of all definitions
/// so that breaking changes can be detected across compilations.
/// </summary>
public sealed record IonSchemaLock
{
    /// <summary>
    /// The lock document shape this toolchain writes, and the only one it can validate against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>2</b> — <see cref="IonLockedUnionCase.Fields"/>. A union case used to be recorded as
    /// <c>{index, name}</c> alone, so a case's entire payload field list was unversioned: turning
    /// <c>A(x: i4, z: i4)</c> into <c>A(x: i4, y: i4, z: i4)</c> re-indexes the positional array the
    /// case encodes to, and nothing in the toolchain could observe it. A v1 document cannot express
    /// that list, so it cannot be validated against — <c>SchemaLockValidationStage</c> reports
    /// ION0069 rather than checking what it can and silently passing what it cannot.
    /// </para>
    /// <para><b>1</b> — the original shape.</para>
    /// <para>
    /// Bump this only when the document gains information a validator <em>needs</em>, because every
    /// bump turns existing lock files into an ION0069 error that only <c>ionc lock update</c> clears.
    /// Purely informational additions do not warrant one — an unknown property is ignored on read.
    /// </para>
    /// </remarks>
    public const int CurrentVersion = 2;

    public const string FileName = "ion.lock.json";

    [JsonPropertyName("version")] public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("module")] public required string Module { get; init; }

    [JsonPropertyName("definitions")]
    public required Dictionary<string, IonLockedDefinition> Definitions { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Why this document could not be read, or <see langword="null"/> when it was read fine.
    /// </summary>
    /// <remarks>
    /// <see cref="TryLoadFrom"/> hands back an unreadable lock carrying this instead of throwing or
    /// returning <see langword="null"/>. Both alternatives were wrong. A throw crashes the compiler
    /// on a file the user can edit — ion.lock.json is user input like any other. A
    /// <see langword="null"/> is indistinguishable from "there is no lock file", so a corrupt
    /// ion.lock.json would silently re-baseline to whatever the schema happens to say today, which
    /// is the one outcome a lock exists to prevent. <c>SchemaLockValidationStage</c> turns a
    /// non-null value here into an ION0069 error, so the failure is a positioned diagnostic and the
    /// build stops.
    /// <para>Not serialized: it describes one load attempt, not the contract.</para>
    /// </remarks>
    [JsonIgnore] public string? LoadError { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static IonSchemaLock FromJson(string json)
        => JsonSerializer.Deserialize<IonSchemaLock>(json, SerializerOptions)
           ?? throw new InvalidOperationException("the file contains a bare 'null'");

    /// <summary>
    /// Loads <c>ion.lock.json</c> from <paramref name="directory"/>.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> only when there is no lock file at all. A file that exists but cannot
    /// be read or parsed comes back as a lock whose <see cref="LoadError"/> is set — never as
    /// <see langword="null"/>, so a caller cannot mistake it for an unlocked project.
    /// </returns>
    public static IonSchemaLock? TryLoadFrom(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
            return null;

        try
        {
            return FromJson(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            return Unreadable(ex.Message);
        }
    }

    /// <summary>A stand-in for a lock file that is present but could not be read.</summary>
    public static IonSchemaLock Unreadable(string reason) => new()
    {
        Version = 0,
        Module = string.Empty,
        Definitions = new Dictionary<string, IonLockedDefinition>(),
        LoadError = reason
    };

    public void SaveTo(string directory)
    {
        var path = Path.Combine(directory, FileName);
        File.WriteAllText(path, ToJson());
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<IonLockedDefinitionKind>))]
public enum IonLockedDefinitionKind
{
    Msg,
    Service,
    Enum,
    Flags,
    Union,
    Typedef
}

public sealed record IonLockedDefinition
{
    [JsonPropertyName("kind")] public required IonLockedDefinitionKind Kind { get; init; }

    /// <summary>Next available field index for messages/unions (used for safe append).</summary>
    [JsonPropertyName("nextIndex")] public int? NextIndex { get; init; }

    /// <summary>Locked fields for msg definitions (positional CBOR array encoding).</summary>
    [JsonPropertyName("fields")] public List<IonLockedField>? Fields { get; init; }

    /// <summary>Locked methods for service definitions.</summary>
    [JsonPropertyName("methods")] public Dictionary<string, IonLockedMethod>? Methods { get; init; }

    /// <summary>Locked members for enum/flags definitions.</summary>
    [JsonPropertyName("members")] public Dictionary<string, string>? Members { get; init; }

    /// <summary>Base type name for enum/flags.</summary>
    [JsonPropertyName("baseType")] public string? BaseType { get; init; }

    /// <summary>Locked union cases (index-based discriminator).</summary>
    [JsonPropertyName("cases")] public List<IonLockedUnionCase>? Cases { get; init; }

    /// <summary>Shared fields for union types.</summary>
    [JsonPropertyName("sharedFields")] public List<IonLockedField>? SharedFields { get; init; }
}

public sealed record IonLockedField
{
    [JsonPropertyName("index")] public required int Index { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("type")] public required string Type { get; init; }
}

public sealed record IonLockedMethod
{
    [JsonPropertyName("args")] public required List<IonLockedMethodArg> Args { get; init; }

    [JsonPropertyName("returns")] public required string Returns { get; init; }

    [JsonPropertyName("modifiers")] public required List<string> Modifiers { get; init; }
}

public sealed record IonLockedMethodArg
{
    [JsonPropertyName("index")] public required int Index { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("type")] public required string Type { get; init; }

    [JsonPropertyName("modifier")] public string? Modifier { get; init; }
}

public sealed record IonLockedUnionCase
{
    [JsonPropertyName("index")] public required int Index { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>
    /// The canonical type name of a <c>case Foo</c> reference; <see langword="null"/> for a case
    /// that declares its payload inline, which is the form <see cref="Fields"/> describes.
    /// </summary>
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>
    /// The payload field list of an inline case, in wire order — the reason this document is v2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A union case is not a leaf on the wire. Its payload is a positional array of its own, so
    /// <c>{index, name}</c> pinned the discriminator and left everything the discriminator selects
    /// unversioned. <c>A(x: i4, z: i4)</c> to <c>A(x: i4, y: i4, z: i4)</c> is exactly the silent
    /// corruption that inserting a field into a <c>msg</c> is, and it passed a lock check clean.
    /// </para>
    /// <para>
    /// <see cref="IonLockedField.Index"/> is the index in the <em>encoded</em> array, so in a union
    /// with shared fields it starts at the shared field count. The shared prefix is recorded once in
    /// <see cref="IonLockedDefinition.SharedFields"/> rather than copied into every case — the IR
    /// does copy it into each case's field list, but writing it out N times would report one edit to
    /// a shared field N+1 times — while the indices still say where each case field actually lands.
    /// </para>
    /// <para>
    /// <see langword="null"/> for a <c>case Foo</c> reference, whose fields are already covered by
    /// the referenced definition's own lock entry, and for every case in a v1 document — which is
    /// precisely why a v1 document is rejected rather than partially trusted. An empty list is an
    /// inline case with no payload, which is a different thing from "not recorded".
    /// </para>
    /// </remarks>
    [JsonPropertyName("fields")] public List<IonLockedField>? Fields { get; init; }
}
