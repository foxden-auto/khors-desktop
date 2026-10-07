using Khors.Core.Profiles;

namespace Khors.Core.Routing;

/// <summary>
/// Маршрутизация для одного запуска ядра: правила по порядку (свои, затем пресета) и действие для остального.
/// Локальные сети (<see cref="BypassLan"/>) проверяются раньше всех правил. Конфиги ядер строятся из плана
/// (CLAUDE.md, правило 3).
/// </summary>
/// <param name="Skipped">Правила, которые в этом режиме не действуют (процессы — только в TUN) или не прошли проверку.</param>
public sealed record RoutingPlan(
    EquatableArray<RoutingRule> Rules,
    bool BypassLan,
    RuleAction Final,
    EquatableArray<RoutingRule> Skipped = default)
{
    /// <summary>Как до ROADMAP 3.5: локальные сети напрямую, остальное через сервер.</summary>
    public static RoutingPlan Default { get; } = new([], BypassLan: true, RuleAction.Proxy);

    public const string RussiaGeoSite = "category-ru";
    public const string RussiaGeoIp = "ru";

    public static RoutingPlan Build(RoutingSettings settings, bool tun)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var rules = new List<RoutingRule>();
        var skipped = new List<RoutingRule>();
        foreach (var rule in settings.Rules)
        {
            if (!RoutingRuleValidator.IsValid(rule) || (rule.Match == RuleMatch.Process && !tun))
            {
                skipped.Add(rule);
            }
            else
            {
                rules.Add(rule);
            }
        }

        var final = RuleAction.Proxy;
        switch (settings.Preset)
        {
            case RoutingPreset.BypassRussia:
                rules.Add(new RoutingRule(RuleMatch.GeoSite, RussiaGeoSite, RuleAction.Direct));
                rules.Add(new RoutingRule(RuleMatch.GeoIp, RussiaGeoIp, RuleAction.Direct));
                break;

            // Без TUN процесс не узнать: пресет действует как «весь трафик».
            case RoutingPreset.SelectedApps when tun:
                final = RuleAction.Direct;
                break;
        }

        return new RoutingPlan(new EquatableArray<RoutingRule>(rules), settings.BypassLan, final, new EquatableArray<RoutingRule>(skipped));
    }

    /// <summary>Категории geosite без атрибутов — их нужно прочитать из базы.</summary>
    public IReadOnlyList<string> GeoSiteCodes =>
        [.. Rules.Where(r => r.Match == RuleMatch.GeoSite).Select(r => RoutingRuleValidator.GeoSiteBaseCode(r.Value)).Distinct(StringComparer.Ordinal)];

    /// <summary>Коды geoip без отрицания.</summary>
    public IReadOnlyList<string> GeoIpCodes =>
        [.. Rules.Where(r => r.Match == RuleMatch.GeoIp).Select(r => r.Value.TrimStart('!')).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// План без правил, чьих категорий нет в гео-базе (<see cref="GeoRuleData.FindMissing"/>): такие правила уходят
    /// в <see cref="Skipped"/>, а ядро запускается с остальными — без базы подключение не ломается.
    /// </summary>
    /// <param name="missing">Категории в виде <c>geosite:код</c> и <c>geoip:код</c> (код без атрибута и отрицания).</param>
    public RoutingPlan Without(IReadOnlyCollection<string> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        if (missing.Count == 0)
        {
            return this;
        }

        bool IsMissing(RoutingRule rule) => rule.Match switch
        {
            RuleMatch.GeoSite => missing.Contains("geosite:" + RoutingRuleValidator.GeoSiteBaseCode(rule.Value)),
            RuleMatch.GeoIp => missing.Contains("geoip:" + rule.Value.TrimStart('!')),
            _ => false,
        };

        return this with
        {
            Rules = new EquatableArray<RoutingRule>(Rules.Where(r => !IsMissing(r))),
            Skipped = new EquatableArray<RoutingRule>([.. Skipped, .. Rules.Where(IsMissing)]),
        };
    }

    /// <summary>Есть правила по адресу: соединению по имени нужен адрес, чтобы их проверить.</summary>
    public bool HasAddressRules => Rules.Any(r => r.Match is RuleMatch.IpCidr or RuleMatch.GeoIp);
}
