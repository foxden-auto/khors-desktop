using Khors.Engines.Storage;
using Khors.Ipc;

namespace Khors.Engines.Geo;

/// <summary>Состояние гео-баз для окна.</summary>
/// <param name="Local">Файлы окна (режим «Системный прокси»).</param>
/// <param name="LocalErrors">Причины неудачи последнего обновления окна по видам файлов.</param>
/// <param name="Service">Копия службы (режим TUN); <c>null</c> — служба не отвечает или не установлена.</param>
public sealed record GeoState(
    IReadOnlyList<GeoFileStatus> Local,
    IReadOnlyDictionary<GeoDatabaseKind, GeoUpdateError> LocalErrors,
    IReadOnlyList<IpcGeoFile>? Service,
    bool IsUpdating);

/// <summary>Гео-базы службы через IPC; <c>null</c> — служба недоступна.</summary>
public interface IServiceGeoClient
{
    Task<GeoStatusResponse?> GetStatusAsync(CancellationToken cancellationToken);

    Task<GeoStatusResponse?> UpdateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Гео-базы окна и службы (ROADMAP 3.6): «Обновить сейчас» и проверка раз в сутки (<see cref="Storage.SettingsStore"/>:
/// <c>GeoAutoUpdate</c>, <c>GeoLastCheck</c>). Неудачная проверка повторяется не раньше чем через час.
/// Обновление окна, затем службы — она скачивает свою копию сама.
/// </summary>
public sealed class GeoManager : IAsyncDisposable
{
    private static readonly TimeSpan s_interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan s_retry = TimeSpan.FromHours(1);

    private readonly GeoDatabaseUpdater _local;
    private readonly IServiceGeoClient? _service;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _startDelay;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private Dictionary<GeoDatabaseKind, GeoUpdateError> _localErrors = [];
    private IReadOnlyList<IpcGeoFile>? _serviceFiles;
    private DateTimeOffset? _lastAttempt;
    private int _updating;
    private Task? _loop;
    private int _disposed;

    public GeoManager(GeoDatabaseUpdater local, IServiceGeoClient? service, SettingsStore settings, TimeProvider? time = null, TimeSpan? checkInterval = null, TimeSpan? startDelay = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(settings);
        _local = local;
        _service = service;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _checkInterval = checkInterval ?? TimeSpan.FromMinutes(10);
        _startDelay = startDelay ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>Состояние изменилось (в фоновом потоке).</summary>
    public event EventHandler? Changed;

    public GeoState Current
    {
        get
        {
            lock (_lock)
            {
                return new GeoState(_local.GetStatus(), _localErrors, _serviceFiles, Volatile.Read(ref _updating) == 1);
            }
        }
    }

    public void Start() => _loop ??= RunAsync(_stop.Token);

    /// <summary>Обновляет окно и службу. Если обновление уже идёт — ждёт его и возвращает <c>false</c>.</summary>
    public async Task<bool> UpdateNowAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _updating, 1) == 1)
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            _lastAttempt = _time.GetUtcNow();
            var outcomes = await _local.UpdateAsync(cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _localErrors = outcomes.Where(o => o.Error is not null).ToDictionary(o => o.Kind, o => o.Error!.Value);
            }

            var success = outcomes.All(o => o.Result != GeoUpdateResult.Failed);
            if (success)
            {
                _settings.Update(s => s with { GeoLastCheck = _time.GetUtcNow() });
            }

            await UpdateServiceAsync(cancellationToken).ConfigureAwait(false);
            return success;
        }
        finally
        {
            Volatile.Write(ref _updating, 0);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Только копия службы (например, сразу после установки службы).</summary>
    public async Task UpdateServiceAsync(CancellationToken cancellationToken = default)
    {
        if (_service is null)
        {
            return;
        }

        SetService(await _service.UpdateAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Состояние копии службы без загрузки.</summary>
    public async Task RefreshServiceAsync(CancellationToken cancellationToken = default)
    {
        if (_service is null)
        {
            return;
        }

        SetService(await _service.GetStatusAsync(cancellationToken).ConfigureAwait(false));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Пора ли проверять: автообновление включено, сутки с последней успешной проверки и час с последней попытки.</summary>
    public bool IsDue()
    {
        var settings = _settings.Current;
        var now = _time.GetUtcNow();
        return settings.GeoAutoUpdate
            && (settings.GeoLastCheck is not { } last || now - last >= s_interval || last > now)
            && (_lastAttempt is not { } attempt || now - attempt >= s_retry);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

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

    private void SetService(GeoStatusResponse? status)
    {
        lock (_lock)
        {
            _serviceFiles = status?.Files;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(_startDelay, _time, cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(_checkInterval, _time);
        do
        {
            if (IsDue())
            {
                try
                {
                    await UpdateNowAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Ошибка записи настроек — повторим на следующей проверке.
                }
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }
}
