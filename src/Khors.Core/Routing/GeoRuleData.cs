using Khors.Core.Geo;

namespace Khors.Core.Routing;

/// <summary>
/// Категории гео-баз, прочитанные для плана (<see cref="RoutingPlan.GeoSiteCodes"/>, <see cref="RoutingPlan.GeoIpCodes"/>).
/// sing-box 1.12+ не читает <c>.dat</c>: из них строятся встроенные наборы правил (docs/SPEC.md, 4.4).
/// </summary>
/// <param name="Sites">По коду категории без атрибута.</param>
/// <param name="Ips">По коду без отрицания.</param>
public sealed record GeoRuleData(
    IReadOnlyDictionary<string, GeoSiteCategory> Sites,
    IReadOnlyDictionary<string, GeoIpCategory> Ips)
{
    public static GeoRuleData Empty { get; } = new(new Dictionary<string, GeoSiteCategory>(), new Dictionary<string, GeoIpCategory>());

    /// <summary>Первая категория плана, которой нет в данных; <c>null</c> — все на месте.</summary>
    public string? FindMissing(RoutingPlan plan) => Missing(plan) is [var first, ..] ? first : null;

    /// <summary>Категории плана, которых нет в данных: <c>geosite:код</c>, <c>geoip:код</c>.</summary>
    public IReadOnlyList<string> Missing(RoutingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return
        [
            .. plan.GeoSiteCodes.Where(c => !Sites.ContainsKey(c)).Select(c => "geosite:" + c),
            .. plan.GeoIpCodes.Where(c => !Ips.ContainsKey(c)).Select(c => "geoip:" + c),
        ];
    }
}
