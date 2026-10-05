using System.Net;
using System.Net.Sockets;
using System.Text;
using Khors.Core.Diagnostics;
using Khors.Core.Profiles;
using Khors.Engines.Connection;
using Khors.Engines.Latency;
using Xunit;

namespace Khors.Engines.Tests.Latency;

/// <summary>Без интернета: локальный HTTP-сервер вместо адреса теста, настоящий Xray с профилем на локальный адрес.</summary>
public class LatencyTesterTests
{
    private static readonly SecretMasker s_masker = new(Encoding.UTF8.GetBytes("latency-test"));

    private static Profile LocalProfile() => new()
    {
        Id = Guid.Empty,
        Name = "local",
        Server = new ServerEndpoint("127.0.0.1", 1),
        Protocol = new VlessSettings { Id = new Secret("3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b") },
    };

    private static void RequireXray() =>
        Assert.SkipWhen(CoreLocator.Find(CoreKind.Xray) is null, "Xray не скачан: dotnet run tools/cores/fetch-cores.cs");

    [Fact]
    public async Task TcpConnectToOpenPortSucceeds()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var result = await LatencyTester.MeasureTcpAsync(new ServerEndpoint("127.0.0.1", port), TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.Equal(LatencyStatus.Ok, result.Status);
            Assert.NotNull(result.Delay);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TcpConnectToClosedPortFails()
    {
        var port = PortAllocator.Allocate((int?)null)[0];

        var result = await LatencyTester.MeasureTcpAsync(new ServerEndpoint("127.0.0.1", port), TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(LatencyStatus.Failed, result.Status);
    }

    [Fact]
    public async Task RequestGoesThroughCoreAndMeasuresDelay()
    {
        RequireXray();
        var ct = TestContext.Current.CancellationToken;
        await using var server = new LocalHttpServer("HTTP/1.1 204 No Content");
        var launcher = new CoreKindLauncher(CoreKind.Xray, s_masker, guard: null);
        await using var session = await launcher.StartAsync(LocalProfile(), new CoreStartPreferences(null, null), ct);

        var result = await LatencyTester.MeasureThroughProxyAsync(session.HttpPort, server.Url, TimeSpan.FromSeconds(5), ct);

        Assert.Equal(LatencyStatus.Ok, result.Status);
        Assert.True(result.Delay > TimeSpan.Zero);
        Assert.NotNull(result.FirstConnection);
        Assert.Equal(2, server.Requests);
        // Запрос действительно прошёл через HTTP-вход Xray, а не напрямую.
        await WaitForAsync(() => session.Log.Snapshot().Any(l => l.Text.Contains("http-in", StringComparison.Ordinal)), ct);
    }

    [Fact]
    public async Task ErrorStatusIsFailure()
    {
        RequireXray();
        var ct = TestContext.Current.CancellationToken;
        await using var server = new LocalHttpServer("HTTP/1.1 500 Internal Server Error");
        var launcher = new CoreKindLauncher(CoreKind.Xray, s_masker, guard: null);
        await using var session = await launcher.StartAsync(LocalProfile(), new CoreStartPreferences(null, null), ct);

        var result = await LatencyTester.MeasureThroughProxyAsync(session.HttpPort, server.Url, TimeSpan.FromSeconds(5), ct);

        Assert.Equal(LatencyStatus.Failed, result.Status);
    }

    [Fact]
    public async Task SilentServerIsTimeout()
    {
        RequireXray();
        var ct = TestContext.Current.CancellationToken;
        await using var server = new LocalHttpServer(response: null);
        var launcher = new CoreKindLauncher(CoreKind.Xray, s_masker, guard: null);
        await using var session = await launcher.StartAsync(LocalProfile(), new CoreStartPreferences(null, null), ct);

        var result = await LatencyTester.MeasureThroughProxyAsync(session.HttpPort, server.Url, TimeSpan.FromSeconds(1), ct);

        Assert.Equal(LatencyStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task ProfileTestUsesTemporaryCoreAndStopsIt()
    {
        RequireXray();
        var ct = TestContext.Current.CancellationToken;
        await using var server = new LocalHttpServer("HTTP/1.1 204 No Content");
        var launcher = new RecordingLauncher(new CoreKindLauncher(CoreKind.Xray, s_masker, guard: null));

        var result = await LatencyTester.MeasureProfileAsync(LocalProfile(), launcher, server.Url, TimeSpan.FromSeconds(5), ct);

        Assert.Equal(LatencyStatus.Ok, result.Status);
        var exit = await launcher.Session!.Completion.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(exit.Expected);
    }

    [Fact]
    public async Task ProfileUnsupportedByCoreIsReportedWithoutStartingCore()
    {
        var profile = LocalProfile() with { Security = new TlsSecurity { Sni = "local.example.com", AllowInsecure = true } };
        var launcher = new RecordingLauncher(new CoreKindLauncher(CoreKind.Xray, s_masker, guard: null));

        var result = await LatencyTester.MeasureProfileAsync(profile, launcher, new Uri("http://127.0.0.1:1/"), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(LatencyStatus.Unsupported, result.Status);
        Assert.Null(launcher.Session);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var i = 0; i < 40 && !condition(); i++)
        {
            await Task.Delay(50, ct);
        }

        Assert.True(condition());
    }

    private sealed class RecordingLauncher(ICoreLauncher inner) : ICoreLauncher
    {
        public ICoreSession? Session { get; private set; }

        public async Task<ICoreSession> StartAsync(Profile profile, CoreStartPreferences preferences, CancellationToken cancellationToken)
        {
            Session = await inner.StartAsync(profile, preferences, cancellationToken);
            return Session;
        }
    }

    /// <summary>HTTP-сервер на 127.0.0.1: отвечает заданной строкой статуса или молчит (<c>null</c>).</summary>
    private sealed class LocalHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;

        public LocalHttpServer(string? response)
        {
            _listener.Start();
            _loop = ServeAsync(response);
        }

        public Uri Url => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/generate_204");

        public int Requests => Volatile.Read(ref _requests);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await Task.WhenAny(_loop, Task.Delay(1000));
            _stop.Dispose();
        }

        private async Task ServeAsync(string? response)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = HandleAsync(client, response);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private async Task HandleAsync(TcpClient client, string? response)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
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

                    Interlocked.Increment(ref _requests);
                    if (response is null)
                    {
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                }
            }
        }
    }
}
