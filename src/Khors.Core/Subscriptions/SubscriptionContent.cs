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
}

/// <param name="Profiles">Профили подписки без Id (назначаются при слиянии), без повторов.</param>
public sealed record SubscriptionContentResult(SubscriptionFormat Format, EquatableArray<Profile> Profiles, EquatableArray<ImportLineError> Errors);

/// <summary>Разбор тела ответа подписки. Чистая функция.</summary>
public static class SubscriptionContent
{
    public static SubscriptionContentResult Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var text = body.Trim().TrimStart('﻿');

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

    private static SubscriptionContentResult FromLinks(SubscriptionFormat format, string text)
    {
        var (profiles, errors) = LinkText.Parse(text);
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
