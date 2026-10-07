using System.Collections.Concurrent;
using Khors.Core.Profiles;
using Khors.Core.Storage;
using Khors.Engines.Connection;
using Khors.Engines.Processes;
using Khors.Platform;
using Xunit;
using static Khors.Engines.Tests.Connection.ConnectionManagerTests;

namespace Khors.Engines.Tests.Connection;

/// <summary>Переподключение после обрыва, смены сети и сна (ROADMAP 4.3).</summary>
public sealed class ReconnectTests : IAsyncDisposable
{
    private static readonly CoreStartPreferences s_proxy = new();
    private static readonly CoreStartPreferences s_tun = s_proxy with { Mode = ConnectionMode.Tun };
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(10);

    private readonly List<string> _events = [];
    private readonly ConcurrentQueue<TimeSpan> _delays = new();
    private readonly ConcurrentQueue<ConnectionStatus> _statuses = new();
    private readonly FakeLauncher _launcher;
    private readonly ConnectionManager _manager;
    private Func<TimeSpan, CancellationToken, Task> _delay = static (_, _) => Task.CompletedTask;

    public ReconnectTests()
    {
        _launcher = new FakeLauncher(_events);
        _manager = new ConnectionManager(_launcher, new FakeSystemProxy(_events))
        {
            ReconnectEnabled = () => true,
            Delay = (delay, ct) =>
            {
                _delays.Enqueue(delay);
                return _delay(delay, ct);
            },
        };
        _manager.StatusChanged += (_, status) => _statuses.Enqueue(status);
    }

    public ValueTask DisposeAsync() => _manager.DisposeAsync();

    private static Profile ValidProfile() => new()
    {
        Id = Guid.NewGuid(),
        Name = "test",
        Server = new ServerEndpoint("vpn.example.com", 443),
        Protocol = new VlessSettings { Id = new Secret("3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b") },
        Security = new TlsSecurity { Sni = "vpn.example.com" },
    };

    private static CoreStartException StartFailed() => new(CoreStartFailure.ExitedDuringStart, "exited", 1, []);

