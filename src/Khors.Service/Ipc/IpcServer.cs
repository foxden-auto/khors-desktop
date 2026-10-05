using Khors.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Khors.Service.Ipc;

/// <summary>
/// Сервер IPC службы: принимает соединения транспорта и обслуживает каждое в своей задаче.
/// Испорченный кадр закрывает только это соединение; число одновременных соединений ограничено.
/// </summary>
public sealed partial class IpcServer(IIpcServerTransport transport, ServiceInfo info, ILogger<IpcServer> logger) : BackgroundService
{
    public const int MaxConnections = 8;

    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);

    public override void Dispose()
    {
        _slots.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sessions = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _slots.WaitAsync(stoppingToken).ConfigureAwait(false);
                IIpcConnection connection;
                try
                {
                    connection = await transport.AcceptAsync(stoppingToken).ConfigureAwait(false);
                }
                catch
                {
                    _slots.Release();
                    throw;
                }

                sessions.RemoveAll(t => t.IsCompleted);
                sessions.Add(ServeAsync(connection, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(sessions).ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Обслуживание одного соединения (открыто для тестов без хоста).</summary>
    internal static async Task ServeConnectionAsync(Stream stream, ServiceRequestHandler handler, CancellationToken cancellationToken)
    {
        while (await IpcFraming.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false) is { } frame)
        {
            var answer = IpcSerializer.TryDeserialize(frame, out var envelope, out var id, out var error)
                ? new IpcEnvelope(envelope!.Id, handler.Handle(envelope.Payload))
                : new IpcEnvelope(id, new ErrorResponse(error));
            await IpcFraming.WriteFrameAsync(stream, IpcSerializer.Serialize(answer), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(IIpcConnection connection, CancellationToken cancellationToken)
    {
        await using (connection.ConfigureAwait(false))
        {
            LogConnected(connection.Peer.ProcessId, connection.Peer.UserName ?? "?");
            try
            {
                await ServeConnectionAsync(connection.Stream, new ServiceRequestHandler(info), cancellationToken).ConfigureAwait(false);
            }
            catch (IpcProtocolException ex)
            {
                LogProtocolError(connection.Peer.ProcessId, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException || (ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // Клиент отключился или служба останавливается.
            }
            finally
            {
                _slots.Release();
                LogDisconnected(connection.Peer.ProcessId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC client connected: pid {ProcessId}, user {UserName}")]
    private partial void LogConnected(int processId, string userName);

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC client disconnected: pid {ProcessId}")]
    private partial void LogDisconnected(int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IPC protocol error from pid {ProcessId}: {Reason}")]
    private partial void LogProtocolError(int processId, string reason);
}
