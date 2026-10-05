using Khors.Core.Profiles;

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
/// Чистая функция: Id и время передаются снаружи.
/// </summary>
public static class ProfileImporter
{
    private static readonly char[] s_separators = [' ', '\t', '\r', '\n'];

    public static ImportResult Import(string text, IReadOnlyCollection<Profile> existing, Func<Guid> newId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(newId);

        var known = existing.Select(Identity).ToHashSet();
        var added = new List<Profile>();
        var errors = new List<ImportLineError>();
        var duplicates = 0;

        var links = text.Split(s_separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < links.Length; i++)
        {
            var result = ShareLinkParser.Parse(links[i]);
            if (!result.IsSuccess)
            {
                errors.Add(new ImportLineError(i + 1, result.Error));
                continue;
            }

            if (!known.Add(Identity(result.Profile)))
            {
                duplicates++;
                continue;
            }

            added.Add(result.Profile with { Id = newId(), UpdatedAt = now });
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
