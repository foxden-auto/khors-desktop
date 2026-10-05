using Khors.Core.Profiles;
using Khors.Engines.Processes;
using Khors.Platform;

namespace Khors.Engines.Connection;

/// <summary>
/// Подключение в режиме «Системный прокси»: проверка профиля → ядро → сторож → системный прокси.
/// Отключение и падение ядра всегда возвращают системный прокси (CLAUDE.md, правило 9).
/// Операции выполняются по одной; событие <see cref="StatusChanged"/> приходит в фоновом потоке.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly ICoreLauncher _launcher;
    private readonly ISystemProxy? _systemProxy;
    private readonly Action? _ensureWatchdog;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ICoreSession? _session;
    private ConnectionStatus _status = ConnectionStatus.Disconnected;

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
                if (_systemProxy is not null)
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
            SetStatus(new ConnectionStatus(ConnectionState.Connected, profile, session.SocksPort, session.HttpPort, _time.GetUtcNow()));
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

    public async ValueTask DisposeAsync()
    {
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

            _systemProxy?.Restore();
            var profile = Status.Profile;
            await session.DisposeAsync().ConfigureAwait(false);
            SetStatus(new ConnectionStatus(
                ConnectionState.Failed,
                profile,
                Failure: new ConnectionFailure(ConnectionFailureKind.CoreCrashed, ExitCode: exit.ExitCode, LogTail: new EquatableArray<string>(session.Log.Tail(20)))));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ConnectionFailure FromStartException(CoreStartException ex) => ex.Failure switch
    {
        CoreStartFailure.ExecutableNotFound => new ConnectionFailure(ConnectionFailureKind.CoreNotFound),
        CoreStartFailure.ConfigNotGenerated => new ConnectionFailure(ConnectionFailureKind.UnsupportedByCore, ConfigError: ex.ConfigError),
        _ => new ConnectionFailure(ConnectionFailureKind.CoreStartFailed, ExitCode: ex.ExitCode, LogTail: new EquatableArray<string>(ex.LogTail)),
    };

    private void SetStatus(ConnectionStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }
}
