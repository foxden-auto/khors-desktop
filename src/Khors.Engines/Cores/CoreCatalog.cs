using System.Globalization;

namespace Khors.Engines.Cores;

/// <summary>
/// Что известно о ядре для обновления из GitHub Releases (ROADMAP 5.2, docs/SPEC.md, 4.8): репозиторий, версия
/// в поставке (как в <c>tools/cores/cores.lock.json</c> — сверяется тестом) и серия, в пределах которой разрешено
/// обновление: Xray — тот же мажор, sing-box — та же ветка. Новая серия приходит вместе с новой версией KHORS:
/// генераторы конфигов проверены только на ней (CLAUDE.md, «Предметные знания»).
/// </summary>
/// <param name="Directory">Каталог ядра в поставке и в хранилище обновлений.</param>
/// <param name="SeriesParts">Сколько первых чисел версии должны совпасть с поставкой: 1 — мажор, 2 — ветка.</param>
public sealed record CoreInfo(CoreKind Kind, string Repository, string ShippedVersion, string Directory, int SeriesParts)
{
    /// <summary>Имя исполняемого файла на этой платформе.</summary>
    public string Executable => (Kind == CoreKind.SingBoxExtended ? "sing-box" : Directory) + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);

    /// <summary>Серия поставки, например «26» или «1.14».</summary>
    public string Series => string.Join('.', ShippedVersion.Split('.').Take(SeriesParts));

    /// <summary>Версия из той же серии, что поставка.</summary>
    public bool InSeries(string version)
    {
        if (Kind == CoreKind.SingBoxExtended)
        {
            // В Windows ZIP этого релиза ошибочно лежит Linux ELF; совместимость проверена по содержимому архива 2026-11-12.
            if (version == "1.14.3-extended-2.7.3")
            {
                return false;
            }

            return CoreVersion.TryParseExtended(version, out var main, out _)
                && CoreVersion.TryParse(ShippedVersion.Split("-extended-", StringSplitOptions.None)[0], out var shipped)
                && main.Take(SeriesParts).SequenceEqual(shipped.Take(SeriesParts));
        }

        return CoreVersion.TryParse(version, out var parsed) && CoreVersion.TryParse(ShippedVersion, out var shippedNumbers)
            && parsed.Take(SeriesParts).SequenceEqual(shippedNumbers.Take(SeriesParts));
    }

    /// <summary>
    /// Архив релиза для этой платформы и пути внутри него: исполняемый файл и лицензия (лицензия идёт вместе с ядром).
    /// <c>null</c> — для платформы обновление из релизов не поддерживается.
    /// </summary>
    public CoreAsset? AssetFor(string version)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
        {
            return null;
        }

        return Kind switch
        {
            CoreKind.Xray => new CoreAsset("Xray-windows-64.zip", "xray.exe", "LICENSE"),
            _ => new CoreAsset(
                string.Create(CultureInfo.InvariantCulture, $"sing-box-{version}-windows-amd64.zip"),
                string.Create(CultureInfo.InvariantCulture, $"sing-box-{version}-windows-amd64/sing-box.exe"),
                string.Create(CultureInfo.InvariantCulture, $"sing-box-{version}-windows-amd64/LICENSE")),
        };
    }
}

/// <param name="Name">Имя файла в релизе.</param>
/// <param name="ExecutableEntry">Путь исполняемого файла в архиве.</param>
/// <param name="LicenseEntry">Путь лицензии в архиве.</param>
public sealed record CoreAsset(string Name, string ExecutableEntry, string LicenseEntry);

public static class CoreCatalog
{
    public static CoreInfo Xray { get; } = new(CoreKind.Xray, "XTLS/Xray-core", "26.10.31", "xray", SeriesParts: 1);

    public static CoreInfo SingBox { get; } = new(CoreKind.SingBox, "SagerNet/sing-box", "1.14.2", "sing-box", SeriesParts: 2);

    public static CoreInfo SingBoxExtended { get; } = new(CoreKind.SingBoxExtended, "shtorm-7/sing-box-extended", "1.14.1-extended-2.7.2", "sing-box-extended", SeriesParts: 2);

    public static IReadOnlyList<CoreInfo> All { get; } = [Xray, SingBox, SingBoxExtended];

    public static CoreInfo Get(CoreKind kind) => kind switch
    {
        CoreKind.Xray => Xray,
        CoreKind.SingBox => SingBox,
        _ => SingBoxExtended,
    };
}

/// <summary>Версия ядра: числа через точку («26.9.9», «1.14.2») или «<основная>-extended-<расширение>» («1.14.1-extended-2.7.2», только sing-box-extended) — она же имя каталога в хранилище.</summary>
public static class CoreVersion
{
    public static bool TryParse(string? text, out int[] parts)
    {
        parts = [];
        if (string.IsNullOrEmpty(text) || text.Length > 32)
        {
            return false;
        }

        var items = text.Split('.');
        if (items.Length is < 2 or > 4)
        {
            return false;
        }

        var result = new int[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].Length is 0 or > 6 || !items[i].All(char.IsAsciiDigit)
                || !int.TryParse(items[i], NumberStyles.None, CultureInfo.InvariantCulture, out result[i]))
            {
                return false;
            }
        }

        parts = result;
        return true;
    }

    public static bool IsValid(string? text) => TryParse(text, out _) || TryParseExtended(text, out _, out _);

    public static bool TryParseExtended(string? text, out int[] main, out int[] extension)
    {
        main = [];
        extension = [];
        var parts = text?.Split("-extended-", StringSplitOptions.None);
        return parts is { Length: 2 } && TryParse(parts[0], out main) && TryParse(parts[1], out extension);
    }

    /// <summary>Сравнение версий по числам; у extended сначала основная версия, затем версия расширения.</summary>
    public static int Compare(string? left, string? right)
    {
        var leftExtended = TryParseExtended(left, out var leftMain, out var leftExtension);
        var rightExtended = TryParseExtended(right, out var rightMain, out var rightExtension);
        var leftValid = leftExtended || TryParse(left, out leftMain);
        var rightValid = rightExtended || TryParse(right, out rightMain);
        if (!leftValid || !rightValid)
        {
            return leftValid.CompareTo(rightValid);
        }

        var main = CompareParts(leftMain, rightMain);
        if (main != 0 || leftExtended != rightExtended)
        {
            return main != 0 ? main : leftExtended.CompareTo(rightExtended);
        }

        return leftExtended ? CompareParts(leftExtension, rightExtension) : 0;
    }

    private static int CompareParts(int[] left, int[] right)
    {
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var x = i < left.Length ? left[i] : 0;
            var y = i < right.Length ? right[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }
}