    /// <summary>Ждёт статус, подходящий под условие (события приходят из фоновых задач).</summary>
    private async Task<ConnectionStatus> WaitForAsync(Func<ConnectionStatus, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(s_wait);
        while (true)
        {
            if (predicate(_manager.Status))
            {
                return _manager.Status;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    private async Task<FakeSession> ConnectAsync(CoreStartPreferences preferences)
    {
        await _manager.ConnectAsync(ValidProfile(), preferences, TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionState.Connected, _manager.Status.State);
        return _launcher.Last!;
    }

    private static void Crash(FakeSession session) => session.Exit(new CoreExit(1, Expected: false, DateTimeOffset.Now));

    [Fact]
    public async Task CrashReconnectsSameProfileInSameMode()
    {
        var first = await ConnectAsync(s_tun);
        var profile = _manager.Status.Profile;

        Crash(first);
        await WaitForAsync(s => s.State == ConnectionState.Connected && !ReferenceEquals(_launcher.Last, first));

        Assert.Equal(["start:Tun", "proxy-restore", "core-dispose", "start:Tun"], _events);
        Assert.Equal(profile, _manager.Status.Profile);
        Assert.Equal([TimeSpan.FromSeconds(1)], _delays);
        var reconnecting = Assert.Single(_statuses, s => s.State == ConnectionState.Reconnecting && s.Reconnect?.RetryAt is not null);
        Assert.Equal(1, reconnecting.Reconnect!.Attempt);
        Assert.Equal(ConnectionFailureKind.CoreCrashed, reconnecting.Failure?.Kind);
    }

    [Fact]
    public async Task DelaysGrowUpToOneMinute()
    {
        var first = await ConnectAsync(s_proxy);
        var failures = 8;
        _launcher.FailWhen = _ => Interlocked.Decrement(ref failures) >= 0 ? StartFailed() : null;

        Crash(first);
        await WaitForAsync(s => s.State == ConnectionState.Connected && !ReferenceEquals(_launcher.Last, first));

        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60, 60], _delays.Select(d => d.TotalSeconds));
    }

    [Fact]
    public async Task FailureThatRetryCannotFixStops()
    {
        var first = await ConnectAsync(s_proxy);
        _launcher.FailWith = new CoreStartException(CoreStartFailure.ExecutableNotFound, "gone");

        Crash(first);
        var status = await WaitForAsync(s => s.State == ConnectionState.Failed);

        Assert.Equal(ConnectionFailureKind.CoreNotFound, status.Failure?.Kind);
        Assert.Single(_delays);
    }

    [Fact]
    public async Task WithoutPermissionCrashIsReported()
    {
        _manager.ReconnectEnabled = () => false;
        var first = await ConnectAsync(s_proxy);

        Crash(first);
        var status = await WaitForAsync(s => s.State == ConnectionState.Failed);

        Assert.Equal(ConnectionFailureKind.CoreCrashed, status.Failure?.Kind);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task DisconnectStopsAttempts()
    {
        var pause = new TaskCompletionSource();
        _delay = (_, ct) => pause.Task.WaitAsync(ct);
        var first = await ConnectAsync(s_proxy);

        Crash(first);
        await WaitForAsync(s => s.State == ConnectionState.Reconnecting);
        await _manager.DisconnectAsync();
        pause.SetResult();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Disconnected, _manager.Status.State);
        Assert.Equal(["start:SystemProxy", "proxy-on:2001", "proxy-restore", "core-dispose"], _events);
    }

    [Fact]
    public async Task UserConnectReplacesAttempts()
    {
        _delay = (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var first = await ConnectAsync(s_proxy);
        Crash(first);
        await WaitForAsync(s => s.State == ConnectionState.Reconnecting);

        var other = ValidProfile();
        await _manager.ConnectAsync(other, s_proxy, TestContext.Current.CancellationToken);

        Assert.Equal(other.Id, _manager.Status.Profile?.Id);
        Assert.Equal(ConnectionState.Connected, _manager.Status.State);
        Assert.Equal(2, _events.Count(e => e.StartsWith("start:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WithoutNetworkWaitsForIt()
    {
        var network = false;
        _manager.IsNetworkAvailable = () => network;
        var first = await ConnectAsync(s_proxy);

        Crash(first);
        var waiting = await WaitForAsync(s => s.Reconnect is { WaitingForNetwork: true });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, _events.Count(e => e.StartsWith("start:", StringComparison.Ordinal)));
        Assert.Equal(1, waiting.Reconnect!.Attempt);

        network = true;
        _manager.RetryNow();
        await WaitForAsync(s => s.State == ConnectionState.Connected);

        Assert.Empty(_delays);
    }

    [Fact]
    public async Task RetryNowSkipsPause()
    {
        _delay = (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var first = await ConnectAsync(s_proxy);
        Crash(first);
        await WaitForAsync(s => s.Reconnect?.RetryAt is not null);

        _manager.RetryNow();

        await WaitForAsync(s => s.State == ConnectionState.Connected);
    }

    [Fact]
    public async Task CrashSoonAfterReconnectContinuesCount()
    {
        var first = await ConnectAsync(s_proxy);
        Crash(first);
        var second = (await WaitForAsync(s => s.State == ConnectionState.Connected && !ReferenceEquals(_launcher.Last, first)), _launcher.Last!).Item2;

        Crash(second);
        await WaitForAsync(s => s.State == ConnectionState.Connected && !ReferenceEquals(_launcher.Last, second));

        Assert.Equal([1, 2], _delays.Select(d => d.TotalSeconds));
    }

    [Fact]
    public async Task RestartInTunStartsNewSessionBeforeClosingOld()
    {
        await ConnectAsync(s_tun);
        _events.Clear();

        await _manager.RestartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["start:Tun", "core-dispose"], _events);
        Assert.Equal(ConnectionState.Connected, _manager.Status.State);
    }

    [Fact]
    public async Task RestartInProxyModeRestoresProxyFirst()
    {
        await ConnectAsync(s_proxy);
        _events.Clear();

        await _manager.RestartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["proxy-restore", "core-dispose", "start:SystemProxy", "proxy-on:2001"], _events);
        Assert.Equal(ConnectionState.Connected, _manager.Status.State);
    }

    [Fact]
    public async Task FailedRestartKeepsRetrying()
    {
        await ConnectAsync(s_tun);
        var failures = 1;
        _launcher.FailWhen = _ => Interlocked.Decrement(ref failures) >= 0 ? StartFailed() : null;

        await _manager.RestartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(s => s.State == ConnectionState.Connected);

        Assert.Equal([2], _delays.Select(d => d.TotalSeconds));
    }

    [Fact]
    public void DelaysFollowSpec()
    {
        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60], Enumerable.Range(1, 8).Select(a => ReconnectPolicy.DelayBefore(a).TotalSeconds));
        Assert.False(ReconnectPolicy.IsRetryable(ConnectionFailureKind.ProfileInvalid));
        Assert.True(ReconnectPolicy.IsRetryable(ConnectionFailureKind.ServiceUnavailable));
    }
}

/// <summary>Смена сети и пробуждение (<see cref="ConnectionWatchdog"/>).</summary>
public sealed class ConnectionWatchdogTests : IAsyncDisposable
{
    private readonly List<string> _events = [];
    private readonly FakeLauncher _launcher;
    private readonly ConnectionManager _manager;
    private readonly FakeNetwork _network = new();
    private readonly FakePower _power = new();
    private readonly Queue<bool> _checks = new();
    private readonly ConnectionWatchdog _watchdog;

    public ConnectionWatchdogTests()
    {
        _launcher = new FakeLauncher(_events);
        _manager = new ConnectionManager(_launcher, new FakeSystemProxy(_events))
        {
            ReconnectEnabled = () => true,
            Delay = static (_, _) => Task.CompletedTask,
        };
        _watchdog = new ConnectionWatchdog(
            _manager,
            _network,
            _power,
            _ => Task.FromResult(_checks.Count == 0 || _checks.Dequeue()),
            static (_, _) => Task.CompletedTask);
    }

    public async ValueTask DisposeAsync()
    {
        _watchdog.Dispose();
        await _manager.DisposeAsync();
    }

    private async Task ConnectAsync()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "test",
            Server = new ServerEndpoint("vpn.example.com", 443),
            Protocol = new VlessSettings { Id = new Secret("3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b") },
            Security = new TlsSecurity { Sni = "vpn.example.com" },
        };
        await _manager.ConnectAsync(profile, new CoreStartPreferences(), TestContext.Current.CancellationToken);
        _events.Clear();
    }

