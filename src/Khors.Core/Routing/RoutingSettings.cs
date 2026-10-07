using Khors.Core.Profiles;

namespace Khors.Core.Routing;

/// <summary>Пресет маршрутизации (docs/SPEC.md, 4.4).</summary>
public enum RoutingPreset
{
    /// <summary>Весь трафик через сервер.</summary>
    AllTraffic,

    /// <summary>Российские домены (<c>geosite:category-ru</c>, в том числе зоны .ru, .su, .рф) и адреса (<c>geoip:ru</c>) — напрямую.</summary>
    BypassRussia,

    /// <summary>Только режим TUN: по умолчанию напрямую, через сервер — процессы из правил с действием «через сервер».</summary>
    SelectedApps,
}

/// <summary>Чем правило выбирает соединение.</summary>
public enum RuleMatch
{
    /// <summary>Точное имя.</summary>
    Domain,

    /// <summary>Домен и все его поддомены.</summary>
    DomainSuffix,

    /// <summary>Подстрока имени.</summary>
    DomainKeyword,

    /// <summary>Адрес или подсеть.</summary>
    IpCidr,

    /// <summary>Категория <c>geosite.dat</c>, можно с атрибутом: <c>category-ru</c>, <c>google@cn</c>.</summary>
    GeoSite,

    /// <summary>Код <c>geoip.dat</c>: <c>ru</c>, <c>private</c>.</summary>
    GeoIp,

    /// <summary>Имя исполняемого файла (<c>chrome.exe</c>) или полный путь. Только режим TUN.</summary>
    Process,
}

public enum RuleAction
{
    Proxy,
    Direct,
    Block,
}

/// <param name="Value">Значение в канонической форме <see cref="RoutingRuleValidator.Normalize"/>.</param>
public sealed record RoutingRule(RuleMatch Match, string Value, RuleAction Action);

/// <summary>Маршрутизация — общая для всех профилей (<c>settings.json</c>).</summary>
/// <param name="BypassLan">Локальные сети — напрямую (так было и до ROADMAP 3.5).</param>
/// <param name="Rules">Свои правила: проверяются сверху вниз раньше пресета.</param>
public sealed record RoutingSettings(
    RoutingPreset Preset = RoutingPreset.AllTraffic,
    bool BypassLan = true,
    EquatableArray<RoutingRule> Rules = default);
