using System.Buffers.Binary;

namespace Khors.Ipc;

/// <summary>Нарушение протокола IPC: кадр слишком большой, оборван или не разбирается.</summary>
public sealed class IpcProtocolException : Exception
{
    public IpcProtocolException()
    {
    }

    public IpcProtocolException(string message)
        : base(message)
    {
    }

    public IpcProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Кадрирование: 4 байта длины (little-endian) и тело — JSON в UTF-8. Длина проверяется до выделения памяти,
/// поэтому клиент не может заставить службу выделить больше <see cref="MaxFrameSize"/>.
/// </summary>
public static class IpcFraming
{
    public const int MaxFrameSize = 1024 * 1024;

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (body.Length is 0 or > MaxFrameSize)
        {
            throw new IpcProtocolException($"Frame size {body.Length} is out of range.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Тело следующего кадра; <c>null</c> — собеседник закрыл соединение между кадрами.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var read = await ReadAtLeastAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < header.Length)
        {
            throw new IpcProtocolException("Connection closed inside a frame header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxFrameSize)
        {
            throw new IpcProtocolException($"Frame size {length} is out of range.");
        }

        var body = new byte[length];
        if (await ReadAtLeastAsync(stream, body, cancellationToken).ConfigureAwait(false) < length)
        {
            throw new IpcProtocolException("Connection closed inside a frame.");
        }

        return body;
    }

    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken) =>
        await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
}
