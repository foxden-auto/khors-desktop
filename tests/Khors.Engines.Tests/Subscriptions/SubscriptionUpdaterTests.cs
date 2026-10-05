using System.Net;
using System.Text;
using Khors.Core.Subscriptions;
using Khors.Engines.Storage;
using Khors.Engines.Subscriptions;
using Khors.Engines.Tests.Storage;
using Xunit;

namespace Khors.Engines.Tests.Subscriptions;

public sealed class SubscriptionUpdaterTests : IDisposable
{
    private const string Vless = "vless://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@vpn.example.com:443?security=tls&sni=vpn.example.com#Германия";
    private const string Trojan = "trojan://Fictional-Pa55@trojan.example.com:443#Нидерланды";
    private const string Ss = "ss://Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTpTc1BhNTV3MHJk@192.0.2.44:8388#Финляндия";

    private readonly TempDirectory _directory = new();
    private readonly List<IWebProxy?> _proxies = [];
    private int? _localPort;

    public void Dispose() => _directory.Dispose();

    private static string Base64(params string[] links) => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", links)));

    private (ProfileRepository Repository, Subscription Subscription, SubscriptionUpdater Updater) Create(Func<int, SubscriptionFetchResult> respond)
    {
        var repository = ProfileRepository.Open(_directory.File("profiles.json"));
        var subscription = repository.AddSubscription(new Uri("https://sub.example.com/s/fictional-token"));
        var calls = 0;
        var updater = new SubscriptionUpdater(repository, () => _localPort, (url, ua, proxy, timeout, ct) =>
        {
            _proxies.Add(proxy);
            return Task.FromResult(respond(++calls));
        });
        return (repository, subscription, updater);
    }

    [Fact]
    public async Task UpdateKeepsIdsOfSameConnectionsAndPersistsSubscriptionInfo()
    {
        var userInfo = new SubscriptionUserInfo(1, 2, 100, null);
        var (repository, subscription, updater) = Create(call => call == 1
            ? new SubscriptionFetchResult(null, Base64(Vless, Trojan), userInfo, 24, "Вымышленный VPN")
            : new SubscriptionFetchResult(null, Base64(Ss, Vless), userInfo, 24, "Вымышленный VPN"));

        var first = await updater.UpdateAsync(subscription.Id, TestContext.Current.CancellationToken);
        var germanyId = repository.Profiles.Single(p => p.Name == "Германия").Id;
        var second = await updater.UpdateAsync(subscription.Id, TestContext.Current.CancellationToken);

        Assert.Equal(new SubscriptionUpdateOutcome(null, Total: 2, Added: 2), first);
        Assert.Equal(new SubscriptionUpdateOutcome(null, Total: 2, Added: 1, Removed: 1), second);
        Assert.Equal(["Финляндия", "Германия"], repository.Profiles.Select(p => p.Name));
        Assert.Equal(germanyId, repository.Profiles.Single(p => p.Name == "Германия").Id);

        var reopened = ProfileRepository.Open(_directory.File("profiles.json"));
        var saved = Assert.Single(reopened.Subscriptions);
        Assert.Equal("Вымышленный VPN", saved.Name);
        Assert.Equal(userInfo, saved.UserInfo);
        Assert.Equal(24, saved.UpdateIntervalHours);
        Assert.All(reopened.Profiles, p => Assert.Equal(subscription.Id, p.SubscriptionId));
    }

    [Fact]
    public async Task ResponseWithoutProfilesKeepsExistingProfiles()
    {
        var (repository, subscription, updater) = Create(call => call == 1
            ? new SubscriptionFetchResult(null, Base64(Vless))
            : new SubscriptionFetchResult(null, "<html>Login required</html>"));

        await updater.UpdateAsync(subscription.Id, TestContext.Current.CancellationToken);
        var outcome = await updater.UpdateAsync(subscription.Id, TestContext.Current.CancellationToken);

        Assert.Equal(SubscriptionUpdateError.NoProfiles, outcome.Error);
        Assert.Single(repository.Profiles);
        Assert.Equal(SubscriptionUpdateError.NoProfiles, repository.FindSubscription(subscription.Id)!.LastError);
    }

    [Fact]
    public async Task NetworkErrorRetriesThroughConnectedKhors()
    {
        _localPort = 10809;
        var (repository, subscription, updater) = Create(call => call == 1
            ? new SubscriptionFetchResult(SubscriptionUpdateError.Network)
            : new SubscriptionFetchResult(null, Base64(Vless)));

        var outcome = await updater.UpdateAsync(subscription.Id, TestContext.Current.CancellationToken);

        Assert.True(outcome.ViaProxy);
        Assert.Null(outcome.Error);
        Assert.Null(_proxies[0]);
        Assert.Equal(new Uri("http://127.0.0.1:10809/"), ((WebProxy)_proxies[1]!).Address);
        Assert.Single(repository.Profiles);
    }

    [Fact]
    public async Task NetworkErrorWithoutConnectionIsRecorded()
    {
        var (repository, subscription, updater) = Create(_ => new SubscriptionFetchResult(SubscriptionUpdateError.Timeout));

        var outcome = await updater.UpdateAsync(subscription.Id, TestContext.Current.CancellationToken);

        Assert.Equal(SubscriptionUpdateError.Timeout, outcome.Error);
        Assert.Single(_proxies);
        Assert.Equal(SubscriptionUpdateError.Timeout, repository.FindSubscription(subscription.Id)!.LastError);
    }

    [Fact]
    public void SubscriptionIsAddedOnceAndRemovedWithItsProfiles()
    {
        var repository = ProfileRepository.Open(_directory.File("profiles.json"));
        var url = new Uri("https://sub.example.com/s/fictional-token");

        var first = repository.AddSubscription(url);
        Assert.Equal(first, repository.AddSubscription(url));
        repository.Import(Trojan);
        repository.ApplySubscriptionUpdate(first.Id, [Khors.Core.Import.ShareLinkParser.Parse(Vless).Profile!], null, null, null);

        Assert.True(repository.RemoveSubscription(first.Id));
        Assert.Empty(repository.Subscriptions);
        Assert.Equal(["Нидерланды"], repository.Profiles.Select(p => p.Name));
        Assert.Throws<ArgumentException>(() => repository.AddSubscription(new Uri("ftp://sub.example.com/x")));
    }
}
