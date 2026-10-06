using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Khors.Core.Generators;
using Khors.Core.Profiles;
using Khors.Core.Storage;
using Khors.Engines;
using Khors.Engines.Connection;
using Khors.Engines.Processes;
using Khors.Engines.Traffic;
using Khors.Engines.Tun;
using Khors.Ipc;
using Khors.Service.Ipc;
using Khors.Service.Tun;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Khors.Service.Tests;

/// <summary>Подменный запуск TUN: настоящий TUN требует прав SYSTEM.</summary>
internal sealed class FakeTunStarter : ITunStarter
{
    public CoreStartException? FailWith { get; set; }

    public ConcurrentQueue<FakeTunRun> Runs { get; } = new();

    public Task<ITunRun> StartAsync(Profile profile, string logLevel, CancellationToken cancellationToken)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        var run = new FakeTunRun(CoreSelection.For(profile));
        Runs.Enqueue(run);
        return Task.FromResult<ITunRun>(run);
    }
}

internal sealed class FakeTunRun(CoreKind core) : ITunRun
{
    private readonly TaskCompletionSource<CoreExit> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _lines = [];

    public event EventHandler<CoreLogLine>? LineAdded;

    public CoreKind Core { get; } = core;

    public int SocksPort => 30808;

    public int HttpPort => 30809;

    public Task<CoreExit> Completion => _completion.Task;

    public bool Disposed { get; private set; }

    public TrafficCounters? Traffic { get; set; }

    public Task<TrafficCounters?> ReadTrafficAsync(CancellationToken cancellationToken = default) => Task.FromResult(Traffic);

    public IReadOnlyList<string> Tail(int count) => [.. _lines.TakeLast(count)];

    public void Log(string text)
    {
        _lines.Add(text);
        LineAdded?.Invoke(this, new CoreLogLine(DateTimeOffset.Now, CoreLogSource.StandardOutput, text));
    }

    public void Crash(int code) => _completion.TrySetResult(new CoreExit(code, Expected: false, DateTimeOffset.Now));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _completion.TrySetResult(new CoreExit(0, Expected: true, DateTimeOffset.Now));
        return ValueTask.CompletedTask;
    }
}

public sealed class TunServiceTests : IAsyncDisposable
{
    private static readonly ServiceInfo s_info = new("1.2.3", DateTimeOffset.UnixEpoch);

    private readonly FakeTunStarter _starter = new();
    private readonly TunController _tun;
    private readonly List<IAsyncDisposable> _cleanup = [];

    public TunServiceTests() => _tun = new TunController(_starter, NullLogger<TunController>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Hysteria2Json() => StorageJson.SerializeProfile(new Profile
    {
        Id = Guid.NewGuid(),
        Name = "hy2",
        Server = new ServerEndpoint("hy.example.com", 443),
        Protocol = new Hysteria2Settings { Password = new Secret("Hy2Pa55") },
        Security = new TlsSecurity { Sni = "hy.example.com" },
    });

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _cleanup)
        {
            await item.DisposeAsync();
        }

