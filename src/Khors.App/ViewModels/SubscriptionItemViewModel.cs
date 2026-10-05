using CommunityToolkit.Mvvm.ComponentModel;
using Khors.App.Services;
using Khors.Core.Subscriptions;

namespace Khors.App.ViewModels;

/// <summary>Подписка в списке: имя, трафик, срок, время обновления или ошибка.</summary>
public sealed partial class SubscriptionItemViewModel(Subscription subscription, DateTimeOffset now) : ObservableObject
{
    public Subscription Subscription { get; } = subscription;

    public Guid Id => Subscription.Id;

    public string Name => Subscription.Name;

    /// <summary>«трафик: 3 ГБ из 50 ГБ · до 01.01.2027» — то, что известно из заголовков подписки.</summary>
    public string? Details { get; } = string.Join(" · ", new[] { Traffic(subscription.UserInfo), Expiry(subscription.UserInfo, now) }.Where(s => s is not null)) is { Length: > 0 } text ? text : null;

    public string Status { get; } = subscription.LastError is { } error
        ? Localizer.Describe(error)
        : subscription.UpdatedAt is { } updated
            ? Localizer.Format("SubscriptionUpdatedAtFormat", updated.ToLocalTime())
            : Localizer.Get("SubscriptionNeverUpdated");

    public bool HasError => Subscription.LastError is not null;

    public bool HasDetails => Details is not null;

    [ObservableProperty]
    public partial bool IsUpdating { get; set; }

    private static string? Traffic(SubscriptionUserInfo? info) => info switch
    {
        null => null,
        { Total: > 0 and var total } => Localizer.Format("SubscriptionTrafficFormat", Localizer.Bytes(info.Used), Localizer.Bytes(total)),
        { Upload: null, Download: null } => null,
        _ => Localizer.Format("SubscriptionTrafficUsedFormat", Localizer.Bytes(info.Used)),
    };

    private static string? Expiry(SubscriptionUserInfo? info, DateTimeOffset now) => info?.Expire switch
    {
        null => null,
        var expire when expire <= now => Localizer.Get("SubscriptionExpired"),
        var expire => Localizer.Format("SubscriptionExpiresFormat", expire.Value.ToLocalTime()),
    };
}
