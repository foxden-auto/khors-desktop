using System.Text.Json;
using System.Text.Json.Serialization;

namespace Khors.Core.Profiles;

/// <summary>
/// Секретное значение профиля (UUID, пароль, ключ). <see cref="ToString"/> не раскрывает значение,
/// поэтому секрет не попадёт в лог через автоматический <c>ToString()</c> записей (CLAUDE.md, правило 5).
/// В JSON записывается настоящее значение — файл профилей не лог.
/// </summary>
[JsonConverter(typeof(SecretJsonConverter))]
public sealed record Secret
{
    public Secret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public string Value { get; }

    public bool IsEmpty => Value.Length == 0;

    public override string ToString() => "***";
}

internal sealed class SecretJsonConverter : JsonConverter<Secret>
{
    public override Secret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("Secret value must be a string."));

    public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
