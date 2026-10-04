using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Khors.Engines.Tests;

/// <summary>Локальный эхо-сервер и клиенты SOCKS5 / HTTP CONNECT — проверка пути данных через ядро без интернета.</summary>
internal sealed class EchoServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public EchoServer()
    {
        _listener.Start();
        _loop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await Task.WhenAny(_loop, Task.Delay(1000));
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = EchoAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task EchoAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[1024];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
            }
        }
    }
}

internal static class ProxyClient
{
    /// <summary>SOCKS5 без аутентификации, CONNECT к 127.0.0.1:port, затем эхо.</summary>
    public static async Task<string> EchoViaSocks5Async(int proxyPort, int targetPort, string payload, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, ct);
        var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 5, 1, 0 }, ct);
        var greeting = await ReadExactAsync(stream, 2, ct);
        if (greeting[0] != 5 || greeting[1] != 0)
        {
            throw new IOException("SOCKS5 greeting rejected.");
        }

        await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(targetPort >> 8), (byte)targetPort }, ct);
        var reply = await ReadExactAsync(stream, 10, ct);
        if (reply[1] != 0)
        {
            throw new IOException($"SOCKS5 CONNECT failed: {reply[1]}");
        }

        return await RoundTripAsync(stream, payload, ct);
    }

    /// <summary>HTTP CONNECT к 127.0.0.1:port, затем эхо.</summary>
    public static async Task<string> EchoViaHttpConnectAsync(int proxyPort, int targetPort, string payload, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, ct);
        var stream = client.GetStream();

        var request = $"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);

        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, ct) == 0)
            {
                throw new IOException("Proxy closed connection.");
            }

            header.Append((char)one[0]);
        }

        if (!header.ToString().StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
        {
            throw new IOException($"HTTP CONNECT failed: {header.ToString().Split('\r')[0]}");
        }

        return await RoundTripAsync(stream, payload, ct);
    }

    private static async Task<string> RoundTripAsync(NetworkStream stream, string payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await stream.WriteAsync(bytes, ct);
        return Encoding.UTF8.GetString(await ReadExactAsync(stream, bytes.Length, ct));
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }
}
