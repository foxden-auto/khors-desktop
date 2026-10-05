using Khors.Core.Profiles;
using Khors.Core.Subscriptions;
using Khors.Core.Text;

namespace Khors.Core.Import;

/// <summary>Итог импорта текста со ссылками.</summary>
/// <param name="Added">Новые профили (с назначенными Id и датой).</param>
/// <param name="Duplicates">Сколько ссылок совпали с уже имеющимися профилями (или между собой) и пропущены.</param>
/// <param name="Errors">Нераспознанные строки — номер и причина, без текста ссылки (в нём секреты).</param>
public sealed record ImportResult(EquatableArray<Profile> Added, int Duplicates, EquatableArray<ImportLineError> Errors);

/// <param name="Line">Номер ссылки во входном тексте, с 1.</param>
public sealed record ImportLineError(int Line, LinkParseError Error);

/// <summary>
/// Импорт одной или нескольких ссылок (каждая с новой строки или через пробел) — например, из буфера обмена.
/// Понимает и содержимое подписки в base64, и конфиг целиком (Clash/mihomo YAML, sing-box или Xray JSON).
/// Чистая функция: Id и время передаются снаружи.
/// </summary>
public static class ProfileImporter
{
    public static ImportResult Import(string text, IReadOnlyCollection<Profile> existing, Func<Guid> newId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(newId);

        // Содержимое подписки, скопированное целиком: base64-блок без «://» внутри.
        if (!LinkText.LooksLikeLinks(text) && Base64Text.TryDecodeUtf8(text.Trim(), out var decoded) && LinkText.LooksLikeLinks(decoded))
        {
            text = decoded;
        }

        var known = existing.Select(Identity).ToHashSet();
        var added = new List<Profile>();
        var duplicates = 0;

        // Конфиг целиком (Clash/mihomo YAML, sing-box или Xray JSON) — разбирается как содержимое подписки.
        var content = SubscriptionContent.Parse(text);
        var (parsed, errors) = content.Format is SubscriptionFormat.ClashYaml or SubscriptionFormat.SingBoxJson or SubscriptionFormat.XrayJson
            ? ([.. content.Profiles], [.. content.Errors])
            : LinkText.Parse(text);
        foreach (var profile in parsed)
        {
            if (!known.Add(Identity(profile)))
            {
                duplicates++;
                continue;
            }

            added.Add(profile with { Id = newId(), UpdatedAt = now });
        }

        return new ImportResult(new EquatableArray<Profile>(added), duplicates, new EquatableArray<ImportLineError>(errors));
    }

    /// <summary>
    /// Что делает профиль «тем же самым» подключением: всё, кроме имени, группы и служебных полей.
    /// </summary>
    public static Profile Identity(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile with { Id = Guid.Empty, Name = string.Empty, Group = null, SubscriptionId = null, UpdatedAt = default };
    }
}
