using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Khors.Core.Storage;

public enum StorageLoadStatus
{
    /// <summary>Прочитано.</summary>
    Ok,

    /// <summary>Файла нет — начинаем с пустого.</summary>
    Missing,

    /// <summary>Файл не читается (не JSON, не та структура, нет schemaVersion).</summary>
    Corrupt,

    /// <summary>Файл от более новой версии KHORS — не читаем и не перезаписываем.</summary>
    FutureVersion,
}

/// <summary>Результат разбора версионированного JSON.</summary>
public sealed record VersionedParseResult<T>(T? Value, StorageLoadStatus Status, int? SchemaVersion)
    where T : class;

/// <summary>
/// Разбор JSON с полем <c>schemaVersion</c> в корне и пошаговой миграцией старых версий.
/// Миграция <c>n → n+1</c> работает над <see cref="JsonObject"/> — чистая функция.
/// </summary>
public static class VersionedJson
{
    private static readonly JsonDocumentOptions s_documentOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static VersionedParseResult<T> Parse<T>(
        string json,
        int currentVersion,
        JsonTypeInfo<T> typeInfo,
        IReadOnlyDictionary<int, Func<JsonObject, JsonObject>>? migrations = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(typeInfo);

        try
        {
            if (JsonNode.Parse(json, documentOptions: s_documentOptions) is not JsonObject root
                || root["schemaVersion"] is not JsonValue versionNode
                || !versionNode.TryGetValue<int>(out var version)
                || version < 1)
            {
                return new(null, StorageLoadStatus.Corrupt, null);
            }

            if (version > currentVersion)
            {
                return new(null, StorageLoadStatus.FutureVersion, version);
            }

            for (var v = version; v < currentVersion; v++)
            {
                if (migrations is null || !migrations.TryGetValue(v, out var migrate))
                {
                    return new(null, StorageLoadStatus.Corrupt, version);
                }

                root = migrate(root);
                root["schemaVersion"] = v + 1;
            }

            var value = root.Deserialize(typeInfo);
            return value is null ? new(null, StorageLoadStatus.Corrupt, version) : new(value, StorageLoadStatus.Ok, version);
        }
        catch (JsonException)
        {
            return new(null, StorageLoadStatus.Corrupt, null);
        }
    }
}
