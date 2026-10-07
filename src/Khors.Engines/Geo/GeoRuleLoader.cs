using Khors.Core.Geo;
using Khors.Core.Routing;

namespace Khors.Engines.Geo;

/// <summary>План маршрутизации, готовый к запуску ядра: категории прочитаны, отсутствующие — исключены.</summary>
/// <param name="Plan">Без правил с отсутствующими категориями.</param>
/// <param name="Geo">Прочитанные категории (sing-box строит из них наборы правил; Xray читает <c>.dat</c> сам).</param>
/// <param name="Missing">Категории, которых нет (базы не скачаны, файл испорчен или нет такого кода): <c>geosite:код</c>, <c>geoip:код</c>.</param>
public sealed record PreparedRouting(RoutingPlan Plan, GeoRuleData Geo, IReadOnlyList<string> Missing)
{
    public static PreparedRouting Default { get; } = new(RoutingPlan.Default, GeoRuleData.Empty, []);
}

/// <summary>
/// Категории гео-баз для плана из каталога баз (у окна и у службы — свои, docs/SPEC.md, 4.4). Каждый файл читается
/// один раз и только если план на него ссылается; разбирается только нужная категория. Нет базы или категории —
/// правило не действует, а подключение идёт без него: ядро Xray с несуществующей категорией не запустилось бы.
/// </summary>
public static class GeoRuleLoader
{
    public static PreparedRouting Prepare(RoutingPlan plan, string? directory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.GeoSiteCodes.Count == 0 && plan.GeoIpCodes.Count == 0)
        {
            return new PreparedRouting(plan, GeoRuleData.Empty, []);
        }

        var sites = Read(directory, GeoSources.Site.FileName, plan.GeoSiteCodes, GeoSiteDatabase.ReadCategory);
        var ips = Read(directory, GeoSources.Ip.FileName, plan.GeoIpCodes, GeoIpDatabase.ReadCategory);
        var geo = new GeoRuleData(sites, ips);
        var missing = geo.Missing(plan);
        return new PreparedRouting(plan.Without(missing), geo, missing);
    }

    /// <summary>Коды категорий базы (подсказки в окне); нет файла или он испорчен — пустой список.</summary>
    public static IReadOnlyList<string> ListCategories(string directory, GeoDatabaseKind kind)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var source = kind == GeoDatabaseKind.Site ? GeoSources.Site : GeoSources.Ip;
        if (ReadFile(directory, source.FileName) is not { } data)
        {
            return [];
        }

        try
        {
            return kind == GeoDatabaseKind.Site ? GeoSiteDatabase.ListCategories(data) : GeoIpDatabase.ListCategories(data);
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    private delegate T? CategoryReader<T>(ReadOnlySpan<byte> data, string code);

    private static Dictionary<string, T> Read<T>(string? directory, string fileName, IReadOnlyList<string> codes, CategoryReader<T> read)
        where T : class
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        if (codes.Count == 0 || directory is null || ReadFile(directory, fileName) is not { } data)
        {
            return result;
        }

        foreach (var code in codes)
        {
            try
            {
                if (read(data, code) is { } category)
                {
                    result[code] = category;
                }
            }
            catch (InvalidDataException)
            {
                return result;
            }
        }

        return result;
    }

    private static byte[]? ReadFile(string directory, string fileName)
    {
        try
        {
            // Обновление базы заменяет файл целиком (GeoDatabaseUpdater): читаем, не мешая замене.
            using var stream = new FileStream(Path.Combine(directory, fileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
