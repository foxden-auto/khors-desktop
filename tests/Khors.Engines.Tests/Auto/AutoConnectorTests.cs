using System.Collections.Concurrent;
using System.Text;
using Khors.Core.Diagnostics;
using Khors.Core.Profiles;
using Khors.Engines.Auto;
using Khors.Engines.Connection;
using Khors.Engines.Latency;
using Khors.Engines.Processes;
using Xunit;

namespace Khors.Engines.Tests.Auto;

public sealed class AutoConnectorTests : IAsyncDisposable
{
    private readonly Profile _fast = Vless("fast");
    private readonly Profile _medium = Vless("medium");
    private readonly Profile _slow = Vless("slow");
    private readonly FakeLauncher _launcher = new();
    private readonly FakeProbe _probe = new();
    private readonly ConnectionManager _connection;
    private readonly AutoConnector _auto;
    private readonly List<Profile> _profiles;

    public AutoConnectorTests()
    {
        _profiles = [_slow, _fast, _medium];
        _connection = new ConnectionManager(_launcher, systemProxy: null);
        _auto = new AutoConnector(_connection, _probe, () => _profiles, () => new CoreStartPreferences())
        {
            RecheckInterval = Timeout.InfiniteTimeSpan,
        };
        _probe.Set(_fast, 80);
        _probe.Set(_medium, 150);
        _probe.Set(_slow, 400);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _auto.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static Profile Vless(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Server = new ServerEndpoint($"{name}.example.com", 443),
        Protocol = new VlessSettings { Id = new Secret("3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b") },
        Security = new TlsSecurity { Sni = $"{name}.example.com" },
    };

    [Fact]
    public async Task StartConnectsFastestProfile()
    {
        var measured = new ConcurrentBag<Guid>();
        _auto.Measured += (_, m) => measured.Add(m.ProfileId);

        await _auto.StartAsync(Ct);

        Assert.Equal(new AutoStatus(AutoState.Connected, _fast.Id), _auto.Status);
        Assert.Equal(_fast.Id, _connection.Status.Profile!.Id);
        Assert.Equal(3, measured.Count);
        Assert.True(_auto.IsActive);
    }

    [Fact]
    public async Task ProfileThatFailsToStartIsSkipped()
    {
        _launcher.Broken.Add(_fast.Id);

        await _auto.StartAsync(Ct);

        Assert.Equal(_medium.Id, _auto.Status.ProfileId);
        Assert.Equal(ConnectionState.Connected, _connection.Status.State);
    }

    [Fact]
    public async Task UnusableProfilesAreNotCandidates()
    {
        var invalid = Vless("invalid") with { Server = new ServerEndpoint("invalid.example.com", 0) };
        _profiles.Add(invalid);
        _probe.Set(invalid, 1);

        await _auto.StartAsync(Ct);

        Assert.Equal(_fast.Id, _auto.Status.ProfileId);
        Assert.DoesNotContain(invalid.Id, _probe.Calls);
    }

    [Fact]
    public async Task NothingRespondsMeansFailed()
    {
        _probe.Fail(_fast);
        _probe.Fail(_medium);
        _probe.Fail(_slow);

        await _auto.StartAsync(Ct);

        Assert.Equal(AutoState.Failed, _auto.Status.State);
        Assert.False(_auto.IsActive);
        Assert.Equal(ConnectionState.Disconnected, _connection.Status.State);
        Assert.Equal(0, _launcher.Starts);
    }

    [Fact]
    public async Task RecheckSwitchesOnlyToNotablyFasterProfile()
    {
        await _auto.StartAsync(Ct);

        // Чуть быстрее (60 против 80 мс) — шум, остаёмся.
        _probe.Set(_medium, 60);
        await _auto.HandleAsync(AutoConnector.AutoTrigger.Recheck, Ct);
        Assert.Equal(_fast.Id, _auto.Status.ProfileId);

        // Текущий стал медленным (500 мс), другой заметно быстрее — переключаемся.
        _probe.Set(_fast, 500);
        await _auto.HandleAsync(AutoConnector.AutoTrigger.Recheck, Ct);
        Assert.Equal(_medium.Id, _auto.Status.ProfileId);
        Assert.Equal(_medium.Id, _connection.Status.Profile!.Id);
    }

    [Fact]
    public async Task RecheckLeavesFailedCurrentProfile()
    {
        await _auto.StartAsync(Ct);
        _probe.Fail(_fast);

        await _auto.HandleAsync(AutoConnector.AutoTrigger.Recheck, Ct);

        Assert.Equal(_medium.Id, _auto.Status.ProfileId);
    }

    [Fact]
    public async Task WhenNothingWorksDuringRecheckConnectionStaysAndAutoWaits()
    {
        await _auto.StartAsync(Ct);
        _probe.Fail(_fast);
        _probe.Fail(_medium);
        _probe.Fail(_slow);

        await _auto.HandleAsync(AutoConnector.AutoTrigger.Recheck, Ct);

        Assert.Equal(AutoState.Waiting, _auto.Status.State);
        Assert.True(_auto.IsActive);
        Assert.Equal(_fast.Id, _connection.Status.Profile!.Id);
    }

    [Fact]
    public async Task CoreCrashSwitchesToNextProfile()
    {
        await _auto.StartAsync(Ct);
        _probe.Fail(_fast);

        _launcher.Sessions[_fast.Id].Exit(new CoreExit(1, Expected: false, DateTimeOffset.Now));

        await WaitForAsync(() => _auto.Status is { State: AutoState.Connected } s && s.ProfileId == _medium.Id);
        Assert.Equal(_medium.Id, _connection.Status.Profile!.Id);
    }

    [Fact]
    public async Task ProblemInLogIsCheckedBeforeSwitching()
    {
        await _auto.StartAsync(Ct);

        // Сервер отвечает — причина из лога была единичной: остаёмся, причина снята.
        _launcher.Sessions[_fast.Id].Log.Add(CoreLogSource.StandardOutput, RefusedLine);
        await WaitForAsync(() => _probe.Calls.Count(id => id == _fast.Id) >= 2 && _connection.Status.Problem is null);
        Assert.Equal(_fast.Id, _auto.Status.ProfileId);

        // Сервер не отвечает — переход на следующий.
        _probe.Fail(_fast);
        _launcher.Sessions[_fast.Id].Log.Add(CoreLogSource.StandardOutput, RefusedLine);
        await WaitForAsync(() => _auto.Status.ProfileId == _medium.Id && _auto.Status.State == AutoState.Connected);
    }

    [Fact]
    public async Task StopWithDisconnect()
    {
        await _auto.StartAsync(Ct);

        await _auto.StopAsync(disconnect: true);

        Assert.Equal(AutoState.Off, _auto.Status.State);
        Assert.Equal(ConnectionState.Disconnected, _connection.Status.State);

        // После остановки сбои подключения «Авто» не трогают.
        await _connection.ConnectAsync(_slow, new CoreStartPreferences(), Ct);
        await _connection.DisconnectAsync();
        await Task.Delay(100, Ct);
        Assert.Equal(AutoState.Off, _auto.Status.State);
    }

    [Fact]
    public async Task StopDuringSelectionCancelsIt()
    {
        _probe.Delay = TimeSpan.FromSeconds(30);
        var start = _auto.StartAsync(Ct);
        await WaitForAsync(() => _auto.Status.State == AutoState.Selecting);

        await _auto.StopAsync(disconnect: false);
        await start.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(AutoState.Off, _auto.Status.State);
        Assert.Equal(0, _launcher.Starts);
    }

    private const string RefusedLine =
        "2026/10/05 20:41:52.085452 [Info] [407998944] app/proxyman/outbound: app/proxyman/outbound: failed to process outbound traffic > "
        + "proxy/vless/outbound: failed to find an available destination > common/retry: [dial tcp 192.0.2.10:443: connect: connection refused] > "
        + "common/retry: all retry attempts failed";

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(condition());
    }

