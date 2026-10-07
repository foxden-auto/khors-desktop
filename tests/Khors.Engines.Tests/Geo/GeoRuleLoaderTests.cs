using Khors.Core.Routing;
using Khors.Engines.Geo;
using Xunit;

namespace Khors.Engines.Tests.Geo;

public sealed class GeoRuleLoaderTests : IDisposable
{
    private static readonly RoutingPlan s_russia = RoutingPlan.Build(new RoutingSettings(RoutingPreset.BypassRussia), tun: true);

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("khors-geo-rules-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void PlanWithoutGeoRulesReadsNothing()
    {
        var plan = RoutingPlan.Build(new RoutingSettings(Rules: new([new RoutingRule(RuleMatch.Domain, "example.org", RuleAction.Direct)])), tun: false);

        var prepared = GeoRuleLoader.Prepare(plan, directory: null);

        Assert.Same(plan, prepared.Plan);
        Assert.Empty(prepared.Missing);
    }

    [Fact]
    public void ReadsCategoriesOfPlan()
    {
        Write("geosite.dat", GeoFiles.Site(("CATEGORY-RU", "example.ru"), ("PRIVATE", "localhost")));
        Write("geoip.dat", GeoFiles.Ip(("RU", [203, 0, 113, 0]), ("PRIVATE", [10, 0, 0, 0])));

        var prepared = GeoRuleLoader.Prepare(s_russia, _directory.FullName);

        Assert.Equal(s_russia, prepared.Plan);
        Assert.Empty(prepared.Missing);
        Assert.Equal(["category-ru"], prepared.Geo.Sites.Keys);
        Assert.Equal("example.ru", Assert.Single(prepared.Geo.Sites["category-ru"].Domains).Value);
        Assert.Equal("203.0.113.0/24", Assert.Single(prepared.Geo.Ips["ru"].Networks).ToString());
    }

    [Fact]
    public void WithoutDatabasesGeoRulesAreSkipped()
    {
        var own = new RoutingRule(RuleMatch.Domain, "news.example.ru", RuleAction.Proxy);
        var plan = RoutingPlan.Build(new RoutingSettings(RoutingPreset.BypassRussia, Rules: new([own])), tun: false);

        var prepared = GeoRuleLoader.Prepare(plan, _directory.FullName);

        Assert.Equal(["geosite:category-ru", "geoip:ru"], prepared.Missing);
        Assert.Equal([own], prepared.Plan.Rules);
        Assert.Equal(2, prepared.Plan.Skipped.Count);
        Assert.False(prepared.Plan.HasAddressRules);
    }

    [Fact]
    public void MissingCategoryAndBrokenFileAreReported()
    {
        Write("geosite.dat", GeoFiles.Site(("PRIVATE", "localhost")));
        Write("geoip.dat", [0x0A, 0xFF, 0x01]);

        var prepared = GeoRuleLoader.Prepare(s_russia, _directory.FullName);

        Assert.Equal(["geosite:category-ru", "geoip:ru"], prepared.Missing);
        Assert.Empty(prepared.Plan.Rules);
    }

    private void Write(string name, byte[] data) => File.WriteAllBytes(Path.Combine(_directory.FullName, name), data);
}
