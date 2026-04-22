namespace ion.runtime;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Root model for ion.lock.json — captures the wire-level layout of all definitions
/// so that breaking changes can be detected across compilations.
/// </summary>
public sealed record IonSchemaLock
{
    public const int CurrentVersion = 1;
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

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static IonSchemaLock FromJson(string json)
        => JsonSerializer.Deserialize<IonSchemaLock>(json, SerializerOptions)
           ?? throw new InvalidOperationException("Failed to deserialize ion.lock.json");

    public static IonSchemaLock? TryLoadFrom(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
            return null;
        var json = File.ReadAllText(path);
        return FromJson(json);
    }

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

    [JsonPropertyName("type")] public string? Type { get; init; }
}
