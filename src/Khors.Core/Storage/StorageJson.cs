using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Khors.Core.Profiles;
using Khors.Core.Subscriptions;

namespace Khors.Core.Storage;

/// <summary>Сериализация файлов профилей и настроек.</summary>
public static class StorageJson
{
    /// <summary>Миграции файла профилей: ключ — исходная версия.</summary>
    public static IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> ProfileMigrations { get; } = new Dictionary<int, Func<JsonObject, JsonObject>>
    {
        // 1 → 2: появились подписки.
        [1] = root =>
        {
            root["subscriptions"] ??= new JsonArray();
            return root;
        },
    };

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
[JsonSerializable(typeof(Subscription[]))]
[JsonSerializable(typeof(UnknownParam[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int[]))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
