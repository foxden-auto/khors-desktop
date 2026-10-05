using System.Text.Json;
using System.Text.Json.Nodes;
using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Core.Text;

namespace Khors.Core.Subscriptions;

public enum SubscriptionFormat
{
    Unknown,

    /// <summary>Ссылки по одной на строке.</summary>
    LinkList,

    /// <summary>Тот же список, закодированный в base64 (самый распространённый формат панелей).</summary>
    Base64LinkList,

    /// <summary>YAML Clash / mihomo с разделом <c>proxies</c>.</summary>
    ClashYaml,

    /// <summary>Конфиг sing-box (выходы с полем <c>type</c>).</summary>
    SingBoxJson,

    /// <summary>Конфиг Xray или массив конфигов (выходы с полем <c>protocol</c>).</summary>
    XrayJson,
}

/// <param name="Profiles">Профили подписки без Id (назначаются при слиянии), без повторов.</param>
public sealed record SubscriptionContentResult(SubscriptionFormat Format, EquatableArray<Profile> Profiles, EquatableArray<ImportLineError> Errors);

/// <summary>Разбор тела ответа подписки. Чистая функция.</summary>
public static class SubscriptionContent
{
    public static SubscriptionContentResult Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var text = body.Trim().TrimStart('\uFEFF');

        if (text.StartsWith('{') || text.StartsWith('['))
        {
            if (TryParseJson(text) is { } json)
            {
                return json;
            }
        }

        // YAML Clash проверяем первым: в нём бывают «://» (адреса наборов правил), а список ссылок — тоже валидный YAML.
        if (text.Contains("proxies", StringComparison.Ordinal) && ClashYaml.TryParse(text, out var clashProfiles, out var clashErrors))
        {
            return Unique(SubscriptionFormat.ClashYaml, clashProfiles, clashErrors);
        }

        if (LinkText.LooksLikeLinks(text))
        {
            return FromLinks(SubscriptionFormat.LinkList, text);
        }

        if (Base64Text.TryDecodeUtf8(text, out var decoded) && LinkText.LooksLikeLinks(decoded))
        {
            return FromLinks(SubscriptionFormat.Base64LinkList, decoded);
        }

        return new SubscriptionContentResult(SubscriptionFormat.Unknown, default, default);
    }

    private static SubscriptionContentResult? TryParseJson(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }

        var profiles = new List<Profile>();
        var errors = new List<ImportLineError>();
        if (root is not null && XrayJson.IsXray(root))
        {
            XrayJson.Parse(root, profiles, errors);
            return Unique(SubscriptionFormat.XrayJson, profiles, errors);
        }

        if (root is JsonObject singBox && SingBoxJson.IsSingBox(singBox))
        {
            SingBoxJson.Parse(singBox, profiles, errors);
            return Unique(SubscriptionFormat.SingBoxJson, profiles, errors);
        }

        return null;
    }

    private static SubscriptionContentResult FromLinks(SubscriptionFormat format, string text)
    {
        var (profiles, errors) = LinkText.Parse(text);
        return Unique(format, profiles, errors);
    }

    private static SubscriptionContentResult Unique(SubscriptionFormat format, List<Profile> profiles, List<ImportLineError> errors)
    {
        var unique = new List<Profile>();
        var seen = new HashSet<Profile>();
        foreach (var profile in profiles)
        {
            if (seen.Add(ProfileImporter.Identity(profile)))
            {
                unique.Add(profile);
            }
        }

        return new SubscriptionContentResult(format, new EquatableArray<Profile>(unique), new EquatableArray<ImportLineError>(errors));
    }
}
