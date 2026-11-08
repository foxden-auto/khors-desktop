using System.Text;
using System.Text.Json.Nodes;
using Khors.Engines.Cores;
using Xunit;

namespace Khors.Engines.Tests.Cores;

public class CoreReleasesTests
{
    private const string Digest = "sha256:9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732";

    /// <summary>Версии поставки в коде совпадают с tools/cores/cores.lock.json (иначе серия и подписи «из поставки» врут).</summary>
    [Fact]
    public void ShippedVersionsMatchLockFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "Khors.slnx")))
        {
            dir = dir.Parent;
        }

        var lockFile = JsonNode.Parse(File.ReadAllText(Path.Combine(dir.FullName, "tools", "cores", "cores.lock.json")))!;
        foreach (var core in lockFile["cores"]!.AsArray())
        {
            var info = CoreCatalog.All.Single(c => c.Directory == core!["name"]!.GetValue<string>());
            Assert.Equal(core!["version"]!.GetValue<string>(), info.ShippedVersion);
        }
    }

    [Theory]
    [InlineData("26.9.9", true)]
    [InlineData("1.14.2", true)]
    [InlineData("26", false)]
    [InlineData("1.14.2-beta.1", false)]
    [InlineData("../1.2", false)]
    [InlineData("1..2", false)]
    [InlineData("", false)]
    public void VersionFormat(string version, bool valid) => Assert.Equal(valid, CoreVersion.IsValid(version));

    [Theory]
    [InlineData("26.10.1", "26.9.9", 1)]
    [InlineData("1.14.10", "1.14.9", 1)]
    [InlineData("1.14.2", "1.14.2", 0)]
    [InlineData("1.14", "1.14.0", 0)]
    [InlineData(null, "1.0.0", -1)]
    public void VersionsCompareNumerically(string? left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(CoreVersion.Compare(left, right)));

    [Theory]
    [InlineData("26.10.1", true)]
    [InlineData("27.1.1", false)]
    [InlineData("25.12.8", false)]
    public void XraySeriesIsMajor(string version, bool inSeries) => Assert.Equal(inSeries, CoreCatalog.Xray.InSeries(version));

    [Theory]
    [InlineData("1.14.9", true)]
    [InlineData("1.15.0", false)]
    [InlineData("1.13.11", false)]
    public void SingBoxSeriesIsBranch(string version, bool inSeries) => Assert.Equal(inSeries, CoreCatalog.SingBox.InSeries(version));

    [Fact]
    public void TagsKeepOnlyStableVersions()
    {
        var json = Tags("v1.14.0-rc.2", "v1.14.1", "v1.14.2", "v1.14.10", "x1.14.3", "v1.15.0-alpha.10");

        Assert.Equal(["1.14.1", "1.14.2", "1.14.10"], CoreReleases.ParseTags(json));
    }

    [Fact]
    public void SeriesUrls()
    {
        Assert.Equal(new Uri("https://api.github.com/repos/XTLS/Xray-core/git/matching-refs/tags/v26."), CoreReleases.SeriesTagsUrl(CoreCatalog.Xray));
        Assert.Equal(new Uri("https://api.github.com/repos/SagerNet/sing-box/git/matching-refs/tags/v1.14."), CoreReleases.SeriesTagsUrl(CoreCatalog.SingBox));
        Assert.Equal([new Uri("https://api.github.com/repos/XTLS/Xray-core/git/matching-refs/tags/v27.")], CoreReleases.NextSeriesTagsUrls(CoreCatalog.Xray));
        Assert.Equal(
            [
                new Uri("https://api.github.com/repos/SagerNet/sing-box/git/matching-refs/tags/v1.15."),
                new Uri("https://api.github.com/repos/SagerNet/sing-box/git/matching-refs/tags/v2."),
            ],
            CoreReleases.NextSeriesTagsUrls(CoreCatalog.SingBox));
    }

    /// <summary>Xray помечает все релизы флагом «pre-release» — релиз с числовым тегом всё равно берётся; черновик — нет.</summary>
    [Fact]
    public void ReleaseFlaggedPrereleaseIsAcceptedDraftIsNot()
    {
        var pre = Encoding.UTF8.GetBytes(Release("v26.9.30", "Xray-windows-64.zip", prerelease: true).ToJsonString());
        var draft = Encoding.UTF8.GetBytes(Release("v26.9.30", "Xray-windows-64.zip", draft: true).ToJsonString());

        Assert.Equal("26.9.30", CoreReleases.Parse(CoreCatalog.Xray, "26.9.30", pre)?.Version);
        Assert.Null(CoreReleases.Parse(CoreCatalog.Xray, "26.9.30", draft));
    }

    [Theory]
    [InlineData(null, "github.com")]
    [InlineData("sha256:9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732", "example.com")]
    [InlineData("md5:0123456789abcdef0123456789abcdef", "github.com")]
    public void ReleaseWithoutDigestOrOffGithubIsSkipped(string? digest, string host)
    {
        var json = Encoding.UTF8.GetBytes(Release("v26.10.1", "Xray-windows-64.zip", digest: digest, host: host).ToJsonString());

        Assert.Null(CoreReleases.Parse(CoreCatalog.Xray, "26.10.1", json));
    }

    [Fact]
    public void ReleaseAssetAndDigestAreTaken()
    {
        var json = Encoding.UTF8.GetBytes(Release("v1.14.3", "sing-box-1.14.3-windows-amd64.zip").ToJsonString());

        var release = CoreReleases.Parse(CoreCatalog.SingBox, "1.14.3", json)!;

        Assert.Equal(new Uri("https://github.com/SagerNet/sing-box/releases/download/v1.14.3/sing-box-1.14.3-windows-amd64.zip"), release.AssetUrl);
        Assert.Equal(Convert.FromHexString(Digest["sha256:".Length..]), release.Sha256);
    }

    [Fact]
    public void TagResponseMustMatchVersionAndSeries()
    {
        var release = Encoding.UTF8.GetBytes(Release("v26.10.1", "Xray-windows-64.zip").ToJsonString());

        Assert.Equal("26.10.1", CoreReleases.Parse(CoreCatalog.Xray, "26.10.1", release)?.Version);
        Assert.Null(CoreReleases.Parse(CoreCatalog.Xray, "26.10.2", release));
        Assert.Throws<ArgumentException>(() => CoreReleases.TagUrl(CoreCatalog.Xray, "../../x"));
    }

    [Fact]
    public void GarbageIsRejected() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => CoreReleases.ParseTags(Encoding.UTF8.GetBytes("{\"message\":\"API rate limit exceeded\"}")));

    internal static byte[] Tags(params string[] tags) =>
        Encoding.UTF8.GetBytes(new JsonArray([.. tags.Select(t => (JsonNode)new JsonObject { ["ref"] = "refs/tags/" + t })]).ToJsonString());

    internal static JsonObject Release(string tag, string asset, bool prerelease = false, bool draft = false, string? digest = Digest, string host = "github.com")
    {
        var repo = asset.StartsWith("Xray", StringComparison.Ordinal) ? "XTLS/Xray-core" : "SagerNet/sing-box";
        var file = new JsonObject
        {
            ["name"] = asset,
            ["browser_download_url"] = $"https://{host}/{repo}/releases/download/{tag}/{asset}",
            ["size"] = 1234,
        };
        if (digest is not null)
        {
            file["digest"] = digest;
        }

        return new JsonObject
        {
            ["tag_name"] = tag,
            ["prerelease"] = prerelease,
            ["draft"] = draft,
            ["assets"] = new JsonArray(file, new JsonObject { ["name"] = asset + ".dgst", ["browser_download_url"] = $"https://github.com/{repo}/x.dgst" }),
        };
    }
}
