using System.Text;
using Khors.Core.Subscriptions;
using Xunit;

namespace Khors.Core.Tests.Subscriptions;

public class SubscriptionHeadersTests
{
    [Fact]
    public void UserInfoIsParsed()
    {
        var info = SubscriptionHeaders.ParseUserInfo("upload=1073741824; download=2147483648; total=53687091200; expire=1798761600");

        Assert.Equal(new SubscriptionUserInfo(1073741824, 2147483648, 53687091200, DateTimeOffset.FromUnixTimeSeconds(1798761600)), info);
        Assert.Equal(3221225472, info!.Used);
    }

    [Theory]
    [InlineData("upload=5;download=7", 5L, 7L, null)]
    [InlineData(" total = 100 ; expire=0 ; junk ; upload=x ", null, null, 100L)]
    public void PartialUserInfoIsAccepted(string header, long? upload, long? download, long? total)
    {
        var info = SubscriptionHeaders.ParseUserInfo(header);

        Assert.Equal(upload, info!.Upload);
        Assert.Equal(download, info.Download);
        Assert.Equal(total, info.Total);
        Assert.Null(info.Expire);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("upload=-1")]
    public void MissingOrInvalidUserInfoIsNull(string? header) => Assert.Null(SubscriptionHeaders.ParseUserInfo(header));

    [Theory]
    [InlineData("12", 12)]
    [InlineData(" 24 ", 24)]
    [InlineData("0", null)]
    [InlineData("abc", null)]
    [InlineData(null, null)]
    public void UpdateIntervalIsParsed(string? header, int? expected) => Assert.Equal(expected, SubscriptionHeaders.ParseUpdateInterval(header));

    [Fact]
    public void TitleIsTakenFromProfileTitleIncludingBase64()
    {
        var encoded = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Вымышленный VPN"));

        Assert.Equal("Вымышленный VPN", SubscriptionHeaders.ParseTitle(encoded, null));
        Assert.Equal("Plain title", SubscriptionHeaders.ParseTitle("Plain title", "attachment; filename=\"ignored.txt\""));
    }

    [Fact]
    public void TitleFallsBackToContentDispositionFileName()
    {
        Assert.Equal("my-sub", SubscriptionHeaders.ParseTitle(null, "attachment; filename=\"my-sub.txt\""));
        Assert.Null(SubscriptionHeaders.ParseTitle(null, null));
    }
}