        await _tun.DisposeAsync();
    }

    [Fact]
    public async Task StartLogAndStop()
    {
        var (client, events) = await ConnectAsync();

        var started = Assert.IsType<TunStartedResponse>(await client.RequestAsync(new StartTunRequest(Hysteria2Json(), "warning"), Ct));
        Assert.Equal(new TunStartedResponse(IpcCore.SingBox, 30808, 30809), started);

        var run = Assert.Single(_starter.Runs);
        run.Log("ERROR connection: open connection to {host-1a2b3c} using outbound/hysteria2[proxy]: authentication failed");
        await WaitForAsync(() => events.Contains(new TunLogEvent("ERROR connection: open connection to {host-1a2b3c} using outbound/hysteria2[proxy]: authentication failed")));

        Assert.IsType<OkResponse>(await client.RequestAsync(new StopTunRequest(), Ct));
        Assert.True(run.Disposed);
        Assert.False(_tun.IsRunning);
    }

    [Theory]
    [InlineData("{}", "warning")]
    [InlineData("not json", "warning")]
    [InlineData(null, "trace; rm -rf")]
    public async Task BadStartParametersAreRejected(string? profile, string logLevel)
    {
        var (client, _) = await ConnectAsync();

        var answer = await client.RequestAsync(new StartTunRequest(profile ?? Hysteria2Json(), logLevel), Ct);

        Assert.Equal(new ErrorResponse(IpcErrorCode.BadRequest), answer);
        Assert.Empty(_starter.Runs);
    }

    [Fact]
    public async Task TrafficIsReportedToOwnerOnly()
    {
        var (owner, _) = await ConnectAsync();
        var (other, _) = await ConnectAsync();
        Assert.Equal(new TunTrafficResponse(Available: false), await owner.RequestAsync(new GetTunTrafficRequest(), Ct));

        await owner.RequestAsync(new StartTunRequest(Hysteria2Json(), "warning"), Ct);
        var run = Assert.Single(_starter.Runs);
        run.Traffic = new TrafficCounters(1200, 34000);

        Assert.Equal(new TunTrafficResponse(true, 1200, 34000), await owner.RequestAsync(new GetTunTrafficRequest(), Ct));
        Assert.Equal(new TunTrafficResponse(Available: false), await other.RequestAsync(new GetTunTrafficRequest(), Ct));

        run.Traffic = null;
        Assert.Equal(new TunTrafficResponse(Available: false), await owner.RequestAsync(new GetTunTrafficRequest(), Ct));
    }

    [Fact]
    public async Task ClosedConnectionStopsItsTun()
    {
        var (client, _) = await ConnectAsync();
        await client.RequestAsync(new StartTunRequest(Hysteria2Json(), "warning"), Ct);
        var run = Assert.Single(_starter.Runs);

        await client.DisposeAsync();

        await WaitForAsync(() => run.Disposed);
        Assert.False(_tun.IsRunning);
    }

    [Fact]
    public async Task CoreCrashIsReportedToOwner()
    {
        var (client, events) = await ConnectAsync();
        await client.RequestAsync(new StartTunRequest(Hysteria2Json(), "warning"), Ct);
        var run = Assert.Single(_starter.Runs);
        run.Log("FATAL[0000] start service: start inbound/tun[tun-in]: configure tun interface: Access is denied.");

        run.Crash(1);

        await WaitForAsync(() => events.OfType<TunExitedEvent>().Any());
        var exited = events.OfType<TunExitedEvent>().Single();
        Assert.Equal(1, exited.ExitCode);
        Assert.Contains(exited.LogTail, l => l.Contains("configure tun interface", StringComparison.Ordinal));
        Assert.False(_tun.IsRunning);
    }

    [Fact]
    public async Task StartFailureIsDescribed()
    {
        _starter.FailWith = new CoreStartException(CoreStartFailure.ConfigNotGenerated, "unsupported", field: "transport")
        {
            Core = CoreKind.SingBox,
            ConfigError = new CoreConfigError(CoreConfigErrorCode.UnsupportedFeature, "transport"),
        };
        var (client, _) = await ConnectAsync();

        var answer = await client.RequestAsync(new StartTunRequest(Hysteria2Json(), "warning"), Ct);

        var failed = Assert.IsType<TunFailedResponse>(answer);
        Assert.Equal(IpcTunFailure.ConfigNotGenerated, failed.Failure);
        Assert.Equal(IpcCore.SingBox, failed.Core);
        Assert.Equal("transport", failed.Field);
        Assert.Equal("UnsupportedFeature", failed.ConfigErrorCode);
        Assert.Empty(failed.LogTail ?? []);
    }

    [Fact]
    public async Task NewStartReplacesRunningTun()
    {
        var (first, _) = await ConnectAsync();
        var (second, _) = await ConnectAsync();
        await first.RequestAsync(new StartTunRequest(Hysteria2Json(), "warning"), Ct);
        _starter.Runs.TryPeek(out var firstRun);

        await second.RequestAsync(new StartTunRequest(Hysteria2Json(), "warning"), Ct);

        Assert.True(firstRun!.Disposed);
        Assert.Equal(2, _starter.Runs.Count);
        Assert.True(_tun.IsRunning);

        // Первое соединение больше не владелец — его отключение не выключает чужой TUN.
        await first.DisposeAsync();
        await Task.Delay(200, Ct);
        Assert.True(_tun.IsRunning);
    }

    private async Task<(IpcClient Client, ConcurrentQueue<IpcPayload> Events)> ConnectAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var clientSocket = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(Ct);
        await clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Ct);
        var serverSocket = await accept;

        var serverStream = new NetworkStream(serverSocket.Client, ownsSocket: true);
        var session = IpcServer.ServeConnectionAsync(serverStream, send => new ServiceRequestHandler(s_info, _tun, send), Ct);
        _cleanup.Add(new SessionCleanup(serverStream, session));

        var events = new ConcurrentQueue<IpcPayload>();
        var client = await IpcClient.ConnectAsync(new FixedTransport(new NetworkStream(clientSocket.Client, ownsSocket: true)), "0.1.0", TimeSpan.FromSeconds(5), Ct);
        client.EventReceived += (_, e) => events.Enqueue(e);
        _cleanup.Add(client);
        return (client, events);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(condition());
    }

    private sealed class FixedTransport(Stream stream) : IIpcClientTransport
    {
        public Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(stream);
    }

    private sealed class SessionCleanup(Stream stream, Task session) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync();
            try
            {
                await session.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or IpcProtocolException)
            {
            }
        }
    }
}
