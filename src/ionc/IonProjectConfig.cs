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

    [JsonPropertyName("generators")]
    public required Dictionary<IonGeneratorPlatform, IonPlatformConfig> Generators { get; init; }

    public static string ToJson(IonProjectConfig config, bool indented = true)
        => JsonSerializer.Serialize(config, JsonOptions(indented));

    public static IonProjectConfig FromJson(string json)
        => JsonSerializer.Deserialize<IonProjectConfig>(json, JsonOptions())
           ?? throw new ValidationException("JSON is null or invalid for ArgonConfig.");

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
        options.Converters.Add(new PlatformKeyConverter());
        options.Converters.Add(new IonPlatformConfigConverter());
        return options;
    }
}

[JsonConverter(typeof(FeatureJsonConverter))]
public enum IonGeneratorFeature
{
    Orleans,
    Std,
    Vector
}

internal sealed class FeatureJsonConverter : JsonConverter<IonGeneratorFeature>
{
    public override IonGeneratorFeature Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Feature must be a string.");

        return reader.GetString() switch
        {
            "orleans" => IonGeneratorFeature.Orleans,
            "std" => IonGeneratorFeature.Std,
            "vector" => IonGeneratorFeature.Vector,
            _ => throw new JsonException("Invalid feature. Allowed: 'orleans','std','vector'.")
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

[JsonConverter(typeof(PlatformKeyConverter))]
public enum IonGeneratorPlatform
{
    Dotnet,
    Browser,
    Rust,
    Go
}
internal sealed class PlatformKeyConverter : JsonConverter<IonGeneratorPlatform>
{
    public override IonGeneratorPlatform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? Parse(reader.GetString())
            : throw new JsonException("Platform key must be a string.");

    public override void Write(Utf8JsonWriter writer, IonGeneratorPlatform value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToString(value));

    public override IonGeneratorPlatform ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Parse(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, IonGeneratorPlatform value, JsonSerializerOptions options)
        => writer.WritePropertyName(ToString(value));

    private static IonGeneratorPlatform Parse(string s) => s switch
    {
        "dotnet" => IonGeneratorPlatform.Dotnet,
        "browser" => IonGeneratorPlatform.Browser,
        "rust" => IonGeneratorPlatform.Rust,
        "go" => IonGeneratorPlatform.Go,
        _ => throw new JsonException($"Invalid platform key '{s}'. Allowed: 'dotnet','browser','rust','go'.")
    };

    private static string ToString(IonGeneratorPlatform value) => value switch
    {
        IonGeneratorPlatform.Dotnet => "dotnet",
        IonGeneratorPlatform.Browser => "browser",
        IonGeneratorPlatform.Rust => "rust",
        IonGeneratorPlatform.Go => "go",
        _ => throw new JsonException($"Unknown Platform: {value}")
    };
}

public abstract record IonPlatformConfig;

public sealed record DotnetGeneratorConfig : IonPlatformConfig
{
    [JsonPropertyName("features")] public required HashSet<DotnetFeature> Features { get; init; }

    [JsonPropertyName("outputs")] public required string Outputs { get; init; }
}

public sealed record BrowserGeneratorConfig : IonPlatformConfig
{
    [JsonPropertyName("outputFile")] public required string OutputFile { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DotnetFeature
{
    Server,
    Client,
    Models
}
internal sealed class IonPlatformConfigConverter : JsonConverter<IonPlatformConfig>
{
    public override IonPlatformConfig? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.TryGetProperty("outputs", out _))
        {
            return JsonSerializer.Deserialize<DotnetGeneratorConfig>(root.GetRawText(), options);
        }
        else if (root.TryGetProperty("outputFile", out _))
        {
            return JsonSerializer.Deserialize<BrowserGeneratorConfig>(root.GetRawText(), options);
        }
        else
        {
            throw new JsonException("Unknown platform config format.");
        }
    }

    public override void Write(Utf8JsonWriter writer, IonPlatformConfig value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case DotnetGeneratorConfig d:
                JsonSerializer.Serialize(writer, d, options);
                break;
            case BrowserGeneratorConfig b:
                JsonSerializer.Serialize(writer, b, options);
                break;
            default:
                throw new JsonException($"Unsupported platform config type: {value.GetType().Name}");
        }
    }
}