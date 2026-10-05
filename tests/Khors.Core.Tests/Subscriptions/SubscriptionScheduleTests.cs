using Khors.Core.Profiles;
using Khors.Core.Subscriptions;
using Xunit;

namespace Khors.Core.Tests.Subscriptions;

public class SubscriptionScheduleTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static Subscription Sub(DateTimeOffset? updated = null, int? headerHours = null, SubscriptionUpdateError? error = null, DateTimeOffset? attempt = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "s",
        Url = new Secret("https://sub.example.com/s"),
        UpdatedAt = updated,
        UpdateIntervalHours = headerHours,
        LastError = error,
        LastAttemptAt = attempt,
    };

    [Fact]
    public void NeverUpdatedIsDueImmediately() => Assert.True(SubscriptionSchedule.IsDue(Sub(), 12, s_t0));

    [Fact]
    public void IntervalFromSubscriptionHeaderWins()
    {
        Assert.Equal(s_t0.AddHours(6), SubscriptionSchedule.NextUpdate(Sub(s_t0, headerHours: 6), 12));
        Assert.Equal(s_t0.AddHours(12), SubscriptionSchedule.NextUpdate(Sub(s_t0), 12));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100000, 8760)]
    public void IntervalIsClamped(int hours, int expectedHours) =>
        Assert.Equal(s_t0.AddHours(expectedHours), SubscriptionSchedule.NextUpdate(Sub(s_t0), hours));

    [Fact]
    public void FailedUpdateIsRetriedAfterThirtyMinutes()
    {
        var failed = Sub(updated: s_t0.AddHours(-20), error: SubscriptionUpdateError.Network, attempt: s_t0);

        Assert.Equal(s_t0.AddMinutes(30), SubscriptionSchedule.NextUpdate(failed, 12));
        Assert.False(SubscriptionSchedule.IsDue(failed, 12, s_t0.AddMinutes(10)));
        Assert.True(SubscriptionSchedule.IsDue(failed, 12, s_t0.AddMinutes(31)));
    }
}
