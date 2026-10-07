using Khors.Ipc;

namespace Khors.Engines.Connection;

/// <summary>Kill Switch службы из окна (ROADMAP 4.1).</summary>
public interface IServiceKillSwitchClient
{
    /// <summary>Состояние; <c>null</c> — служба не отвечает или старая.</summary>
    Task<KillSwitchStatusResponse?> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<KillSwitchStatusResponse?> SetAsync(IpcKillSwitchMode mode, bool allowLocalNetwork, CancellationToken cancellationToken = default);

    /// <summary>Снять блокировку «пока подключено», оставшуюся без туннеля.</summary>
    Task<KillSwitchStatusResponse?> ReleaseAsync(CancellationToken cancellationToken = default);
}

/// <summary>Kill Switch службы по IPC: отдельное соединение на запрос. Служба не отвечает или старая — <c>null</c>.</summary>
public sealed class ServiceKillSwitchClient(IIpcClientTransport transport, string clientVersion) : IServiceKillSwitchClient
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    public Task<KillSwitchStatusResponse?> GetStatusAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(new GetKillSwitchRequest(), cancellationToken);

    public Task<KillSwitchStatusResponse?> SetAsync(IpcKillSwitchMode mode, bool allowLocalNetwork, CancellationToken cancellationToken = default) =>
        RequestAsync(new SetKillSwitchRequest(mode, allowLocalNetwork), cancellationToken);

    public Task<KillSwitchStatusResponse?> ReleaseAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(new ReleaseKillSwitchRequest(), cancellationToken);

    private async Task<KillSwitchStatusResponse?> RequestAsync(IpcPayload request, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(s_timeout);
        try
        {
            await using var client = await IpcClient.ConnectAsync(transport, clientVersion, s_timeout, limit.Token).ConfigureAwait(false);
            return await client.RequestAsync(request, limit.Token).ConfigureAwait(false) as KillSwitchStatusResponse;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException
            or IpcDisconnectedException or IpcProtocolException or IpcVersionMismatchException)
        {
            return null;
        }
    }
}
