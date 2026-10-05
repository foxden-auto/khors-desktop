using System.Threading.Channels;
using Khors.Core.Profiles;
using Khors.Engines.Connection;
using Khors.Engines.Latency;

namespace Khors.Engines.Auto;

public enum AutoState
{
    /// <summary>«Авто» не работает.</summary>
    Off,

    /// <summary>Замер профилей и подключение к лучшему.</summary>
    Selecting,

    /// <summary>Подключено к выбранному профилю, идёт перепроверка.</summary>
    Connected,

    /// <summary>Ни один профиль сейчас не отвечает; повтор при следующей перепроверке.</summary>
    Waiting,

    /// <summary>При запуске ни один профиль не ответил — «Авто» остановлено.</summary>
    Failed,
}

/// <summary>Состояние «Авто». <see cref="Measured"/> из <see cref="Total"/> — ход замера при подборе.</summary>
public sealed record AutoStatus(AutoState State, Guid? ProfileId = null, int Measured = 0, int Total = 0);

/// <summary>Итог замера одного профиля.</summary>
public sealed record ProfileLatency(Guid ProfileId, LatencyResult Result);

/// <summary>
/// Группа «Авто» (docs/SPEC.md, 4.3): замер всех профилей-кандидатов, подключение к самому быстрому,
/// перепроверка раз в <see cref="RecheckInterval"/>. Падение ядра или причина из лога (ROADMAP 2.7) у текущего
/// профиля — сразу переход на следующий; работающий профиль меняется только на заметно более быстрый
/// (<see cref="AutoPolicy.IsNotablyFaster"/>). Подключение выполняет <see cref="ConnectionManager"/>.
/// События приходят в фоновом потоке.
/// </summary>
public sealed class AutoConnector : IAsyncDisposable
{
    private const int Parallelism = 4;

    private readonly ConnectionManager _connection;
    private readonly ILatencyProbe _probe;
    private readonly Func<IReadOnlyList<Profile>> _profiles;
    private readonly Func<CoreStartPreferences> _preferences;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _statusLock = new();
    private AutoStatus _status = new(AutoState.Off);
    private CancellationTokenSource? _session;
    private Channel<AutoTrigger>? _triggers;
    private Task? _loop;
    private int _switching;
    private int _disposed;

    public AutoConnector(
        ConnectionManager connection,
        ILatencyProbe probe,
        Func<IReadOnlyList<Profile>> profiles,
        Func<CoreStartPreferences> preferences,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(preferences);
        _connection = connection;
        _probe = probe;
        _profiles = profiles;
        _preferences = preferences;
        _time = time ?? TimeProvider.System;
        _connection.StatusChanged += OnConnectionStatusChanged;
    }

    public event EventHandler<AutoStatus>? StatusChanged;

    /// <summary>Замер профиля (при подборе и перепроверке) — чтобы показать задержку в списке.</summary>
    public event EventHandler<ProfileLatency>? Measured;

    /// <summary>Интервал перепроверки; <see cref="Timeout.InfiniteTimeSpan"/> — только по сбоям. Применяется при запуске.</summary>
    public TimeSpan RecheckInterval { get; set; } = TimeSpan.FromMinutes(10);

    public AutoStatus Status => Volatile.Read(ref _status);

    /// <summary>«Авто» подбирает сервер или следит за подключением.</summary>
    public bool IsActive => Status.State is AutoState.Selecting or AutoState.Connected or AutoState.Waiting;

    internal enum AutoTrigger
    {
        Recheck,
        Problem,
        Failure,
    }

    /// <summary>Подбирает сервер и подключается; при успехе дальше следит за подключением до <see cref="StopAsync"/>.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(disconnect: false).ConfigureAwait(false);

