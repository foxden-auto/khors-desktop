namespace Khors.Engines.Geo;

public enum GeoDatabaseKind
{
    /// <summary>Домены по категориям: <c>geosite.dat</c> (v2fly/domain-list-community, MIT).</summary>
    Site,

    /// <summary>IP-адреса по странам: <c>geoip.dat</c> (v2fly/geoip, CC BY-SA 4.0; данные DB-IP Lite, CC BY 4.0).</summary>
    Ip,
}

/// <summary>Откуда скачивать файл и чем проверять: адрес файла и адрес его <c>.sha256sum</c> из того же релиза.</summary>
public sealed record GeoMirror(Uri Data, Uri Checksum);

/// <param name="FileName">Имя на диске — то, что ждёт Xray (<c>geosite.dat</c>, <c>geoip.dat</c>).</param>
/// <param name="RequiredCategories">Без этих категорий файл не принимается (обрезанный или чужой файл).</param>
public sealed record GeoSource(GeoDatabaseKind Kind, string FileName, IReadOnlyList<GeoMirror> Mirrors, IReadOnlyList<string> RequiredCategories, long MaxBytes);

/// <summary>
/// Источники гео-баз (ROADMAP 3.6): выбраны по лицензии — без GPL (CLAUDE.md, правила 1 и 7). Сначала GitHub Releases,
/// при неудаче — то же содержимое через jsDelivr. Авторство указано в NOTICE и «О приложении».
/// </summary>
public static class GeoSources
{
    public static GeoSource Site { get; } = new(
        GeoDatabaseKind.Site,
        "geosite.dat",
        [
            Mirror("https://github.com/v2fly/domain-list-community/releases/latest/download/dlc.dat"),
            Mirror("https://cdn.jsdelivr.net/gh/v2fly/domain-list-community@release/dlc.dat"),
        ],
        ["category-ru", "private"],
        MaxBytes: 32L * 1024 * 1024);

    public static GeoSource Ip { get; } = new(
        GeoDatabaseKind.Ip,
        "geoip.dat",
        [
            Mirror("https://github.com/v2fly/geoip/releases/latest/download/geoip.dat"),
            Mirror("https://cdn.jsdelivr.net/gh/v2fly/geoip@release/geoip.dat"),
        ],
        ["ru", "private"],
        MaxBytes: 128L * 1024 * 1024);

    public static IReadOnlyList<GeoSource> All { get; } = [Site, Ip];

    private static GeoMirror Mirror(string data) => new(new Uri(data), new Uri(data + ".sha256sum"));
}
