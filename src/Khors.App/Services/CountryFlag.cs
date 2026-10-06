using System.Text;

namespace Khors.App.Services;

/// <summary>
/// Флаг страны в имени профиля («🇩🇪 Германия») — два символа regional indicator. В Windows флаги-эмодзи
/// рисуются буквами, поэтому окно показывает код страны отдельной плашкой, а имя — без флага.
/// </summary>
public static class CountryFlag
{
    private const int RegionalIndicatorA = 0x1F1E6;
    private const int RegionalIndicatorZ = 0x1F1FF;

    /// <summary>Код страны первого флага («DE») и имя без него; флага нет — <c>(null, name)</c>.</summary>
    public static (string? Code, string Name) Split(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var runes = name.EnumerateRunes().ToList();
        for (var i = 0; i + 1 < runes.Count; i++)
        {
            if (!IsIndicator(runes[i]) || !IsIndicator(runes[i + 1]))
            {
                continue;
            }

            var code = string.Concat((char)('A' + runes[i].Value - RegionalIndicatorA), (char)('A' + runes[i + 1].Value - RegionalIndicatorA));
            var rest = new StringBuilder();
            for (var j = 0; j < runes.Count; j++)
            {
                if (j != i && j != i + 1)
                {
                    rest.Append(runes[j].ToString());
                }
            }

            var cleaned = string.Join(' ', rest.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return (code, cleaned.Length > 0 ? cleaned : name);
        }

        return (null, name);
    }

    private static bool IsIndicator(Rune rune) => rune.Value is >= RegionalIndicatorA and <= RegionalIndicatorZ;
}
