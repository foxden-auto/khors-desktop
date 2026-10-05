using System.Text.Json;
using System.Text.Json.Serialization;

namespace Khors.Core.Profiles;

/// <summary>JSON-представление профиля. Версия схемы хранится в корне файла профилей (ROADMAP 1.7).</summary>
public static class ProfileJson
{
    public static string Serialize(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Serialize(profile, ProfileJsonContext.Default.Profile);
    }

    /// <exception cref="JsonException">JSON не соответствует модели профиля.</exception>
    public static Profile Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, ProfileJsonContext.Default.Profile)
            ?? throw new JsonException("Profile JSON is null.");
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(UnknownParam[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int[]))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;