        var session = new CancellationTokenSource();
        Volatile.Write(ref _session, session);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token);

        bool connected;
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                connected = await SelectAndConnectAsync(exclude: null, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Остановлено во время подбора (StopAsync).
            return;
        }

        if (session.IsCancellationRequested)
        {
            return;
        }

        if (!connected)
        {
            SetStatus(new AutoStatus(AutoState.Failed));
            return;
        }

        var triggers = Channel.CreateUnbounded<AutoTrigger>(new UnboundedChannelOptions { SingleReader = true });
        Volatile.Write(ref _triggers, triggers);
        _loop = RunAsync(triggers, session.Token);
    }

    /// <summary>Останавливает «Авто»; <paramref name="disconnect"/> — заодно отключиться.</summary>
    public async Task StopAsync(bool disconnect)
    {
        var session = Interlocked.Exchange(ref _session, null);
        Interlocked.Exchange(ref _triggers, null)?.Writer.TryComplete();
        var loop = Interlocked.Exchange(ref _loop, null);
        if (session is not null)
        {
            await session.CancelAsync().ConfigureAwait(false);

            // Дождаться операции, которая сейчас идёт (подбор, переключение): она видит отмену и завершается.
            await _gate.WaitAsync().ConfigureAwait(false);
            _gate.Release();
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }

            session.Dispose();
        }

        if (Status.State != AutoState.Off)
        {
            SetStatus(new AutoStatus(AutoState.Off));
        }

        if (disconnect)
        {
            await _connection.DisconnectAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _connection.StatusChanged -= OnConnectionStatusChanged;
        await StopAsync(disconnect: false).ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>Одна реакция «Авто» (перепроверка, причина из лога, сбой) — для тестов без таймера.</summary>
    internal async Task HandleAsync(AutoTrigger trigger, CancellationToken cancellationToken)
    {
        var currentId = Status.ProfileId;
        var current = currentId is { } id ? _profiles().FirstOrDefault(p => p.Id == id) : null;
        var connected = current is not null && _connection.Status is { State: ConnectionState.Connected, Profile: { } active } && active.Id == current.Id;

        if (trigger == AutoTrigger.Failure || !connected)
        {
            await ReselectAsync(currentId, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (trigger == AutoTrigger.Problem)
        {
            // Причина из лога могла относиться к единичному соединению — проверяем сервер целиком.
            var check = await MeasureAsync(current!, cancellationToken).ConfigureAwait(false);
            if (check.Status == LatencyStatus.Ok)
            {
                _connection.ClearProblem();
            }
            else
            {
                await ReselectAsync(currentId, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var candidates = Candidates();
        var results = await MeasureAllAsync(candidates, progress: false, cancellationToken).ConfigureAwait(false);
        var ranked = AutoPolicy.Rank(candidates, results);
        if (results.GetValueOrDefault(current!.Id) is not { Status: LatencyStatus.Ok, Delay: { } currentDelay })
        {
            if (!await ConnectFirstAsync(ranked, currentId, cancellationToken).ConfigureAwait(false))
            {
                SetStatus(new AutoStatus(AutoState.Waiting, currentId));
            }

            return;
        }

        if (ranked.FirstOrDefault(p => p.Id != current.Id) is { } best
            && results[best.Id].Delay is { } bestDelay
            && AutoPolicy.IsNotablyFaster(bestDelay, currentDelay))
        {
            // Если лучший не подключился — следующие по скорости, текущий тоже в их числе.
            await ConnectFirstAsync(ranked, exclude: null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(Channel<AutoTrigger> triggers, CancellationToken cancellationToken)
    {
        var ticker = RecheckInterval == Timeout.InfiniteTimeSpan ? Task.CompletedTask : TickAsync(triggers.Writer, cancellationToken);
        try
        {
            while (await triggers.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Накопившиеся поводы схлопываются в самый серьёзный.
                var trigger = AutoTrigger.Recheck;
                while (triggers.Reader.TryRead(out var next))
                {
                    trigger = next > trigger ? next : trigger;
                }

                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await HandleAsync(trigger, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await ticker.ConfigureAwait(false);
        }
    }

    private async Task TickAsync(ChannelWriter<AutoTrigger> writer, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RecheckInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                writer.TryWrite(AutoTrigger.Recheck);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> SelectAndConnectAsync(Guid? exclude, CancellationToken cancellationToken)
    {
        var candidates = Candidates();
        SetStatus(new AutoStatus(AutoState.Selecting, Total: candidates.Count));
        var results = await MeasureAllAsync(candidates, progress: true, cancellationToken).ConfigureAwait(false);
        return await ConnectFirstAsync(AutoPolicy.Rank(candidates, results), exclude, cancellationToken).ConfigureAwait(false);
    }

    // Подбор во время работы: ничего не ответило — остаёмся как есть и ждём следующей перепроверки.
    private async Task ReselectAsync(Guid? exclude, CancellationToken cancellationToken)
    {
        if (!await SelectAndConnectAsync(exclude, cancellationToken).ConfigureAwait(false))
        {
            SetStatus(new AutoStatus(AutoState.Waiting, exclude));
        }
    }

    /// <summary>Подключает профили по порядку до первого удачного; <paramref name="exclude"/> пробуется последним.</summary>
    private async Task<bool> ConnectFirstAsync(IReadOnlyList<Profile> ranked, Guid? exclude, CancellationToken cancellationToken)
    {
        foreach (var profile in ranked.Where(p => p.Id != exclude).Concat(ranked.Where(p => p.Id == exclude)))
        {
            if (_connection.Status is { State: ConnectionState.Connected, Profile: { } active, Problem: null } && active.Id == profile.Id)
            {
                SetStatus(new AutoStatus(AutoState.Connected, profile.Id));
                return true;
            }

            Interlocked.Increment(ref _switching);
            try
            {
                await _connection.ConnectAsync(profile, _preferences(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _switching);
            }

            if (_connection.Status is { State: ConnectionState.Connected, Profile: { } connected } && connected.Id == profile.Id)
            {
                SetStatus(new AutoStatus(AutoState.Connected, profile.Id));
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<Profile> Candidates() => [.. _profiles().Where(AutoPolicy.IsCandidate)];

    private async Task<Dictionary<Guid, LatencyResult>> MeasureAllAsync(IReadOnlyList<Profile> candidates, bool progress, CancellationToken cancellationToken)
    {
        var results = new Dictionary<Guid, LatencyResult>();
        var measured = 0;
        using var parallel = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(candidates.Select(async profile =>
        {
            await parallel.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await MeasureAsync(profile, cancellationToken).ConfigureAwait(false);
                lock (results)
                {
                    results[profile.Id] = result;
                    measured++;
                    if (progress)
                    {
                        SetStatus(new AutoStatus(AutoState.Selecting, Measured: measured, Total: candidates.Count));
                    }
                }
            }
            finally
            {
                parallel.Release();
            }
        })).ConfigureAwait(false);
        return results;
    }

    private async Task<LatencyResult> MeasureAsync(Profile profile, CancellationToken cancellationToken)
    {
        var result = await _probe.MeasureAsync(profile, cancellationToken).ConfigureAwait(false);
        Measured?.Invoke(this, new ProfileLatency(profile.Id, result));
        return result;
    }

    // Свои переключения «Авто» не считаются сбоями; чужие события по текущему профилю — повод отреагировать.
    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        if (Volatile.Read(ref _switching) > 0 || Volatile.Read(ref _triggers) is not { } triggers)
        {
            return;
        }

        var currentId = Status.ProfileId;
        var trigger = status switch
        {
            { State: ConnectionState.Failed } when status.Profile?.Id == currentId => AutoTrigger.Failure,
            { State: ConnectionState.Disconnected } => AutoTrigger.Failure,
            { State: ConnectionState.Connected, Problem: not null } when status.Profile?.Id == currentId => AutoTrigger.Problem,
            _ => (AutoTrigger?)null,
        };

        if (trigger is { } value)
        {
            triggers.Writer.TryWrite(value);
        }
    }

    private void SetStatus(AutoStatus status)
    {
        lock (_statusLock)
        {
            Volatile.Write(ref _status, status);
            StatusChanged?.Invoke(this, status);
        }
    }
}
