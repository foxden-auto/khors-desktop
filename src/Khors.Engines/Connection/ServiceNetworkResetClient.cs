using Khors.Ipc;

namespace Khors.Engines.Connection;

/// <summary>«Сбросить сетевые настройки KHORS» в службе (ROADMAP 4.4).</summary>
public interface IServiceNetworkResetClient
{
    /// <summary>Итог по шагам; <c>null</c> — служба не отвечает или старая (тогда сброс — через <c>khors-service.exe reset</c>).</summary>
    Task<NetworkResetResponse?> ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class ServiceNetworkResetClient(IIpcClientTransport transport, string clientVersion) : IServiceNetworkResetClient
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(5);

    // Остановка TUN и удаление адаптера могут занять десятки секунд.
    private static readonly TimeSpan s_resetTimeout = TimeSpan.FromSeconds(60);

    public async Task<NetworkResetResponse?> ResetAsync(CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(s_resetTimeout);
        try
        {
            await using var client = await IpcClient.ConnectAsync(transport, clientVersion, s_connectTimeout, limit.Token).ConfigureAwait(false);
            return await client.RequestAsync(new ResetNetworkRequest(), limit.Token).ConfigureAwait(false) as NetworkResetResponse;
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
