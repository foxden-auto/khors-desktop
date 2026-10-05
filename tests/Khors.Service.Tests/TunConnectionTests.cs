using System.Net;
using System.Net.Sockets;
using System.Text;
using Khors.Core.Diagnostics;
using Khors.Core.Profiles;
using Khors.Core.Storage;
using Khors.Engines;
using Khors.Engines.Connection;
using Khors.Engines.Diagnostics;
using Khors.Engines.Processes;
using Khors.Ipc;
using Khors.Platform;
using Khors.Service.Ipc;
using Khors.Service.Tun;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Khors.Service.Tests;

/// <summary>
/// Сквозной путь режима TUN без прав SYSTEM: окно (<see cref="ConnectionManager"/> + <see cref="ServiceTunLauncher"/>)
/// ↔ протокол IPC ↔ сессия службы ↔ <see cref="TunController"/> с подменным запуском ядер.
/// </summary>
public sealed class TunConnectionTests : IAsyncDisposable
{
    private static readonly CoreStartPreferences s_tun = new(LogLevel: "warning", Mode: ConnectionMode.Tun);

    private readonly FakeTunStarter _starter = new();
    private readonly TunController _tun;
    private readonly ServiceTransport _transport;
    private readonly RecordingProxy _proxy = new();
    private readonly ConnectionManager _connection;

    public TunConnectionTests()
    {
        _tun = new TunController(_starter, NullLogger<TunController>.Instance);
        _transport = new ServiceTransport(_tun);
        var masker = new SecretMasker(Encoding.UTF8.GetBytes("tun-connection"));
        var launcher = new SelectingCoreLauncher(masker, guard: null, new ServiceTunLauncher(_transport, "0.1.0", masker));
        _connection = new ConnectionManager(launcher, _proxy);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _transport.DisposeAsync();
        await _tun.DisposeAsync();
    }

    private static Profile Hysteria2() => new()
    {
        Id = Guid.NewGuid(),
        Name = "hy2",
        Server = new ServerEndpoint("hy.example.com", 443),
        Protocol = new Hysteria2Settings { Password = new Secret("Hy2Pa55") },
        Security = new TlsSecurity { Sni = "hy.example.com" },
    };

    [Fact]
    public async Task ConnectInTunModeLeavesSystemProxyAlone()
    {
        await _connection.ConnectAsync(Hysteria2(), s_tun, Ct);

        Assert.Equal(ConnectionState.Connected, _connection.Status.State);
        Assert.Equal(CoreKind.SingBox, _connection.Status.Core);
        Assert.Equal(30809, _connection.Status.HttpPort);
        Assert.Empty(_proxy.Calls);
        Assert.True(_tun.IsRunning);

        await _connection.DisconnectAsync();

        Assert.True(Assert.Single(_starter.Runs).Disposed);
        Assert.False(_tun.IsRunning);
        Assert.Equal(["restore"], _proxy.Calls);
    }

