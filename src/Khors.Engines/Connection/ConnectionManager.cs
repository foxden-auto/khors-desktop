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
/// Подключение, оборвавшееся само (ядро упало, служба потеряла TUN), восстанавливается с паузами
/// <see cref="ReconnectPolicy"/> (ROADMAP 4.3), если это разрешает <see cref="ReconnectEnabled"/>; без сети
/// (<see cref="IsNetworkAvailable"/>) попытки ждут её. Любое подключение или отключение извне прерывает попытки.
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
    private CoreStartPreferences? _preferences;
    private CancellationTokenSource? _reconnect;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _lastAttempt;
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

    /// <summary>Переподключаться ли после обрыва (настройка и «Авто», у которого своя реакция на обрыв); <c>null</c> — нет.</summary>
    public Func<bool>? ReconnectEnabled { get; set; }

    /// <summary>Есть ли сеть; без неё попытки переподключения ждут <see cref="RetryNow"/>.</summary>
    public Func<bool> IsNetworkAvailable { get; set; } = static () => true;

    /// <summary>Пауза между попытками (для тестов — подмена).</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    public ConnectionStatus Status => Volatile.Read(ref _status);

    /// <summary>Лог текущего ядра (замаскированный) или <c>null</c>, если ядро не запущено.</summary>
    public CoreLogBuffer? Log => Volatile.Read(ref _session)?.Log;

    /// <summary>Счётчики трафика текущего подключения; <c>null</c> — не подключено или ядро не ответило.</summary>
    public Task<Traffic.TrafficCounters?> ReadTrafficAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _session) is { } session && Status.State == ConnectionState.Connected
            ? session.ReadTrafficAsync(cancellationToken)
            : Task.FromResult<Traffic.TrafficCounters?>(null);

    /// <summary>
    /// Подключает профиль; если уже подключён другой — сначала отключает его. Смена сервера в режиме TUN (из TUN в TUN) —
    /// без промежутка: новое подключение поднимается раньше, чем закрывается старое, и служба заменяет TUN, не снимая
    /// блокировку Kill Switch (ROADMAP 4.5) — иначе между отключением и подключением трафик ушёл бы напрямую.
    /// </summary>
    public async Task ConnectAsync(Profile profile, CoreStartPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preferences);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ICoreSession? replaced = null;
        try
        {
            CancelReconnect();
            _lastAttempt = 0;
            var issue = ProfileValidator.Validate(profile).FirstOrDefault(i => i.Severity == ProfileIssueSeverity.Error);
            if (issue is null && _session is { } current && _preferences?.Mode == ConnectionMode.Tun && preferences.Mode == ConnectionMode.Tun)
            {
                Volatile.Write(ref _session, null);
                StopWatchingProblems(current);
                replaced = current;
            }
            else
            {
                await DisconnectCoreAsync().ConfigureAwait(false);
            }

            if (issue is not null)
            {
                SetStatus(new ConnectionStatus(ConnectionState.Failed, profile, Failure: new ConnectionFailure(ConnectionFailureKind.ProfileInvalid, Issue: issue.Code)));
                return;
            }

            SetStatus(new ConnectionStatus(ConnectionState.Connecting, profile));

            ConnectionFailure? failure;
            try
            {
                failure = await StartSessionAsync(profile, preferences, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SetStatus(ConnectionStatus.Disconnected);
                throw;
            }

            if (failure is not null)
            {
                SetStatus(new ConnectionStatus(ConnectionState.Failed, profile, Failure: failure));
            }
        }
        finally
        {
            // Прежняя сессия TUN — после запуска новой: служба уже заменила TUN, её команда остановки ничего не снимет.
            if (replaced is not null)
            {
                await replaced.DisposeAsync().ConfigureAwait(false);
            }

            _gate.Release();
        }
    }

    /// <summary>
    /// Проверка после смены сети или пробуждения не прошла: ядро перезапускается тем же профилем и режимом. В режиме TUN
    /// новое подключение поднимается раньше, чем закрывается старое, — служба заменяет TUN, не снимая блокировку
    /// Kill Switch. Не поднялось — дальше как при обрыве (<see cref="ReconnectPolicy"/>).
    /// </summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status is not { State: ConnectionState.Connected, Profile: { } profile } || _session is not { } old || _preferences is not { } preferences)
            {
                return;
            }

            Volatile.Write(ref _session, null);
            StopWatchingProblems(old);
            var tun = preferences.Mode == ConnectionMode.Tun;
            if (!tun)
            {
                _systemProxy?.Restore();
                await old.DisposeAsync().ConfigureAwait(false);
            }

            SetStatus(new ConnectionStatus(ConnectionState.Reconnecting, profile, Core: old.Core, Reconnect: new ReconnectProgress(1)));
            ConnectionFailure? failure;
            try
            {
                failure = await StartSessionAsync(profile, preferences, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (tun)
                {
                    await old.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (failure is not null)
            {
                AfterFailedAttempt(profile, preferences, failure, attempt: 1);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Сеть появилась или сменилась: попытка переподключения — сейчас, не дожидаясь паузы.</summary>
    public void RetryNow() => Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>
    /// Смена режима «Системный прокси» ↔ TUN без отключения пользователем (ROADMAP 3.7): тот же профиль переподключается
    /// с <paramref name="target"/>. Старый режим снимается до запуска нового (прокси восстановлен, TUN выключен), поэтому
    /// режимы не накладываются. Новый режим не поднялся — подключение возвращается в <paramref name="previous"/>,
    /// чтобы пользователь не остался без VPN; причина — в результате.
    /// </summary>
    public async Task<ModeSwitchResult> SwitchModeAsync(CoreStartPreferences target, CoreStartPreferences previous, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(previous);
        if (Status is not { State: ConnectionState.Connected, Profile: { } profile })
        {
            return new ModeSwitchResult(ModeSwitchOutcome.NotConnected);
        }

        await ConnectAsync(profile, target, cancellationToken).ConfigureAwait(false);
        if (Status.State == ConnectionState.Connected)
        {
            return new ModeSwitchResult(ModeSwitchOutcome.Switched);
        }

        var failure = Status.Failure;
        await ConnectAsync(profile, previous, cancellationToken).ConfigureAwait(false);
        return new ModeSwitchResult(Status.State == ConnectionState.Connected ? ModeSwitchOutcome.Reverted : ModeSwitchOutcome.Failed, failure);
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var reconnecting = CancelReconnect();
            _lastAttempt = 0;
            if (_session is null && Status.State != ConnectionState.Failed && !reconnecting)
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
            var connectedAt = Status.ConnectedAt;
            await session.DisposeAsync().ConfigureAwait(false);
            var tail = session.Log.Tail(20);
            var failure = new ConnectionFailure(
                ConnectionFailureKind.CoreCrashed,
                ExitCode: exit.ExitCode,
                LogTail: new EquatableArray<string>(tail),
                Core: session.Core,
                Problem: CoreErrorClassifier.Diagnose(session.Core, tail));
            if (profile is not null && _preferences is { } preferences && ReconnectEnabled?.Invoke() == true)
            {
                // Подключение, восстановленное недавно, снова оборвалось — продолжаем счёт попыток, а не начинаем с паузы в 1 с.
                var stable = connectedAt is { } since && _time.GetUtcNow() - since >= ReconnectPolicy.StableAfter;
                BeginReconnect(profile, preferences, failure, stable ? 1 : _lastAttempt + 1);
                return;
            }

            SetStatus(new ConnectionStatus(ConnectionState.Failed, profile, Failure: failure, Core: session.Core));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ядро → системный прокси → «Подключено». <c>null</c> — подключено, иначе причина неудачи. Вызывается под <see cref="_gate"/>.</summary>
    private async Task<ConnectionFailure?> StartSessionAsync(Profile profile, CoreStartPreferences preferences, CancellationToken cancellationToken)
    {
        ICoreSession session;
        try
        {
            session = await _launcher.StartAsync(profile, preferences, cancellationToken).ConfigureAwait(false);
        }
        catch (CoreStartException ex)
        {
            return FromStartException(ex);
        }

        try
        {
            // В режиме TUN трафик и так идёт через адаптер службы — системный прокси не трогаем.
            if (_systemProxy is not null && preferences.Mode == ConnectionMode.SystemProxy)
            {
                _ensureWatchdog?.Invoke();
                // Снятый «Обход локальной сети» — частные сети тоже идут в ядро, иначе Windows отправила бы их мимо прокси.
                _systemProxy.Enable(SystemProxySettings.ForLocalHttp(session.HttpPort, preferences.Routing?.BypassLan ?? true));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _systemProxy?.Restore();
            await session.DisposeAsync().ConfigureAwait(false);
            return new ConnectionFailure(ConnectionFailureKind.SystemProxyFailed);
        }

        Volatile.Write(ref _session, session);
        _preferences = preferences;
        SetStatus(new ConnectionStatus(ConnectionState.Connected, profile, session.SocksPort, session.HttpPort, _time.GetUtcNow(), Core: session.Core, MissingGeo: new EquatableArray<string>(session.MissingGeo)));
        WatchForProblems(session);
        _ = WatchForCrashAsync(session);
        return null;
    }

    /// <summary>Попытка не удалась: следующая — если повтор может помочь, иначе «Ошибка подключения». Под <see cref="_gate"/>.</summary>
    private void AfterFailedAttempt(Profile profile, CoreStartPreferences preferences, ConnectionFailure failure, int attempt)
    {
        if (ReconnectPolicy.IsRetryable(failure.Kind) && ReconnectEnabled?.Invoke() == true)
        {
            BeginReconnect(profile, preferences, failure, attempt + 1);
        }
        else
        {
            _lastAttempt = 0;
            SetStatus(new ConnectionStatus(ConnectionState.Failed, profile, Failure: failure, Core: failure.Core));
        }
    }

    /// <summary>Запускает ожидание и попытку номер <paramref name="attempt"/>. Под <see cref="_gate"/>.</summary>
    private void BeginReconnect(Profile profile, CoreStartPreferences preferences, ConnectionFailure failure, int attempt)
    {
        CancelReconnect();
        var cancellation = new CancellationTokenSource();
        _reconnect = cancellation;
        _lastAttempt = attempt;
        _ = ReconnectAsync(profile, preferences, failure, attempt, cancellation.Token);
    }

    /// <summary>Прерывает попытки переподключения; <c>true</c> — они шли. Под <see cref="_gate"/>.</summary>
    private bool CancelReconnect()
    {
        if (Interlocked.Exchange(ref _reconnect, null) is not { } cancellation)
        {
            return false;
        }

        cancellation.Cancel();
        cancellation.Dispose();
        return true;
    }

    private async Task ReconnectAsync(Profile profile, CoreStartPreferences preferences, ConnectionFailure failure, int attempt, CancellationToken cancellationToken)
    {
        try
        {
            await WaitForAttemptAsync(profile, failure, attempt, cancellationToken).ConfigureAwait(false);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!IsNetworkAvailable())
            {
                // Сеть пропала, пока ждали очереди: та же попытка, когда она появится.
                BeginReconnect(profile, preferences, failure, attempt);
                return;
            }

            // Дальше попытку прерывать нечем: отключение и новое подключение ждут её конца в очереди.
            CancelReconnect();
            SetStatus(new ConnectionStatus(ConnectionState.Reconnecting, profile, Failure: failure, Core: failure.Core, Reconnect: new ReconnectProgress(attempt)));
            ConnectionFailure? next;
            try
            {
                next = await StartSessionAsync(profile, preferences, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Попытка идёт в фоне — непредвиденная ошибка не должна оставить статус «Переподключение» навсегда.
                next = new ConnectionFailure(ConnectionFailureKind.CoreStartFailed, Core: failure.Core);
            }

            if (next is not null)
            {
                AfterFailedAttempt(profile, preferences, next, attempt);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Пауза перед попыткой; без сети — ожидание сети. <see cref="RetryNow"/> прерывает паузу.</summary>
    private async Task WaitForAttemptAsync(Profile profile, ConnectionFailure failure, int attempt, CancellationToken cancellationToken)
    {
        var delay = ReconnectPolicy.DelayBefore(attempt);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wake = Volatile.Read(ref _wake).Task;
            var available = IsNetworkAvailable();
            var progress = available
                ? new ReconnectProgress(attempt, _time.GetUtcNow() + delay)
                : new ReconnectProgress(attempt, WaitingForNetwork: true);
            SetReconnectStatus(new ConnectionStatus(ConnectionState.Reconnecting, profile, Failure: failure, Core: failure.Core, Reconnect: progress), cancellationToken);

            using var pause = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sleep = available ? (Delay ?? DefaultDelay)(delay, pause.Token) : Task.Delay(Timeout.InfiniteTimeSpan, pause.Token);
            var woken = await Task.WhenAny(sleep, wake).ConfigureAwait(false) == wake;
            await pause.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (IsNetworkAvailable() && (available || woken))
            {
                return;
            }
        }
    }

    private Task DefaultDelay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, _time, cancellationToken);

    // Статус ожидания — только пока попытки не прерваны (иначе он мог бы прийти после «Отключено»).
    private void SetReconnectStatus(ConnectionStatus status, CancellationToken cancellationToken)
    {
        lock (_statusLock)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetStatus(status);
            }
        }
    }

    private static ConnectionFailure FromStartException(CoreStartException ex) => ex.Failure switch
    {
        CoreStartFailure.ExecutableNotFound => new ConnectionFailure(ConnectionFailureKind.CoreNotFound, Core: ex.Core),
        CoreStartFailure.ServiceUnavailable => new ConnectionFailure(ConnectionFailureKind.ServiceUnavailable, Core: ex.Core),
        CoreStartFailure.KillSwitchFailed => new ConnectionFailure(ConnectionFailureKind.KillSwitchFailed, Core: ex.Core),
        CoreStartFailure.ConfigNotGenerated => new ConnectionFailure(ConnectionFailureKind.UnsupportedByCore, ConfigError: ex.ConfigError, Core: ex.Core),
        _ => new ConnectionFailure(
            ex.Failure == CoreStartFailure.ReadyTimeout ? ConnectionFailureKind.CoreNotReady : ConnectionFailureKind.CoreStartFailed,
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