    [Fact]
    public async Task NetworkChangeWithWorkingConnectionDoesNothing()
    {
        await ConnectAsync();

        _network.Raise(available: true);
        await _watchdog.Checking;

        Assert.Empty(_events);
    }

    [Fact]
    public async Task OneFailedCheckIsNotEnough()
    {
        await ConnectAsync();
        _checks.Enqueue(false);
        _checks.Enqueue(true);

        _network.Raise(available: true);
        await _watchdog.Checking;

        Assert.Empty(_events);
    }

    [Fact]
    public async Task TwoFailedChecksRestartCore()
    {
        await ConnectAsync();
        _checks.Enqueue(false);
        _checks.Enqueue(false);

        _network.Raise(available: true);
        await _watchdog.Checking;

        Assert.Equal(["proxy-restore", "core-dispose", "start:SystemProxy", "proxy-on:2001"], _events);
        Assert.Equal(ConnectionState.Connected, _manager.Status.State);
    }

    [Fact]
    public async Task ResumeChecksConnection()
    {
        await ConnectAsync();
        _checks.Enqueue(false);
        _checks.Enqueue(false);

        _power.Raise();
        await _watchdog.Checking;

        Assert.Contains("start:SystemProxy", _events);
    }

    [Fact]
    public async Task NetworkReturnWakesWaitingAttempt()
    {
        await ConnectAsync();
        _network.Available = false;
        _launcher.Last!.Exit(new CoreExit(1, Expected: false, DateTimeOffset.Now));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (_manager.Status.Reconnect is not { WaitingForNetwork: true })
        {
            await Task.Delay(10, timeout.Token);
        }

        _network.Raise(available: true);
        while (_manager.Status.State != ConnectionState.Connected)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeNetwork : INetworkMonitor
    {
        public bool Available { get; set; } = true;

        public bool IsAvailable => Available;

        public event EventHandler? Changed;

        public void Raise(bool available)
        {
            Available = available;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePower : IPowerEvents
    {
        public event EventHandler? Resumed;

        public void Raise() => Resumed?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }
}
