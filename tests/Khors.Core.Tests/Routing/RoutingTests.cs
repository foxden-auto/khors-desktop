using Khors.Core.Profiles;
using Khors.Core.Routing;
using Xunit;

namespace Khors.Core.Tests.Routing;

public class RoutingTests
{
    [Theory]
    [InlineData(RuleMatch.Domain, " WWW.Example.ORG. ", "www.example.org")]
    [InlineData(RuleMatch.Domain, "localhost", "localhost")]
    [InlineData(RuleMatch.Domain, "пример.рф", "xn--e1afmkfd.xn--p1ai")]
    [InlineData(RuleMatch.DomainSuffix, ".example.org", "example.org")]
    [InlineData(RuleMatch.DomainSuffix, "рф", "xn--p1ai")]
    [InlineData(RuleMatch.DomainSuffix, "_dmarc.example.org", "_dmarc.example.org")]
    [InlineData(RuleMatch.DomainKeyword, "Tracker", "tracker")]
    [InlineData(RuleMatch.IpCidr, "203.0.113.7", "203.0.113.7/32")]
    [InlineData(RuleMatch.IpCidr, "2001:DB8::1", "2001:db8::1/128")]
    [InlineData(RuleMatch.IpCidr, "198.51.100.0/24", "198.51.100.0/24")]
    [InlineData(RuleMatch.GeoSite, "Category-RU", "category-ru")]
    [InlineData(RuleMatch.GeoSite, "geolocation-!cn", "geolocation-!cn")]
    [InlineData(RuleMatch.GeoSite, "google@cn", "google@cn")]
    [InlineData(RuleMatch.GeoSite, "category-ru@!ads", "category-ru@!ads")]
    [InlineData(RuleMatch.GeoIp, "RU", "ru")]
    [InlineData(RuleMatch.GeoIp, "!private", "!private")]
    [InlineData(RuleMatch.Process, "firefox.exe", "firefox.exe")]
    [InlineData(RuleMatch.Process, @"C:\Program Files\Example\App.exe", @"C:\Program Files\Example\App.exe")]
    public void NormalizesValidValues(RuleMatch match, string value, string expected)
    {
        var result = RoutingRuleValidator.Normalize(match, value);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData(RuleMatch.Domain, "", RoutingRuleError.Empty)]
    [InlineData(RuleMatch.Domain, "   ", RoutingRuleError.Empty)]
    [InlineData(RuleMatch.Domain, "domain:example.org", RoutingRuleError.InvalidDomain)]
    [InlineData(RuleMatch.Domain, "https://example.org", RoutingRuleError.InvalidDomain)]
    [InlineData(RuleMatch.Domain, "exa mple.org", RoutingRuleError.InvalidDomain)]
    [InlineData(RuleMatch.Domain, "-example.org", RoutingRuleError.InvalidDomain)]
    [InlineData(RuleMatch.Domain, "example..org", RoutingRuleError.InvalidDomain)]
    [InlineData(RuleMatch.DomainSuffix, "*.example.org", RoutingRuleError.InvalidDomain)]
    [InlineData(RuleMatch.DomainKeyword, "two words", RoutingRuleError.InvalidKeyword)]
    [InlineData(RuleMatch.DomainKeyword, "a\"b", RoutingRuleError.InvalidKeyword)]
    [InlineData(RuleMatch.IpCidr, "203.0.113.7/24", RoutingRuleError.InvalidAddress)]
    [InlineData(RuleMatch.IpCidr, "203.0.113.0/33", RoutingRuleError.InvalidAddress)]
    [InlineData(RuleMatch.IpCidr, "example.org", RoutingRuleError.InvalidAddress)]
    [InlineData(RuleMatch.IpCidr, "fe80::1%12", RoutingRuleError.InvalidAddress)]
    [InlineData(RuleMatch.GeoSite, "geosite:category-ru", RoutingRuleError.InvalidGeoCode)]
    [InlineData(RuleMatch.GeoSite, "category ru", RoutingRuleError.InvalidGeoCode)]
    [InlineData(RuleMatch.GeoSite, "google@", RoutingRuleError.InvalidGeoCode)]
    [InlineData(RuleMatch.GeoIp, "ru@cn", RoutingRuleError.InvalidGeoCode)]
    [InlineData(RuleMatch.GeoIp, "geoip:ru", RoutingRuleError.InvalidGeoCode)]
    [InlineData(RuleMatch.Process, "fire*.exe", RoutingRuleError.InvalidProcess)]
    [InlineData(RuleMatch.Process, "\"firefox.exe\"", RoutingRuleError.InvalidProcess)]
    [InlineData(RuleMatch.Process, @"C:\Program Files\", RoutingRuleError.InvalidProcess)]
    public void RejectsInvalidValues(RuleMatch match, string value, RoutingRuleError error)
    {
        var result = RoutingRuleValidator.Normalize(match, value);

        Assert.Null(result.Value);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void DefaultSettingsGiveDefaultPlan()
    {
        Assert.Equal(RoutingPlan.Default, RoutingPlan.Build(new RoutingSettings(), tun: false));
        Assert.Equal(RoutingPlan.Default, RoutingPlan.Build(new RoutingSettings(), tun: true));
    }

    [Fact]
    public void OwnRulesComeBeforePreset()
    {
        var own = new RoutingRule(RuleMatch.Domain, "news.example.ru", RuleAction.Proxy);
        var plan = RoutingPlan.Build(new RoutingSettings(RoutingPreset.BypassRussia, Rules: new([own])), tun: false);

        Assert.Equal(
            [
                own,
                new RoutingRule(RuleMatch.GeoSite, "category-ru", RuleAction.Direct),
                new RoutingRule(RuleMatch.GeoIp, "ru", RuleAction.Direct),
            ],
            plan.Rules);
        Assert.Equal(RuleAction.Proxy, plan.Final);
        Assert.Equal(["category-ru"], plan.GeoSiteCodes);
        Assert.Equal(["ru"], plan.GeoIpCodes);
        Assert.True(plan.HasAddressRules);
    }

    [Fact]
    public void ProcessRulesAndSelectedAppsWorkOnlyInTun()
    {
        var process = new RoutingRule(RuleMatch.Process, "firefox.exe", RuleAction.Proxy);
        var settings = new RoutingSettings(RoutingPreset.SelectedApps, Rules: new([process]));

        var tun = RoutingPlan.Build(settings, tun: true);
        Assert.Equal([process], tun.Rules);
        Assert.Equal(RuleAction.Direct, tun.Final);
        Assert.Empty(tun.Skipped);

        var proxy = RoutingPlan.Build(settings, tun: false);
        Assert.Empty(proxy.Rules);
        Assert.Equal(RuleAction.Proxy, proxy.Final);
        Assert.Equal([process], proxy.Skipped);
    }

    [Fact]
    public void InvalidStoredRulesAreSkipped()
    {
        var bad = new RoutingRule(RuleMatch.Domain, "Not Normalized.org", RuleAction.Direct);
        var unknownAction = new RoutingRule(RuleMatch.Domain, "example.org", (RuleAction)42);
        var good = new RoutingRule(RuleMatch.GeoSite, "google@cn", RuleAction.Block);

        var plan = RoutingPlan.Build(new RoutingSettings(Rules: new([bad, unknownAction, good])), tun: false);

        Assert.Equal([good], plan.Rules);
        Assert.Equal([bad, unknownAction], plan.Skipped);
        Assert.Equal(["google"], plan.GeoSiteCodes);
        Assert.False(plan.HasAddressRules);
    }

    [Fact]
    public void NegatedGeoIpReadsBaseCode()
    {
        var plan = RoutingPlan.Build(new RoutingSettings(Rules: new([new RoutingRule(RuleMatch.GeoIp, "!private", RuleAction.Proxy)])), tun: true);

        Assert.Equal(["private"], plan.GeoIpCodes);
        Assert.Equal("geoip:private", GeoRuleData.Empty.FindMissing(plan));
    }
}
