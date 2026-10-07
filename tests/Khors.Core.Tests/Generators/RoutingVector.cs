using System.Text.Json.Nodes;
using Khors.Core.Profiles;
using Khors.Core.Routing;

namespace Khors.Core.Tests.Generators;

/// <summary>
/// Маршрутизация из вектора конфига: <c>"routing": {"preset": "BypassRussia", "bypassLan": false,
/// "rules": [{"match": "Domain", "value": "example.org", "action": "Proxy"}]}</c>; нет блока — план по умолчанию.
/// </summary>
internal static class RoutingVector
{
    public static RoutingPlan Plan(JsonObject vector, bool tun)
    {
        if (vector["routing"] is not { } routing)
        {
            return RoutingPlan.Default;
        }

        var settings = new RoutingSettings(
            routing["preset"] is { } preset ? Enum.Parse<RoutingPreset>(preset.GetValue<string>()) : RoutingPreset.AllTraffic,
            routing["bypassLan"]?.GetValue<bool>() ?? true,
            new EquatableArray<RoutingRule>(routing["rules"]?.AsArray().Select(r => new RoutingRule(
                Enum.Parse<RuleMatch>(r!["match"]!.GetValue<string>()),
                r["value"]!.GetValue<string>(),
                Enum.Parse<RuleAction>(r["action"]!.GetValue<string>()))) ?? []));
        return RoutingPlan.Build(settings, tun);
    }
}
