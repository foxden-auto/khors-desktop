using Khors.Core.Dns;
using Khors.Core.Profiles;
using Khors.Engines;
using Khors.Engines.Processes;
using Khors.Engines.Tun;
using Khors.Ipc;
using Microsoft.Extensions.Logging;

namespace Khors.Service.Tun;

/// <summary>
/// Режим TUN в службе: не больше одного одновременно. TUN принадлежит соединению, которое его включило
/// (<c>owner</c>): закрылось соединение (окно вышло или упало) — TUN выключается, сеть работает напрямую.
/// Лог ядер и их завершение уходят владельцу событиями. Профиль и секреты в журнал службы не пишутся.
/// </summary>
public sealed partial class TunController(ITunStarter starter, ILogger<TunController> logger) : IAsyncDisposable
{
    private const int TailLines = 20;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITunRun? _run;
    private object? _owner;
    private EventHandler<CoreLogLine>? _forward;

    public bool IsRunning => Volatile.Read(ref _run) is not null;

    /// <summary>Включает TUN для <paramref name="owner"/>; работающий TUN (в том числе чужой) сначала выключается.</summary>
    public async Task<IpcPayload> StartAsync(object owner, Profile profile, string logLevel, DnsServer remoteDns, Action<IpcPayload> send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(remoteDns);
        ArgumentNullException.ThrowIfNull(send);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCurrentAsync().ConfigureAwait(false);

            ITunRun run;
            try
            {
                run = await starter.StartAsync(profile, logLevel, remoteDns, cancellationToken).ConfigureAwait(false);
            }
            catch (CoreStartException ex)
            {
                LogStartFailed(ex.Failure, ex.Core);
                return ToFailed(ex);
            }

            _forward = (_, line) => send(new TunLogEvent(line.Text));
            run.LineAdded += _forward;
            _owner = owner;
            Volatile.Write(ref _run, run);
            _ = WatchAsync(run, send);
            LogStarted(run.Core);
            return new TunStartedResponse(ToIpc(run.Core), run.SocksPort, run.HttpPort);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Счётчики трафика TUN, если он принадлежит <paramref name="owner"/>.</summary>
    public async Task<TunTrafficResponse> ReadTrafficAsync(object owner, CancellationToken cancellationToken)
    {
        var run = Volatile.Read(ref _run);
        if (run is null || !ReferenceEquals(Volatile.Read(ref _owner), owner))
        {
            return new TunTrafficResponse(Available: false);
        }

        return await run.ReadTrafficAsync(cancellationToken).ConfigureAwait(false) is { } traffic
            ? new TunTrafficResponse(Available: true, traffic.Uplink, traffic.Downlink)
            : new TunTrafficResponse(Available: false);
    }

    /// <summary>Выключает TUN, если он принадлежит <paramref name="owner"/>.</summary>
    public async Task StopAsync(object owner)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_owner, owner))
            {
                await StopCurrentAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCurrentAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static IpcCore ToIpc(CoreKind core) => core == CoreKind.Xray ? IpcCore.Xray : IpcCore.SingBox;

    private static TunFailedResponse ToFailed(CoreStartException ex) => new(
        ex.Failure switch
        {
            CoreStartFailure.ConfigNotGenerated => IpcTunFailure.ConfigNotGenerated,
            CoreStartFailure.ExecutableNotFound => IpcTunFailure.ExecutableNotFound,
            CoreStartFailure.ReadyTimeout => IpcTunFailure.ReadyTimeout,
            _ => IpcTunFailure.ExitedDuringStart,
        },
        ex.Core is { } core ? ToIpc(core) : null,
        ex.Field ?? ex.ConfigError?.Field,
        ex.ConfigError?.Code.ToString(),
        ex.ExitCode,
        ex.LogTail,
        ex.Diagnosis?.Problem.ToString());

    private async Task StopCurrentAsync()
    {
        var run = Interlocked.Exchange(ref _run, null);
        _owner = null;
        if (run is null)
        {
            return;
        }

        if (_forward is not null)
        {
            run.LineAdded -= _forward;
        }

        await run.DisposeAsync().ConfigureAwait(false);
        LogStopped();
    }

    // Ядро завершилось само — TUN снят, владелец узнаёт причину по хвосту лога.
    private async Task WatchAsync(ITunRun run, Action<IpcPayload> send)
    {
        var exit = await run.Completion.ConfigureAwait(false);
        if (exit.Expected)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_run, run))
            {
                return;
            }

            LogCrashed(exit.ExitCode);
            var tail = run.Tail(TailLines);
            await StopCurrentAsync().ConfigureAwait(false);
            send(new TunExitedEvent(exit.ExitCode, tail));
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "TUN started, core {Core}")]
    private partial void LogStarted(CoreKind core);

    [LoggerMessage(Level = LogLevel.Information, Message = "TUN stopped")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Warning, Message = "TUN failed to start: {Failure}, core {Core}")]
    private partial void LogStartFailed(CoreStartFailure failure, CoreKind? core);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TUN core exited unexpectedly with code {ExitCode}")]
    private partial void LogCrashed(int exitCode);
}
