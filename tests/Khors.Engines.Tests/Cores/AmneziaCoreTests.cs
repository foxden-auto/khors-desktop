using System.Text;
using Khors.Core.Import;
using Khors.Engines.Connection;
using Khors.Engines.Cores;
using Xunit;

namespace Khors.Engines.Tests.Cores;

public class AmneziaCoreTests
{
    private const string Link = "wireguard://RotLZicYjYUFFxy3SYlIvOJxpGmWtNthOuIC4yZdZ14%3D@198.51.100.9:51820?publickey=S2BGSyYNxTf1wij4Qk%2F8nbbFhbbGfzsDwb4shXmfImw%3D&address=10.0.0.2%2F32&jc=4&jmin=10&jmax=50&s1=1&s2=2&h1=1&h2=2&h3=3&h4=4#awg";

    [Fact]
    public void AutoSelectionUsesExtendedCore()
    {
        var profile = ShareLinkParser.Parse(Link).Profile!;

        Assert.Equal(CoreKind.SingBoxExtended, CoreSelection.For(profile));
    }

    [Theory]
    [InlineData("1.14.1-extended-2.7.2", true)]
    [InlineData("1.14.9-extended-2.8.0", true)]
    [InlineData("1.15.0-extended-2.8.0", false)]
    [InlineData("1.14.3", false)]
    [InlineData("1.14.1-extended-2.7.2-beta", false)]
    public void ExtendedSeriesAcceptsOnlyExtendedVersions(string version, bool expected) =>
        Assert.Equal(expected, CoreCatalog.SingBoxExtended.InSeries(version));

    [Fact]
    public void ExtendedVersionsCompareByBothNumbers()
    {
        Assert.True(CoreVersion.Compare("1.14.1-extended-2.7.10", "1.14.1-extended-2.7.9") > 0);
        Assert.True(CoreVersion.Compare("1.14.2-extended-2.7.0", "1.14.1-extended-9.0.0") > 0);
        Assert.False(CoreCatalog.SingBox.InSeries("1.14.3-extended-2.7.3"));
    }

    [Fact]
    public void ExtendedReleaseHasExpectedAsset()
    {
        const string version = "1.14.1-extended-2.7.2";
        var json = Encoding.UTF8.GetBytes(CoreReleasesTests.Release(
            "v" + version,
            $"sing-box-{version}-windows-amd64.zip").ToJsonString());

        var release = CoreReleases.Parse(CoreCatalog.SingBoxExtended, version, json);

        Assert.NotNull(release);
        Assert.Equal($"sing-box-{version}-windows-amd64/sing-box.exe", CoreCatalog.SingBoxExtended.AssetFor(version)!.ExecutableEntry);
    }
}
