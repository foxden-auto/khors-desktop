using Khors.Core.Profiles;
using Khors.Core.Storage;
using Khors.Engines.Diagnostics;
using Khors.Engines.Processes;
using Khors.Platform;

namespace Khors.Engines.Connection;

/// <summary>
/// Подключение в режиме «Системный прокси»: проверка профиля → ядро → сторож → системный прокси.
/// Отключение и падение ядра всегда возвращают системный прокси (CLAUDE.md, правило 9).
/// Операции выполняются по одной; событие <see cref="StatusChanged"/> приходит в фоновом потоке.
/// Пока подключено, лог ядра разбирается <see cref="CoreErrorClassifier"/>: неудачные соединения с сервером
/// попадают в <see cref="ConnectionStatus.Problem"/>.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly ICoreLauncher _launcher;
    private readonly ISystemProxy? _systemProxy;
    private readonly Action? _ensureWatchdog;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _statusLock = new();
    private ICoreSession? _session;
    private EventHandler<CoreLogLine>? _problemWatch;
    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private int _disposed;

    /// <param name="systemProxy"><c>null</c> — платформа без системного прокси (ядро запускается, прокси не меняется).</param>
    /// <param name="ensureWatchdog">Запуск сторожа перед включением прокси (<see cref="SystemProxyWatchdog.EnsureStarted"/>).</param>
    public ConnectionManager(ICoreLauncher launcher, ISystemProxy? systemProxy, Action? ensureWatchdog = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
        _systemProxy = systemProxy;
        _ensureWatchdog = ensureWatchdog;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<ConnectionStatus>? StatusChanged;

    public ConnectionStatus Status => Volatile.Read(ref _status);

    /// <summary>Лог текущего ядра (замаскированный) или <c>null</c>, если ядро не запущено.</summary>
    public CoreLogBuffer? Log => Volatile.Read(ref _session)?.Log;

    /// <summary>Счётчики трафика текущего подключения; <c>null</c> — не подключено или ядро не ответило.</summary>
    public Task<Traffic.TrafficCounters?> ReadTrafficAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _session) is { } session && Status.State == ConnectionState.Connected
            ? session.ReadTrafficAsync(cancellationToken)
            : Task.FromResult<Traffic.TrafficCounters?>(null);

    /// <summary>Подключает профиль; если уже подключён другой — сначала отключает его.</summary>
    public async Task ConnectAsync(Profile profile, CoreStartPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preferences);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);

            if (ProfileValidator.Validate(profile).FirstOrDefault(i => i.Severity == ProfileIssueSeverity.Error) is { } issue)
            {
                SetStatus(new ConnectionStatus(ConnectionState.Failed, profile, Failure: new ConnectionFailure(ConnectionFailureKind.ProfileInvalid, Issue: issue.Code)));
                return;
            }

            SetStatus(new ConnectionStatus(ConnectionState.Connecting, profile));

            ICoreSession session;
            try
            {
                session = await _launcher.StartAsync(profile, preferences, cancellationToken).ConfigureAwait(false);
            }
            catch (CoreStartException ex)
            {
                SetStatus(new ConnectionStatus(ConnectionState.Failed, profile, Failure: FromStartException(ex)));
                return;
            }
            catch (OperationCanceledException)
            {
                SetStatus(ConnectionStatus.Disconnected);
                throw;
            }

            try
            {
                // В режиме TUN трафик и так идёт через адаптер службы — системный прокси не трогаем.
                if (_systemProxy is not null && preferences.Mode == ConnectionMode.SystemProxy)
                {
                    _ensureWatchdog?.Invoke();
                    _systemProxy.Enable(SystemProxySettings.ForLocalHttp(session.HttpPort));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _systemProxy?.Restore();
                await session.DisposeAsync().ConfigureAwait(false);
                SetStatus(new ConnectionStatus(ConnectionState.Failed, profile, Failure: new ConnectionFailure(ConnectionFailureKind.SystemProxyFailed)));
                return;
            }

            Volatile.Write(ref _session, session);
            SetStatus(new ConnectionStatus(ConnectionState.Connected, profile, session.SocksPort, session.HttpPort, _time.GetUtcNow(), Core: session.Core));
            WatchForProblems(session);
            _ = WatchForCrashAsync(session);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is null && Status.State != ConnectionState.Failed)
            {
                return;
            }

            if (_session is not null)
            {
                SetStatus(Status with { State = ConnectionState.Disconnecting });
            }

            await DisconnectCoreAsync().ConfigureAwait(false);
            SetStatus(ConnectionStatus.Disconnected);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Соединения снова проходят (например, удался тест задержки) — убрать причину из статуса.</summary>
    public void ClearProblem()
    {
        lock (_statusLock)
        {
            if (_status is { State: ConnectionState.Connected, Problem: not null })
            {
                SetStatus(_status with { Problem = null });
            }
        }
    }

    /// <summary>Отключает и освобождает ресурсы. Повторный вызов ничего не делает.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task DisconnectCoreAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        StopWatchingProblems(session);

        // Сначала прокси: браузер не должен ни мгновения смотреть на остановленное ядро.
        _systemProxy?.Restore();
        await session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task WatchForCrashAsync(ICoreSession session)
    {
        var exit = await session.Completion.ConfigureAwait(false);
        if (exit.Expected)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Сессию могли уже сменить или отключить — тогда это не наше падение.
            if (Interlocked.CompareExchange(ref _session, null, session) != session)
            {
                return;
            }

            StopWatchingProblems(session);
            _systemProxy?.Restore();
            var profile = Status.Profile;
            await session.DisposeAsync().ConfigureAwait(false);
            var tail = session.Log.Tail(20);
            SetStatus(new ConnectionStatus(
                ConnectionState.Failed,
                profile,
                Failure: new ConnectionFailure(
                    ConnectionFailureKind.CoreCrashed,
                    ExitCode: exit.ExitCode,
                    LogTail: new EquatableArray<string>(tail),
                    Core: session.Core,
                    Problem: CoreErrorClassifier.Diagnose(session.Core, tail)),
                Core: session.Core));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ConnectionFailure FromStartException(CoreStartException ex) => ex.Failure switch
    {
        CoreStartFailure.ExecutableNotFound => new ConnectionFailure(ConnectionFailureKind.CoreNotFound, Core: ex.Core),
        CoreStartFailure.ServiceUnavailable => new ConnectionFailure(ConnectionFailureKind.ServiceUnavailable, Core: ex.Core),
        CoreStartFailure.ConfigNotGenerated => new ConnectionFailure(ConnectionFailureKind.UnsupportedByCore, ConfigError: ex.ConfigError, Core: ex.Core),
        _ => new ConnectionFailure(
            ConnectionFailureKind.CoreStartFailed,
            ExitCode: ex.ExitCode,
            LogTail: new EquatableArray<string>(ex.LogTail),
            Core: ex.Core,
            Problem: ex.Diagnosis ?? (ex.Core is { } core ? CoreErrorClassifier.Diagnose(core, ex.LogTail) : null)),
    };

    private void WatchForProblems(ICoreSession session)
    {
        _problemWatch = (_, line) => OnCoreLogLine(session, line);
        session.Log.LineAdded += _problemWatch;
    }

    private void StopWatchingProblems(ICoreSession session)
    {
        if (Interlocked.Exchange(ref _problemWatch, null) is { } watch)
        {
            session.Log.LineAdded -= watch;
        }
    }

    // Приходит в потоке чтения лога ядра. Проверка и смена статуса — под одной блокировкой, иначе причина
    // от старой сессии могла бы прийти в окно после «Отключение».
    private void OnCoreLogLine(ICoreSession session, CoreLogLine line)
    {
        if (CoreErrorClassifier.Classify(session.Core, line.Text) is not { } problem)
        {
            return;
        }

        lock (_statusLock)
        {
            if (ReferenceEquals(Volatile.Read(ref _session), session)
                && _status.State == ConnectionState.Connected
                && _status.Problem?.Problem != problem)
            {
                SetStatus(_status with { Problem = new CoreDiagnosis(problem, session.Core) });
            }
        }
    }

    // Событие — под блокировкой: порядок статусов у подписчиков совпадает с порядком смены (подписчик окна только ставит в очередь).
    private void SetStatus(ConnectionStatus status)
    {
        lock (_statusLock)
        {
            Volatile.Write(ref _status, status);
            StatusChanged?.Invoke(this, status);
        }
    }
}
