using System.Text;
using Khors.Core.Storage;
using Khors.Core.Subscriptions;
using Khors.Engines.Storage;
using Khors.Engines.Subscriptions;
using Khors.Engines.Tests.Storage;
using Xunit;

namespace Khors.Engines.Tests.Subscriptions;

public sealed class SubscriptionSchedulerTests : IDisposable
{
    private const string Vless = "vless://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@vpn.example.com:443?security=tls&sni=vpn.example.com#Германия";

    private readonly TempDirectory _directory = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    private AppSettings _settings = new();
    private int _fetches;
    private SubscriptionFetchResult _response = new(null, Convert.ToBase64String(Encoding.UTF8.GetBytes(Vless)));

    public void Dispose() => _directory.Dispose();

    private (ProfileRepository Repository, SubscriptionUpdater Updater, SubscriptionScheduler Scheduler) Create()
    {
        var repository = ProfileRepository.Open(_directory.File("profiles.json"), _time);
        repository.AddSubscription(new Uri("https://sub.example.com/s/fictional-token"));
        var updater = new SubscriptionUpdater(repository, () => null, (url, ua, proxy, timeout, ct) =>
        {
            Interlocked.Increment(ref _fetches);
            return Task.FromResult(_response);
        });
        return (repository, updater, new SubscriptionScheduler(repository, updater, () => _settings, _time));
    }

    [Fact]
    public async Task DueSubscriptionsAreUpdatedOnlyWhenTheirTimeComes()
    {
        var (repository, updater, scheduler) = Create();
        await using (scheduler)
        using (updater)
        {
            var ct = TestContext.Current.CancellationToken;

            Assert.Equal(1, await scheduler.UpdateDueAsync(ct));
            Assert.Single(repository.Profiles);

            _time.Now = _time.Now.AddHours(11);
            Assert.Equal(0, await scheduler.UpdateDueAsync(ct));

            _time.Now = _time.Now.AddHours(2);
            Assert.Equal(1, await scheduler.UpdateDueAsync(ct));
            Assert.Equal(2, _fetches);
        }
    }

    [Fact]
    public async Task FailedSubscriptionIsRetriedAfterThirtyMinutes()
    {
        _response = new SubscriptionFetchResult(SubscriptionUpdateError.Network);
        var (_, updater, scheduler) = Create();
        await using (scheduler)
        using (updater)
        {
            var ct = TestContext.Current.CancellationToken;
            await scheduler.UpdateDueAsync(ct);

            _time.Now = _time.Now.AddMinutes(10);
            Assert.Equal(0, await scheduler.UpdateDueAsync(ct));

            _time.Now = _time.Now.AddMinutes(25);
            Assert.Equal(1, await scheduler.UpdateDueAsync(ct));
        }
    }

    [Fact]
    public async Task DisabledAutoUpdateDoesNothing()
    {
        _settings = new AppSettings(SubscriptionAutoUpdate: false);
        var (_, updater, scheduler) = Create();
        await using (scheduler)
        using (updater)
        {
            Assert.Equal(0, await scheduler.UpdateDueAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, _fetches);
        }
    }

    [Fact]
    public async Task UpdatesNeverOverlap()
    {
        var repository = ProfileRepository.Open(_directory.File("profiles.json"), _time);
        var first = repository.AddSubscription(new Uri("https://sub.example.com/one"));
        var second = repository.AddSubscription(new Uri("https://sub.example.com/two"));
        var running = 0;
        var maxRunning = 0;
        using var updater = new SubscriptionUpdater(repository, () => null, async (url, ua, proxy, timeout, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(100, ct);
            Interlocked.Decrement(ref running);
            return _response;
        });

        await Task.WhenAll(updater.UpdateAsync(first.Id, TestContext.Current.CancellationToken), updater.UpdateAsync(second.Id, TestContext.Current.CancellationToken));

        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task BackgroundLoopRunsAfterStartDelayAndStopsOnDispose()
    {
        var repository = ProfileRepository.Open(_directory.File("profiles.json"));
        repository.AddSubscription(new Uri("https://sub.example.com/s/fictional-token"));
        var updated = new TaskCompletionSource();
        using var updater = new SubscriptionUpdater(repository, () => null, (url, ua, proxy, timeout, ct) => Task.FromResult(_response));
        var scheduler = new SubscriptionScheduler(repository, updater, () => _settings, startDelay: TimeSpan.FromMilliseconds(50), checkInterval: TimeSpan.FromHours(1));
        scheduler.Updated += (_, _) => updated.TrySetResult();

        scheduler.Start();
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Single(repository.Profiles);
    }
}