    private sealed class FakeProbe : ILatencyProbe
    {
        private readonly ConcurrentDictionary<Guid, LatencyResult> _results = new();

        public ConcurrentQueue<Guid> Calls { get; } = new();

        public TimeSpan Delay { get; set; }

        public void Set(Profile profile, int ms) => _results[profile.Id] = LatencyResult.Success(TimeSpan.FromMilliseconds(ms));

        public void Fail(Profile profile) => _results[profile.Id] = new LatencyResult(LatencyStatus.Failed);

        public async Task<LatencyResult> MeasureAsync(Profile profile, CancellationToken cancellationToken)
        {
            Calls.Enqueue(profile.Id);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return _results.GetValueOrDefault(profile.Id) ?? new LatencyResult(LatencyStatus.Timeout);
        }
    }

    private sealed class FakeLauncher : ICoreLauncher
    {
        private int _starts;

        public HashSet<Guid> Broken { get; } = [];

        public ConcurrentDictionary<Guid, FakeSession> Sessions { get; } = new();

        public int Starts => Volatile.Read(ref _starts);

        public Task<ICoreSession> StartAsync(Profile profile, CoreStartPreferences preferences, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _starts);
            if (Broken.Contains(profile.Id))
            {
                throw new CoreStartException(CoreStartFailure.ExitedDuringStart, "exited", 1) { Core = CoreKind.Xray };
            }

            var session = new FakeSession();
            Sessions[profile.Id] = session;
            return Task.FromResult<ICoreSession>(session);
        }
    }

    private sealed class FakeSession : ICoreSession
    {
        private readonly TaskCompletionSource<CoreExit> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CoreKind Core => CoreKind.Xray;

        public int SocksPort => 2000;

        public int HttpPort => 2001;

        public CoreLogBuffer Log { get; } = new(new SecretMasker(Encoding.UTF8.GetBytes("t")));

        public Task<CoreExit> Completion => _completion.Task;

        public void Exit(CoreExit exit) => _completion.TrySetResult(exit);

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Exit(new CoreExit(0, Expected: true, DateTimeOffset.Now));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Exit(new CoreExit(0, Expected: true, DateTimeOffset.Now));
            return ValueTask.CompletedTask;
        }
    }
}
