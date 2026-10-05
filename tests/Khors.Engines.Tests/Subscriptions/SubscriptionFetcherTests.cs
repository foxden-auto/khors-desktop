using Khors.Core.Subscriptions;
using Khors.Engines.Subscriptions;
using Xunit;

namespace Khors.Engines.Tests.Subscriptions;

public class SubscriptionFetcherTests
{
    private const string Body = "dHJvamFuOi8vRmljdGlvbmFsLVBhNTVAdHJvamFuLmV4YW1wbGUuY29tOjQ0MyNUcm9qYW4=";

    private static Task<SubscriptionFetchResult> Fetch(Uri url, TimeSpan? timeout = null) =>
        SubscriptionFetcher.FetchAsync(url, "KHORS-Desktop/test", proxy: null, timeout ?? TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    [Fact]
    public async Task BodyAndHeadersAreReturned()
    {
        await using var server = new HttpTestServer(_ => HttpTestServer.Response(
            "200 OK",
            Body,
            "subscription-userinfo: upload=10; download=20; total=1000; expire=1798761600",
            "profile-update-interval: 12",
            "profile-title: base64:0JLRi9C80YvRiNC70LXQvdC90YvQuQ=="));

        var result = await Fetch(server.Url("/sub/token"));

        Assert.Null(result.Error);
        Assert.Equal(Body, result.Body);
        Assert.Equal(new SubscriptionUserInfo(10, 20, 1000, DateTimeOffset.FromUnixTimeSeconds(1798761600)), result.UserInfo);
        Assert.Equal(12, result.UpdateIntervalHours);
        Assert.Equal("Вымышленный", result.Title);
        Assert.Contains("User-Agent: KHORS-Desktop/test", server.LastRequest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectIsFollowed()
    {
        await using var server = new HttpTestServer(path => path == "/old"
            ? HttpTestServer.Response("302 Found", "", "Location: /new")
            : HttpTestServer.Response("200 OK", Body));

        var result = await Fetch(server.Url("/old"));

        Assert.Equal(Body, result.Body);
    }

    [Fact]
    public async Task HttpErrorIsReported()
    {
        await using var server = new HttpTestServer(_ => HttpTestServer.Response("403 Forbidden", "nope"));

        var result = await Fetch(server.Url("/sub"));

        Assert.Equal(SubscriptionUpdateError.HttpError, result.Error);
        Assert.Equal(403, result.HttpStatus);
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        await using var server = new HttpTestServer(_ => HttpTestServer.Response("200 OK", new byte[SubscriptionFetcher.MaxBytes + 1]));

        var result = await Fetch(server.Url("/big"));

        Assert.Equal(SubscriptionUpdateError.TooLarge, result.Error);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task SilentServerTimesOut()
    {
        await using var server = new HttpTestServer(_ => null);

        var result = await Fetch(server.Url("/slow"), TimeSpan.FromSeconds(1));

        Assert.Equal(SubscriptionUpdateError.Timeout, result.Error);
    }

    [Fact]
    public async Task ClosedPortIsNetworkError()
    {
        var port = PortAllocator.Allocate((int?)null)[0];

        var result = await Fetch(new Uri($"http://127.0.0.1:{port}/sub"));

        Assert.Equal(SubscriptionUpdateError.Network, result.Error);
    }
}
