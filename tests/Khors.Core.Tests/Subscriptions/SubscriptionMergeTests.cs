using Khors.Core.Profiles;
using Khors.Core.Subscriptions;
using Khors.Core.Tests.Profiles;
using Xunit;

namespace Khors.Core.Tests.Subscriptions;

public class SubscriptionMergeTests
{
    private static readonly DateTimeOffset s_before = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    private static readonly Subscription s_subscription = new()
    {
        Id = Guid.Parse("9e8d7c6b-5a4f-4e3d-9c2b-1a0f9e8d7c6b"),
        Name = "Вымышленная подписка",
        Url = new Secret("https://sub.example.com/s/token"),
    };

    private static Profile Incoming(Profile profile, string name) => profile with { Id = Guid.Empty, Name = name, UpdatedAt = default, SubscriptionId = null, Group = null };

    private static Profile Existing(Profile profile, string name) => profile with { Name = name, UpdatedAt = s_before, SubscriptionId = s_subscription.Id, Group = s_subscription.Name };

    [Fact]
    public void FirstUpdateAddsAllProfilesWithSubscriptionGroup()
    {
        var result = SubscriptionMerge.Merge([], [Incoming(TestProfiles.VlessReality(), "A"), Incoming(TestProfiles.TrojanGrpc(), "B")], s_subscription, Guid.NewGuid, s_now);

        Assert.Equal(2, result.Added);
        Assert.Empty(result.Removed);
        Assert.All(result.Profiles, p =>
        {
            Assert.NotEqual(Guid.Empty, p.Id);
            Assert.Equal(s_subscription.Id, p.SubscriptionId);
            Assert.Equal(s_subscription.Name, p.Group);
            Assert.Equal(s_now, p.UpdatedAt);
        });
    }

    [Fact]
    public void SameConnectionsKeepIdsRenamesAreTrackedAndMissingAreRemoved()
    {
        var keep = Existing(TestProfiles.VlessReality(), "Германия");
        var rename = Existing(TestProfiles.VmessWsTls(), "Старое имя");
        var gone = Existing(TestProfiles.TrojanGrpc(), "Исчезнет");

        var result = SubscriptionMerge.Merge(
            [keep, rename, gone],
            [Incoming(TestProfiles.ShadowsocksPlugin(), "Новый"), Incoming(TestProfiles.VmessWsTls(), "Новое имя"), Incoming(TestProfiles.VlessReality(), "Германия")],
            s_subscription,
            Guid.NewGuid,
            s_now);

        Assert.Equal(["Новый", "Новое имя", "Германия"], result.Profiles.Select(p => p.Name));
        Assert.Equal(rename.Id, result.Profiles[1].Id);
        Assert.Equal(s_now, result.Profiles[1].UpdatedAt);
        Assert.Equal(keep.Id, result.Profiles[2].Id);
        Assert.Equal(s_before, result.Profiles[2].UpdatedAt);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Renamed);
        Assert.Equal([gone.Id], result.Removed);
    }
}
