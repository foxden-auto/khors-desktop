using Khors.Core.Diagnostics;
using Khors.Core.Generators;
using Khors.Core.Profiles;
using Khors.Core.Storage;
using Khors.Engines.Diagnostics;
using Khors.Engines.Processes;
using Khors.Engines.Traffic;
using Khors.Ipc;

namespace Khors.Engines.Connection;

/// <summary>
/// Режим TUN из окна: профиль уходит службе KHORS, ядра и адаптер TUN поднимает она. Сессия держит своё
/// соединение со службой — пока оно открыто, TUN работает; закрылось (отключение, выход, падение окна) —
/// служба выключает TUN. Лог и завершение ядер приходят событиями, поэтому для <see cref="ConnectionManager"/>,
/// «Авто» и классификатора ошибок это обычная сессия ядра.
/// </summary>
public sealed class ServiceTunLauncher(IIpcClientTransport transport, string clientVersion, SecretMasker masker) : ICoreLauncher
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(5);

    public async Task<ICoreSession> StartAsync(Profile profile, CoreStartPreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preferences);
        var core = CoreSelection.For(profile);

        IpcClient client;
        try
        {
            client = await IpcClient.ConnectAsync(transport, clientVersion, s_connectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsServiceUnavailable(ex))
        {
            throw Unavailable(core, ex);
        }

        var session = new Session(client, masker);
        try
        {
            var answer = await client.RequestAsync(new StartTunRequest(StorageJson.SerializeProfile(profile), preferences.LogLevel), cancellationToken).ConfigureAwait(false);
            switch (answer)
            {
                case TunStartedResponse started:
                    session.Started(started);
                    return session;
                case TunFailedResponse failed:
                    throw ToException(failed);
                default:
                    throw Unavailable(core, null);
            }
        }
        catch (Exception ex) when (IsServiceUnavailable(ex))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw Unavailable(core, ex);
        }
        catch
        {
            // Закрытое соединение — служба выключит TUN, если он успел включиться.
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsServiceUnavailable(Exception ex) =>
        ex is TimeoutException or IOException or UnauthorizedAccessException or IpcVersionMismatchException or IpcDisconnectedException or IpcProtocolException;

    private static CoreStartException Unavailable(CoreKind core, Exception? inner) =>
        new(CoreStartFailure.ServiceUnavailable, "KHORS service is unavailable: " + (inner?.Message ?? "unexpected answer")) { Core = core };

    private static CoreKind ToCore(IpcCore core) => core == IpcCore.Xray ? CoreKind.Xray : CoreKind.SingBox;

    private static CoreStartException ToException(TunFailedResponse failed)
    {
        var core = failed.Core is { } c ? ToCore(c) : (CoreKind?)null;
        var failure = failed.Failure switch
        {
            IpcTunFailure.ConfigNotGenerated => CoreStartFailure.ConfigNotGenerated,
            IpcTunFailure.ExecutableNotFound => CoreStartFailure.ExecutableNotFound,
            IpcTunFailure.ReadyTimeout => CoreStartFailure.ReadyTimeout,
            _ => CoreStartFailure.ExitedDuringStart,
        };
        return new CoreStartException(failure, $"TUN failed to start: {failed.Failure}.", failed.ExitCode, failed.LogTail ?? [], failed.Field)
        {
            Core = core,
            ConfigError = Enum.TryParse<CoreConfigErrorCode>(failed.ConfigErrorCode, out var code) ? new CoreConfigError(code, failed.Field) : null,
            Diagnosis = core is { } known && Enum.TryParse<CoreProblem>(failed.Problem, out var problem) ? new CoreDiagnosis(problem, known) : null,
        };
    }

    private sealed class Session : ICoreSession
    {
        private static readonly TimeSpan s_stopTimeout = TimeSpan.FromSeconds(15);

        private readonly IpcClient _client;
        private readonly TaskCompletionSource<CoreExit> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stopping;

        public Session(IpcClient client, SecretMasker masker)
        {
            _client = client;
            Log = new CoreLogBuffer(masker);
            client.EventReceived += OnEvent;
            client.Disconnected += OnDisconnected;
        }

        public CoreKind Core { get; private set; }

        public int SocksPort { get; private set; }

        public int HttpPort { get; private set; }

        public CoreLogBuffer Log { get; }

        public Task<CoreExit> Completion => _completion.Task;

        public void Started(TunStartedResponse started)
        {
            Core = ToCore(started.Core);
            SocksPort = started.SocksPort;
            HttpPort = started.HttpPort;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _stopping, 1) == 0)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(s_stopTimeout);
                try
                {
                    await _client.RequestAsync(new StopTunRequest(), limit.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IpcDisconnectedException or IpcProtocolException or ObjectDisposedException)
                {
                    // Служба недоступна — закрытое ниже соединение всё равно выключит TUN.
                }

                await _client.DisposeAsync().ConfigureAwait(false);
            }

            _completion.TrySetResult(new CoreExit(0, Expected: true, DateTimeOffset.Now));
        }

        public ValueTask DisposeAsync() => new(StopAsync());

        public async Task<TrafficCounters?> ReadTrafficAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _stopping) == 1)
            {
                return null;
            }

            try
            {
                return await _client.RequestAsync(new GetTunTrafficRequest(), cancellationToken).ConfigureAwait(false) is TunTrafficResponse { Available: true } traffic
                    ? new TrafficCounters(traffic.Uplink, traffic.Downlink)
                    : null;
            }
            catch (Exception ex) when (ex is IpcDisconnectedException or IpcProtocolException or ObjectDisposedException)
            {
                return null;
            }
        }

        private void OnEvent(object? sender, IpcPayload payload)
        {
            switch (payload)
            {
                case TunLogEvent log:
                    Log.Add(CoreLogSource.StandardOutput, log.Line);
                    break;
                case TunExitedEvent exited:
                    _completion.TrySetResult(new CoreExit(exited.ExitCode, Expected: false, DateTimeOffset.Now));
                    break;
            }
        }

        // Соединение со службой оборвалось само (служба остановлена или упала) — для окна это падение подключения.
        private void OnDisconnected(object? sender, EventArgs e) =>
            _completion.TrySetResult(new CoreExit(-1, Expected: Volatile.Read(ref _stopping) == 1, DateTimeOffset.Now));
    }
}
