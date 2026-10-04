using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Khors.Core.Diagnostics;

public sealed partial class SecretMasker
{
    private static readonly JsonDocumentOptions s_documentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Отчёт — файл, а не HTML: кириллицу и маски оставляем читаемыми.
    private static readonly JsonSerializerOptions s_indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions s_compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Маскирует JSON (конфиги Xray и sing-box, профили, подписки) по именам полей:
    /// id/uuid, пароли, ключи REALITY и WireGuard, short id, адреса серверов и SNI.
    /// Остальные строковые значения маскируются как текст (<see cref="MaskText"/>).
    /// Если строка не является JSON, она маскируется как текст.
    /// </summary>
    public string MaskJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return MaskJson(json, indented: true);
    }

    private string MaskJson(string json, bool indented)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: s_documentOptions);
        }
        catch (JsonException)
        {
            return MaskText(json);
        }

        if (root is null)
        {
            return json;
        }

        root = MaskNode(root, inheritedKind: null);
        return root.ToJsonString(indented ? s_indented : s_compact);
    }

    private JsonNode MaskNode(JsonNode node, SecretKind? inheritedKind)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is not null)
                    {
                        obj[name] = MaskChild(child, KindForKey(name, json: true));
                    }
                }

                return obj;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } item)
                    {
                        array[i] = MaskChild(item, inheritedKind);
                    }
                }

                return array;

            default:
                return MaskChild(node, inheritedKind);
        }
    }

    private JsonNode MaskChild(JsonNode child, SecretKind? kind)
    {
        if (child is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var masked = kind switch
            {
                null => MaskText(text),
                SecretKind.Host => MaskHostValue(text),
                _ => Mask(kind.Value, text),
            };
            return JsonValue.Create(masked);
        }

        // Вид поля применяется к строкам и массивам строк (shortIds: [...]), но не к вложенным объектам.
        return child switch
        {
            JsonArray => MaskNode(child.DeepClone(), kind),
            JsonObject => MaskNode(child.DeepClone(), inheritedKind: null),
            _ => child.DeepClone(),
        };
    }
}