    [Fact]
    public async Task CoreLogReachesWindowAndProblemIsDetected()
    {
        await _connection.ConnectAsync(Hysteria2(), s_tun, Ct);
        var run = Assert.Single(_starter.Runs);

        run.Log("ERROR[0003] [1 8ms] connection: open connection to {host-1a2b3c}:443 using outbound/hysteria2[proxy]: authentication failed, status code: 404");

        await WaitForAsync(() => _connection.Status.Problem is not null);
        Assert.Equal(new CoreDiagnosis(CoreProblem.AuthenticationFailed, CoreKind.SingBox), _connection.Status.Problem);
        Assert.Contains(_connection.Log!.Tail(5), l => l.Contains("authentication failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CoreCrashInServiceIsConnectionFailure()
    {
        await _connection.ConnectAsync(Hysteria2(), s_tun, Ct);
        var run = Assert.Single(_starter.Runs);
        run.Log("FATAL[0010] something broke");

        run.Crash(2);

        await WaitForAsync(() => _connection.Status.State == ConnectionState.Failed);
        Assert.Equal(ConnectionFailureKind.CoreCrashed, _connection.Status.Failure!.Kind);
        Assert.Equal(2, _connection.Status.Failure.ExitCode);
        Assert.False(_tun.IsRunning);
    }

    [Fact]
    public async Task StartFailureCarriesReason()
    {
        _starter.FailWith = new CoreStartException(
            CoreStartFailure.ExitedDuringStart,
            "exited",
            1,
            ["FATAL[0000] start service: start inbound/tun[tun-in]: configure tun interface: Access is denied."])
        {
            Core = CoreKind.SingBox,
        };

        await _connection.ConnectAsync(Hysteria2(), s_tun, Ct);

        var failure = _connection.Status.Failure!;
        Assert.Equal(ConnectionFailureKind.CoreStartFailed, failure.Kind);
        Assert.Equal(new CoreDiagnosis(CoreProblem.TunUnavailable, CoreKind.SingBox), failure.Problem);
    }

    [Fact]
    public async Task UnresolvedServerReasonComesFromService()
    {
        _starter.FailWith = new CoreStartException(CoreStartFailure.ExitedDuringStart, "unresolved")
        {
            Core = CoreKind.Xray,
            Diagnosis = new CoreDiagnosis(CoreProblem.ServerNotFound, CoreKind.Xray),
        };

        await _connection.ConnectAsync(Hysteria2(), s_tun, Ct);

        Assert.Equal(new CoreDiagnosis(CoreProblem.ServerNotFound, CoreKind.Xray), _connection.Status.Failure!.Problem);
    }

    [Fact]
    public async Task MissingServiceIsReported()
    {
        _transport.Available = false;

        await _connection.ConnectAsync(Hysteria2(), s_tun, Ct);

        Assert.Equal(ConnectionFailureKind.ServiceUnavailable, _connection.Status.Failure!.Kind);
    }

    [Fact]
    public async Task ServiceGoneWhileConnectedIsConnectionFailure()
    {
        await _connection.ConnectAsync(Hysteria2(), s_tun, Ct);

        await _transport.DropAllAsync();

        await WaitForAsync(() => _connection.Status.State == ConnectionState.Failed);
        Assert.Equal(ConnectionFailureKind.CoreCrashed, _connection.Status.Failure!.Kind);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(condition());
    }

    /// <summary>Каждое подключение — новая сессия службы на паре TCP-сокетов loopback.</summary>
    private sealed class ServiceTransport(TunController tun) : IIpcClientTransport, IAsyncDisposable
    {
        private static readonly ServiceInfo s_info = new("1.2.3", DateTimeOffset.UnixEpoch);
        private readonly List<(Stream Server, Task Session)> _sessions = [];

        public bool Available { get; set; } = true;

        public async Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (!Available)
            {
                throw new TimeoutException("No service.");
            }

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync(cancellationToken);
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
            var server = new NetworkStream((await accept).Client, ownsSocket: true);
            var session = IpcServer.ServeConnectionAsync(server, send => new ServiceRequestHandler(s_info, tun, send), CancellationToken.None);
            lock (_sessions)
            {
                _sessions.Add((server, session));
            }

            return new NetworkStream(client.Client, ownsSocket: true);
        }

        /// <summary>Служба «упала»: все соединения закрыты с её стороны.</summary>
        public async Task DropAllAsync()
        {
            List<(Stream Server, Task Session)> sessions;
            lock (_sessions)
            {
                sessions = [.. _sessions];
                _sessions.Clear();
            }

            foreach (var (server, session) in sessions)
            {
                await server.DisposeAsync();
                try
                {
                    await session.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or IpcProtocolException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync() => await DropAllAsync();
    }

    private sealed class RecordingProxy : ISystemProxy
    {
        public List<string> Calls { get; } = [];

        public void Enable(SystemProxySettings settings) => Calls.Add("enable");

        public bool Restore()
        {
            Calls.Add("restore");
            return true;
        }

        public SystemProxyRecovery RecoverAfterCrash() => SystemProxyRecovery.NothingToRecover;
    }
}
