using System.Threading.Channels;
using Khors.Ipc;
using Khors.Service.Tun;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Khors.Service.Ipc;

/// <summary>
/// Сервер IPC службы: принимает соединения транспорта и обслуживает каждое в своей задаче.
/// Испорченный кадр закрывает только это соединение; число одновременных соединений ограничено.
/// </summary>
public sealed partial class IpcServer(IIpcServerTransport transport, ServiceInfo info, TunController tun, ILogger<IpcServer> logger) : BackgroundService
{
    public const int MaxConnections = 8;

    // Очередь исходящих кадров соединения: ответы ждут места, события лога при переполнении отбрасываются
    // (клиент, который не читает, не должен раздувать память службы).
    private const int OutgoingCapacity = 4096;

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

    /// <summary>
    /// Обслуживание одного соединения (открыто для тестов без хоста). Запросы — по одному; ответы и события
    /// уходят через одну очередь, поэтому строки лога не перемешиваются и не обгоняют ответы.
    /// </summary>
    internal static async Task ServeConnectionAsync(Stream stream, Func<Action<IpcPayload>, ServiceRequestHandler> createHandler, CancellationToken cancellationToken)
    {
        var outgoing = Channel.CreateBounded<IpcEnvelope>(new BoundedChannelOptions(OutgoingCapacity) { SingleReader = true });
        var writer = WriteLoopAsync(stream, outgoing.Reader, cancellationToken);
        var handler = createHandler(payload => outgoing.Writer.TryWrite(new IpcEnvelope(0, payload)));
        try
        {
            while (await IpcFraming.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false) is { } frame)
            {
                var answer = IpcSerializer.TryDeserialize(frame, out var envelope, out var id, out var error)
                    ? new IpcEnvelope(envelope!.Id, await handler.HandleAsync(envelope.Payload, cancellationToken).ConfigureAwait(false))
                    : new IpcEnvelope(id, new ErrorResponse(error));
                await outgoing.Writer.WriteAsync(answer, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Соединение закрыто — выключить его TUN, затем дописать очередь.
            await handler.DisposeAsync().ConfigureAwait(false);
            outgoing.Writer.TryComplete();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Клиент уже отключился.
            }
        }
    }

    // Запись сломалась — соединение закрывается, чтобы цикл чтения тоже завершился, а не принимал запросы без ответов.
    private static async Task WriteLoopAsync(Stream stream, ChannelReader<IpcEnvelope> outgoing, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var envelope in outgoing.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await IpcFraming.WriteFrameAsync(stream, IpcSerializer.Serialize(envelope), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ServeAsync(IIpcConnection connection, CancellationToken cancellationToken)
    {
        await using (connection.ConfigureAwait(false))
        {
            LogConnected(connection.Peer.ProcessId, connection.Peer.UserName ?? "?");
            try
            {
                await ServeConnectionAsync(connection.Stream, send => new ServiceRequestHandler(info, tun, send), cancellationToken).ConfigureAwait(false);
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
