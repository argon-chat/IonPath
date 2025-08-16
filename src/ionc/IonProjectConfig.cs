namespace ion.compiler;

using Microsoft.Build.Framework;
using runtime;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record IonProjectConfig
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("features")] public required HashSet<IonGeneratorFeature> Features { get; init; }

    [JsonPropertyName("generators")] public List<IonGeneratorConfig> Generators { get; init; } = new List<IonGeneratorConfig>();

    public static string ToJson(IonProjectConfig config, bool indented = true)
        => JsonSerializer.Serialize(config, JsonOptions(indented));

    public static IonProjectConfig FromJson(string json)
    {
        var cfg = JsonSerializer.Deserialize<IonProjectConfig>(json, JsonOptions(true))
                  ?? throw new ValidationException("JSON is null or invalid for ArgonConfig.");
        return cfg;
    }

    public static JsonSerializerOptions JsonOptions(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        options.Converters.Add(new FeatureJsonConverter());
        options.Converters.Add(new GeneratorTypeJsonConverter());
        options.Converters.Add(new PlatformJsonConverter());
        return options;
    }
}

public sealed record IonGeneratorConfig
{
    [JsonPropertyName("type")] public required IonGeneratorType Type { get; init; }

    [JsonPropertyName("platform")] public required IonGeneratorPlatform Platform { get; init; }

    [JsonPropertyName("output")] public required string Output { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Output))
            throw new ValidationException("Generator 'output' must be a non-empty string.");
    }
}

[JsonConverter(typeof(FeatureJsonConverter))]
public enum IonGeneratorFeature
{
    Orleans,
    Std,
    Vector
}

[JsonConverter(typeof(GeneratorTypeJsonConverter))]
public enum IonGeneratorType
{
    Server,
    Client
}

[JsonConverter(typeof(PlatformJsonConverter))]
public enum IonGeneratorPlatform
{
    Dotnet,
    Browser,
    Rust,
    Go
}

internal sealed class FeatureJsonConverter : JsonConverter<IonGeneratorFeature>
{
    public override IonGeneratorFeature Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Feature must be a string.");

        return reader.GetString() switch
        {
            "orleans" => IonGeneratorFeature.Orleans,
            "std" => IonGeneratorFeature.Std,
            "vector" => IonGeneratorFeature.Vector,
            _ => throw new JsonException("Invalid feature. Allowed: 'orleans', 'std', 'vector'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, IonGeneratorFeature value, JsonSerializerOptions options)
    {
        var s = value switch
        {
            IonGeneratorFeature.Orleans => "orleans",
            IonGeneratorFeature.Std => "std",
            IonGeneratorFeature.Vector => "vector",
            _ => throw new JsonException($"Unknown Feature: {value}")
        };
        writer.WriteStringValue(s);
    }
}

internal sealed class GeneratorTypeJsonConverter : JsonConverter<IonGeneratorType>
{
    public override IonGeneratorType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Generator.type must be a string.");

        return reader.GetString() switch
        {
            "server" => IonGeneratorType.Server,
            "client" => IonGeneratorType.Client,
            _ => throw new JsonException("Invalid generator.type. Allowed: 'server', 'client'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, IonGeneratorType value, JsonSerializerOptions options)
    {
        var s = value switch
        {
            IonGeneratorType.Server => "server",
            IonGeneratorType.Client => "client",
            _ => throw new JsonException($"Unknown GeneratorType: {value}")
        };
        writer.WriteStringValue(s);
    }
}

internal sealed class PlatformJsonConverter : JsonConverter<IonGeneratorPlatform>
{
    public override IonGeneratorPlatform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Generator.platform must be a string.");

        return reader.GetString() switch
        {
            "dotnet" => IonGeneratorPlatform.Dotnet,
            "browser" => IonGeneratorPlatform.Browser,
            "rust" => IonGeneratorPlatform.Rust,
            "go" => IonGeneratorPlatform.Go,
            _ => throw new JsonException("Invalid generator.platform. Allowed: 'dotnet', 'browser', 'rust', 'go'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, IonGeneratorPlatform value, JsonSerializerOptions options)
    {
        var s = value switch
        {
            IonGeneratorPlatform.Dotnet => "dotnet",
            IonGeneratorPlatform.Browser => "browser",
            IonGeneratorPlatform.Rust => "rust",
            IonGeneratorPlatform.Go => "go",
            _ => throw new JsonException($"Unknown Platform: {value}")
        };
        writer.WriteStringValue(s);
    }
}