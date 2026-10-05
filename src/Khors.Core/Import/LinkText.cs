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
        var links = Split(text);
        for (var i = 0; i < links.Count; i++)
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

    /// <summary>
    /// Ссылки по строкам; внутри строки новая ссылка начинается только со «схема://». Слово без «://»
    /// продолжает имя предыдущей ссылки: некоторые клиенты копируют имя (<c>#Germany 1</c>) с пробелом.
    /// </summary>
    internal static List<string> Split(string text)
    {
        var links = new List<string>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var startOfLine = true;
            foreach (var word in line.Split(s_separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!startOfLine && !word.Contains("://", StringComparison.Ordinal))
                {
                    links[^1] += " " + word;
                }
                else
                {
                    links.Add(word);
                }

                startOfLine = false;
            }
        }

        return links;
    }
}
