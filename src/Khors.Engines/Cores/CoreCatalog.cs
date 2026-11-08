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
    public string Executable => Directory + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);

    /// <summary>Серия поставки, например «26» или «1.14».</summary>
    public string Series => string.Join('.', ShippedVersion.Split('.').Take(SeriesParts));

    /// <summary>Версия из той же серии, что поставка.</summary>
    public bool InSeries(string version) =>
        CoreVersion.TryParse(version, out var parsed) && CoreVersion.TryParse(ShippedVersion, out var shipped)
        && parsed.Take(SeriesParts).SequenceEqual(shipped.Take(SeriesParts));

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

    public static IReadOnlyList<CoreInfo> All { get; } = [Xray, SingBox];

    public static CoreInfo Get(CoreKind kind) => kind == CoreKind.Xray ? Xray : SingBox;
}

/// <summary>Версия ядра: только числа через точку («26.9.9», «1.14.2») — она же имя каталога в хранилище.</summary>
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

    public static bool IsValid(string? text) => TryParse(text, out _);

    /// <summary>Сравнение версий по числам; неверные версии — меньше любых верных.</summary>
    public static int Compare(string? left, string? right)
    {
        var leftValid = TryParse(left, out var a);
        var rightValid = TryParse(right, out var b);
        if (!leftValid || !rightValid)
        {
            return leftValid.CompareTo(rightValid);
        }

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }
}
