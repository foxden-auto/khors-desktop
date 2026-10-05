using System.Collections.Concurrent;

namespace Khors.Ipc;

/// <summary>Соединение со службой прервано.</summary>
public sealed class IpcDisconnectedException : Exception
{
    public IpcDisconnectedException()
        : base("Connection to the KHORS service was closed.")
    {
    }

    public IpcDisconnectedException(string message)
        : base(message)
    {
    }

    public IpcDisconnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Служба отказала в рукопожатии: другая версия протокола.</summary>
public sealed class IpcVersionMismatchException : Exception
{
    public IpcVersionMismatchException()
    {
    }

    public IpcVersionMismatchException(string message)
        : base(message)
    {
    }

    public IpcVersionMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int? ServiceProtocolVersion { get; init; }
}

/// <summary>
/// Клиент протокола: рукопожатие, запросы с сопоставлением ответов по <see cref="IpcEnvelope.Id"/>, события службы.
/// Запросы можно отправлять из разных потоков; события приходят в потоке чтения.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IpcPayload>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _reader;
    private long _nextId;
    private int _disposed;

    private IpcClient(Stream stream)
    {
        _stream = stream;
        _reader = ReadLoopAsync();
    }

    /// <summary>Событие службы (кадр с <c>Id = 0</c>).</summary>
    public event EventHandler<IpcPayload>? EventReceived;

    /// <summary>Соединение закрыто службой или оборвалось.</summary>
    public event EventHandler? Disconnected;

    /// <summary>Ответ службы на рукопожатие.</summary>
    public HelloResponse Service { get; private set; } = new(0, string.Empty);

    /// <summary>Подключение и рукопожатие.</summary>
    /// <exception cref="TimeoutException">Служба не запущена или не отвечает.</exception>
    /// <exception cref="IpcVersionMismatchException">Служба другой версии протокола.</exception>
    public static async Task<IpcClient> ConnectAsync(IIpcClientTransport transport, string clientVersion, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var stream = await transport.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
        var client = new IpcClient(stream);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var answer = await client.RequestAsync(new HelloRequest(IpcProtocol.Version, clientVersion), limit.Token).ConfigureAwait(false);
            client.Service = answer switch
            {
                HelloResponse hello => hello,
                ErrorResponse { Code: IpcErrorCode.ProtocolMismatch } error => throw new IpcVersionMismatchException("KHORS service protocol version differs.")
                {
                    ServiceProtocolVersion = error.ServiceProtocolVersion,
                },
                _ => throw new IpcProtocolException($"Unexpected handshake answer: {answer.GetType().Name}."),
            };
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Отправляет запрос и ждёт ответа на него.</summary>
    /// <exception cref="IpcDisconnectedException">Соединение закрыто.</exception>
    public async Task<IpcPayload> RequestAsync(IpcPayload request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<IpcPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            if (_reader.IsCompleted)
            {
                throw new IpcDisconnectedException();
            }

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await IpcFraming.WriteFrameAsync(_stream, IpcSerializer.Serialize(new IpcEnvelope(id, request)), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new IpcDisconnectedException("Failed to send a request to the KHORS service.", ex);
            }
            finally
            {
                _writeLock.Release();
            }

            return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _closing.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        await _reader.ConfigureAwait(false);
        _closing.Dispose();
        _writeLock.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await IpcFraming.ReadFrameAsync(_stream, _closing.Token).ConfigureAwait(false) is { } frame)
            {
                if (!IpcSerializer.TryDeserialize(frame, out var envelope, out var badId, out _))
                {
                    // Ответ на наш запрос не разбирается — запрос завершается ошибкой, а не ждёт вечно.
                    if (badId != 0 && _pending.TryGetValue(badId, out var broken))
                    {
                        broken.TrySetException(new IpcProtocolException("Unreadable answer from the KHORS service."));
                    }

                    continue;
                }

                if (envelope!.Id == 0)
                {
                    EventReceived?.Invoke(this, envelope.Payload);
                }
                else if (_pending.TryGetValue(envelope.Id, out var waiter))
                {
                    waiter.TrySetResult(envelope.Payload);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or IpcProtocolException or ObjectDisposedException or OperationCanceledException)
        {
            // Соединение закрыто или испорчено — ниже все ожидающие запросы получают отказ.
        }

        foreach (var waiter in _pending.Values)
        {
            waiter.TrySetException(new IpcDisconnectedException());
        }

        Disconnected?.Invoke(this, EventArgs.Empty);
    }
}
