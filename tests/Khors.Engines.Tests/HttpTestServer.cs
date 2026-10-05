using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Khors.Engines.Tests;

/// <summary>Минимальный HTTP-сервер на 127.0.0.1 для тестов: ответ строится по пути запроса.</summary>
internal sealed class HttpTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, byte[]?> _respond;
    private readonly Task _loop;

    /// <param name="respond">По пути запроса — полный HTTP-ответ; <c>null</c> — молчать (для тайм-аута).</param>
    public HttpTestServer(Func<string, byte[]?> respond)
    {
        _respond = respond;
        _listener.Start();
        _loop = ServeAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Заголовки последнего запроса (для проверки User-Agent).</summary>
    public string? LastRequest { get; private set; }

    public Uri Url(string path) => new($"http://127.0.0.1:{Port}{path}");

    public static byte[] Response(string status, string body, params string[] headers) =>
        Response(status, Encoding.UTF8.GetBytes(body), headers);

    public static byte[] Response(string status, byte[] body, params string[] headers)
    {
        var head = new StringBuilder($"HTTP/1.1 {status}\r\n");
        foreach (var header in headers)
        {
            head.Append(header).Append("\r\n");
        }

        head.Append(System.Globalization.CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        return [.. Encoding.ASCII.GetBytes(head.ToString()), .. body];
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await Task.WhenAny(_loop, Task.Delay(1000));
        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = HandleAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                LastRequest = request.ToString();
                var path = LastRequest.Split(' ', 3)[1];
                var response = _respond(path);
                if (response is null)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    return;
                }

                await stream.WriteAsync(response, _stop.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
            }
        }
    }
}
