using Khors.Core.Storage;
using Khors.Core.Subscriptions;
using Khors.Engines.Storage;

namespace Khors.Engines.Subscriptions;

/// <summary>
/// Автообновление подписок (ROADMAP 2.2): раз в <c>checkInterval</c> обновляет подписки, у которых подошёл срок
/// (<see cref="SubscriptionSchedule"/>). Первая проверка — через <c>startDelay</c> после запуска.
/// После сна компьютера просроченные подписки обновляются на первой же проверке.
/// </summary>
public sealed class SubscriptionScheduler : IAsyncDisposable
{
    private readonly ProfileRepository _repository;
    private readonly SubscriptionUpdater _updater;
    private readonly Func<AppSettings> _settings;
    private readonly TimeProvider _time;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _startDelay;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public SubscriptionScheduler(
        ProfileRepository repository,
        SubscriptionUpdater updater,
        Func<AppSettings> settings,
        TimeProvider? time = null,
        TimeSpan? checkInterval = null,
        TimeSpan? startDelay = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(settings);
        _repository = repository;
        _updater = updater;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _checkInterval = checkInterval ?? TimeSpan.FromMinutes(1);
        _startDelay = startDelay ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Подписка обновлена автоматически (в фоновом потоке).</summary>
    public event EventHandler<SubscriptionUpdateOutcome>? Updated;

    public void Start() => _loop ??= RunAsync(_stop.Token);

    /// <summary>Обновляет все подписки, у которых подошёл срок. Возвращает число обновлённых (успешно или нет).</summary>
    public async Task<int> UpdateDueAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings();
        if (!settings.SubscriptionAutoUpdate || _repository.IsReadOnly)
        {
            return 0;
        }

        var due = _repository.Subscriptions
            .Where(s => SubscriptionSchedule.IsDue(s, settings.SubscriptionUpdateIntervalHours, _time.GetUtcNow()))
            .ToList();

        foreach (var subscription in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await _updater.UpdateAsync(subscription.Id, cancellationToken).ConfigureAwait(false);
            Updated?.Invoke(this, outcome);
        }

        return due.Count;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(_startDelay, _time, cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(_checkInterval, _time);
        do
        {
            try
            {
                await UpdateDueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
            {
                // Ошибка записи файла или подписку удалили во время обновления — повторим на следующей проверке.
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }
}
