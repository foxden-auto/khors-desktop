using Khors.Core.Profiles;

namespace Khors.Core.Import;

/// <summary>Текст со ссылками: по одной на строке или через пробелы.</summary>
internal static class LinkText
{
    private static readonly char[] s_separators = [' ', '\t', '\r', '\n'];

    public static bool LooksLikeLinks(string text) => text.Contains("://", StringComparison.Ordinal);

    /// <summary>Разбирает все ссылки; ошибки — по номеру ссылки во входе, без её текста.</summary>
    public static (List<Profile> Profiles, List<ImportLineError> Errors) Parse(string text)
    {
        var profiles = new List<Profile>();
        var errors = new List<ImportLineError>();
        var links = text.Split(s_separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < links.Length; i++)
        {
            var result = ShareLinkParser.Parse(links[i]);
            if (result.IsSuccess)
            {
                profiles.Add(result.Profile);
            }
            else
            {
                errors.Add(new ImportLineError(i + 1, result.Error));
            }
        }

        return (profiles, errors);
    }
}
