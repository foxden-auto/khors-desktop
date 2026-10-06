using Khors.Ipc;

namespace Khors.Engines.Geo;

/// <summary>Гео-базы службы по IPC: отдельное соединение на запрос. Служба не отвечает или старая — <c>null</c>.</summary>
public sealed class ServiceGeoClient(IIpcClientTransport transport, string clientVersion) : IServiceGeoClient
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(3);

    // Служба скачивает до ~25 МБ с запасным источником — с запасом на медленную сеть.
    private static readonly TimeSpan s_updateTimeout = TimeSpan.FromMinutes(15);

    public Task<GeoStatusResponse?> GetStatusAsync(CancellationToken cancellationToken) =>
        RequestAsync(new GetGeoStatusRequest(), s_connectTimeout, cancellationToken);

    public Task<GeoStatusResponse?> UpdateAsync(CancellationToken cancellationToken) =>
        RequestAsync(new UpdateGeoRequest(), s_updateTimeout, cancellationToken);

    private async Task<GeoStatusResponse?> RequestAsync(IpcPayload request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await using var client = await IpcClient.ConnectAsync(transport, clientVersion, s_connectTimeout, limit.Token).ConfigureAwait(false);
            return await client.RequestAsync(request, limit.Token).ConfigureAwait(false) as GeoStatusResponse;
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
