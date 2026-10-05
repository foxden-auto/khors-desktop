namespace Khors.Core.Subscriptions;

/// <summary>Когда обновлять подписку (ROADMAP 2.2). Чистая функция.</summary>
public static class SubscriptionSchedule
{
    /// <summary>Повтор после неудачного обновления — не чаще, чем раз в это время (и не реже интервала подписки).</summary>
    public static TimeSpan RetryAfterFailure { get; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Время следующего обновления. Интервал — из заголовка подписки, иначе <paramref name="defaultIntervalHours"/>
    /// (1…8760 ч). Ещё не обновлявшаяся подписка — сразу; после ошибки — через <see cref="RetryAfterFailure"/>.
    /// </summary>
    public static DateTimeOffset NextUpdate(Subscription subscription, int defaultIntervalHours)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var interval = TimeSpan.FromHours(Math.Clamp(subscription.UpdateIntervalHours ?? defaultIntervalHours, 1, 8760));

        if (subscription.LastError is not null && subscription.LastAttemptAt is { } attempt)
        {
            return attempt + (interval < RetryAfterFailure ? interval : RetryAfterFailure);
        }

        return subscription.UpdatedAt is { } updated ? updated + interval : DateTimeOffset.MinValue;
    }

    public static bool IsDue(Subscription subscription, int defaultIntervalHours, DateTimeOffset now) =>
        NextUpdate(subscription, defaultIntervalHours) <= now;
}
