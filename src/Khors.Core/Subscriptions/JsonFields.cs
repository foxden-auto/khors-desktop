using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Khors.Core.Profiles;

namespace Khors.Core.Subscriptions;

/// <summary>
/// Поля JSON-объекта конфига: чтение с отметкой «использовано»; всё неиспользованное становится
/// неизвестными параметрами с путём через точку (<c>streamSettings.sockopt.mark</c>).
/// </summary>
internal sealed class JsonFields(JsonObject node, string prefix = "")
{
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);
    private readonly List<JsonFields> _children = [];

    public JsonNode? Take(string key)
    {
        _taken.Add(key);
        return node.TryGetPropertyValue(key, out var value) ? value : null;
    }

    /// <summary>Пометить поля использованными без чтения (служебные поля, которые KHORS осознанно не переносит).</summary>
    public void Ignore(params string[] keys)
    {
        foreach (var key in keys)
        {
            _taken.Add(key);
        }
    }

    public string? String(string key) => Take(key) is JsonValue value ? Text(value) : null;

    public int? Int(string key) => Take(key) is JsonValue value
        && (value.TryGetValue<int>(out var number) || int.TryParse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            ? number
            : null;

    public bool? Bool(string key) => Take(key) is JsonValue value
        ? value.TryGetValue<bool>(out var flag) ? flag : Text(value)?.ToLowerInvariant() switch { "true" => true, "false" => false, _ => null }
        : null;

    /// <summary>Массив строк или одна строка.</summary>
    public string[] Strings(string key) => Take(key) switch
    {
        JsonArray array => [.. array.OfType<JsonValue>().Select(Text).OfType<string>()],
        JsonValue single when Text(single) is { } text => [text],
        _ => [],
    };

    public JsonFields? Object(string key)
    {
        if (Take(key) is not JsonObject child)
        {
            return null;
        }

        var fields = new JsonFields(child, prefix + key + ".");
        _children.Add(fields);
        return fields;
    }

    /// <summary>Первый объект массива (<c>vnext[0]</c>, <c>users[0]</c>).</summary>
    public JsonFields? FirstOf(string key)
    {
        if (Take(key) is not JsonArray { Count: > 0 } array || array[0] is not JsonObject first)
        {
            return null;
        }

        var fields = new JsonFields(first, prefix + key + "[0].");
        _children.Add(fields);
        return fields;
    }

    /// <summary>Значение как исходный JSON (для <c>extra</c> XHTTP, который хранится строкой).</summary>
    public string? Raw(string key) => Take(key) is { } value ? value.ToJsonString() : null;

    public IEnumerable<UnknownParam> Remaining()
    {
        foreach (var (key, value) in node)
        {
            if (!_taken.Contains(key) && value is not null)
            {
                yield return new UnknownParam(prefix + key, value is JsonValue scalar ? Text(scalar) ?? string.Empty : value.ToJsonString());
            }
        }

        foreach (var child in _children)
        {
            foreach (var param in child.Remaining())
            {
                yield return param;
            }
        }
    }

    private static string? Text(JsonValue value) => value.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>() is { Length: > 0 } text ? text : null,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
        _ => null,
    };
}
