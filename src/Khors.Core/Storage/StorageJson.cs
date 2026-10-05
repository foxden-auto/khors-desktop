using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Khors.Core.Profiles;

namespace Khors.Core.Storage;

/// <summary>Сериализация файлов профилей и настроек.</summary>
public static class StorageJson
{
    /// <summary>Миграции файла профилей: ключ — исходная версия. Пока нет.</summary>
    public static IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> ProfileMigrations { get; } = new Dictionary<int, Func<JsonObject, JsonObject>>();

    public static string SerializeProfiles(ProfileDocument document) =>
        JsonSerializer.Serialize(document, StorageJsonContext.Default.ProfileDocument);

    public static VersionedParseResult<ProfileDocument> ParseProfiles(string json) =>
        VersionedJson.Parse(json, ProfileDocument.CurrentSchemaVersion, StorageJsonContext.Default.ProfileDocument, ProfileMigrations);

    public static string SerializeSettings(AppSettings settings) =>
        JsonSerializer.Serialize(settings, StorageJsonContext.Default.AppSettings);

    public static VersionedParseResult<AppSettings> ParseSettings(string json) =>
        VersionedJson.Parse(json, AppSettings.CurrentSchemaVersion, StorageJsonContext.Default.AppSettings);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ProfileDocument))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Profile[]))]
[JsonSerializable(typeof(UnknownParam[]))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
