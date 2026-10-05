using System.Net;
using Khors.Core.Subscriptions;
using Khors.Engines.Storage;

namespace Khors.Engines.Subscriptions;

/// <param name="Error"><c>null</c> — обновлено.</param>
/// <param name="Unrecognized">Строк ответа, которые не удалось разобрать.</param>
/// <param name="ViaProxy">Напрямую не получилось — загружено через локальный прокси KHORS.</param>
public sealed record SubscriptionUpdateOutcome(SubscriptionUpdateError? Error, int Total = 0, int Added = 0, int Removed = 0, int Unrecognized = 0, bool ViaProxy = false);

/// <summary>
/// Обновление подписки: загрузка (напрямую, при сетевой ошибке и подключённом KHORS — через его прокси),
/// разбор, слияние с имеющимися профилями. Пустой или непонятный ответ не стирает профили подписки.
/// Обновления выполняются по одному — ручное и автоматическое не пересекаются.
/// </summary>
public sealed class SubscriptionUpdater : IDisposable
{
    public delegate Task<SubscriptionFetchResult> SubscriptionFetch(Uri url, string userAgent, IWebProxy? proxy, TimeSpan timeout, CancellationToken cancellationToken);

    private readonly ProfileRepository _repository;
    private readonly Func<int?> _localProxyPort;
    private readonly SubscriptionFetch _fetch;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="localProxyPort">HTTP-порт подключённого KHORS или <c>null</c>, если не подключён.</param>
    public SubscriptionUpdater(ProfileRepository repository, Func<int?> localProxyPort, SubscriptionFetch? fetch = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(localProxyPort);
        _repository = repository;
        _localProxyPort = localProxyPort;
        _fetch = fetch ?? SubscriptionFetcher.FetchAsync;
    }

    public void Dispose() => _gate.Dispose();

    public async Task<SubscriptionUpdateOutcome> UpdateAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UpdateCoreAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SubscriptionUpdateOutcome> UpdateCoreAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = _repository.FindSubscription(subscriptionId)
            ?? throw new KeyNotFoundException($"Subscription {subscriptionId} not found.");
        var url = new Uri(subscription.Url.Value);
        var userAgent = subscription.UserAgent ?? SubscriptionFetcher.DefaultUserAgent;

        var fetched = await _fetch(url, userAgent, null, SubscriptionFetcher.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var viaProxy = false;
        if (fetched.Error is SubscriptionUpdateError.Network or SubscriptionUpdateError.Timeout && _localProxyPort() is { } port)
        {
            fetched = await _fetch(url, userAgent, new WebProxy(new Uri($"http://127.0.0.1:{port}")), SubscriptionFetcher.DefaultTimeout, cancellationToken).ConfigureAwait(false);
            viaProxy = true;
        }

        if (fetched.Error is { } error)
        {
            _repository.MarkSubscriptionFailed(subscriptionId, error);
            return new SubscriptionUpdateOutcome(error, ViaProxy: viaProxy);
        }

        var content = SubscriptionContent.Parse(fetched.Body ?? string.Empty);
        if (content.Profiles.Count == 0)
        {
            _repository.MarkSubscriptionFailed(subscriptionId, SubscriptionUpdateError.NoProfiles);
            return new SubscriptionUpdateOutcome(SubscriptionUpdateError.NoProfiles, Unrecognized: content.Errors.Count, ViaProxy: viaProxy);
        }

        var merge = _repository.ApplySubscriptionUpdate(subscriptionId, content.Profiles, fetched.UserInfo, fetched.UpdateIntervalHours, fetched.Title);
        return new SubscriptionUpdateOutcome(null, merge.Profiles.Count, merge.Added, merge.Removed.Count, content.Errors.Count, viaProxy);
    }
}
