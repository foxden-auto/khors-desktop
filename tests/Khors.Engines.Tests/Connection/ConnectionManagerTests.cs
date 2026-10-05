using System.Text;
using Khors.Core.Diagnostics;
using Khors.Core.Generators;
using Khors.Core.Profiles;
using Khors.Engines.Connection;
using Khors.Engines.Processes;
using Khors.Platform;
using Xunit;

namespace Khors.Engines.Tests.Connection;

public sealed class ConnectionManagerTests : IAsyncDisposable
{
    private static readonly CoreStartPreferences s_preferences = new();

    private readonly List<string> _events = [];
    private readonly FakeLauncher _launcher;
    private readonly FakeSystemProxy _proxy;
    private readonly ConnectionManager _manager;

    public ConnectionManagerTests()
    {
        _launcher = new FakeLauncher(_events);
        _proxy = new FakeSystemProxy(_events);
        _manager = new ConnectionManager(_launcher, _proxy, () => _events.Add("watchdog"));
    }

    public ValueTask DisposeAsync() => _manager.DisposeAsync();

    private static Profile ValidProfile(string name = "test") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Server = new ServerEndpoint("vpn.example.com", 443),
        Protocol = new VlessSettings { Id = new Secret("3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b") },
        Security = new TlsSecurity { Sni = "vpn.example.com" },
    };

    [Fact]
    public async Task ConnectStartsCoreThenWatchdogThenSystemProxy()
    {
        var states = new List<ConnectionState>();
        _manager.StatusChanged += (_, s) => states.Add(s.State);

        await _manager.ConnectAsync(ValidProfile(), s_preferences, TestContext.Current.CancellationToken);

        Assert.Equal(["start", "watchdog", "proxy-on:2001"], _events);
        Assert.Equal([ConnectionState.Connecting, ConnectionState.Connected], states);
        Assert.Equal(ConnectionState.Connected, _manager.Status.State);
        Assert.Equal(2000, _manager.Status.SocksPort);
        Assert.NotNull(_manager.Status.ConnectedAt);
    }

    [Fact]
    public async Task DisconnectRestoresProxyBeforeStoppingCore()
    {
        await _manager.ConnectAsync(ValidProfile(), s_preferences, TestContext.Current.CancellationToken);
        _events.Clear();

        await _manager.DisconnectAsync();

        Assert.Equal(["proxy-restore", "core-dispose"], _events);
        Assert.Equal(ConnectionStatus.Disconnected, _manager.Status);
    }

    [Fact]
    public async Task CoreCrashRestoresProxyAndReportsFailureWithLog()
    {
        await _manager.ConnectAsync(ValidProfile(), s_preferences, TestContext.Current.CancellationToken);
        var failed = new TaskCompletionSource<ConnectionStatus>();
        _manager.StatusChanged += (_, s) =>
        {
            if (s.State == ConnectionState.Failed)
            {
                failed.TrySetResult(s);
            }
        };
        _events.Clear();

        _launcher.Last!.Log.Add(CoreLogSource.StandardOutput, "proxy/vless: connection ends > reality verification failed");
        _launcher.Last.Exit(new CoreExit(1, Expected: false, DateTimeOffset.Now));
        var status = await failed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.Equal(["proxy-restore", "core-dispose"], _events);
        Assert.Equal(ConnectionFailureKind.CoreCrashed, status.Failure!.Kind);
        Assert.Equal(1, status.Failure.ExitCode);
        Assert.Contains(status.Failure.LogTail, l => l.Contains("reality verification failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidProfileFailsWithoutStartingCore()
    {
        var profile = ValidProfile() with { Server = new ServerEndpoint("vpn.example.com", 0) };

        await _manager.ConnectAsync(profile, s_preferences, TestContext.Current.CancellationToken);

        Assert.Empty(_events);
        Assert.Equal(new ConnectionFailure(ConnectionFailureKind.ProfileInvalid, Issue: ProfileIssueCode.PortOutOfRange), _manager.Status.Failure);
    }

    [Theory]
    [InlineData(CoreStartFailure.ExecutableNotFound, ConnectionFailureKind.CoreNotFound)]
    [InlineData(CoreStartFailure.ConfigNotGenerated, ConnectionFailureKind.UnsupportedByCore)]
    [InlineData(CoreStartFailure.ExitedDuringStart, ConnectionFailureKind.CoreStartFailed)]
    [InlineData(CoreStartFailure.ReadyTimeout, ConnectionFailureKind.CoreStartFailed)]
    public async Task StartFailuresAreMapped(CoreStartFailure failure, ConnectionFailureKind expected)
    {
        _launcher.FailWith = new CoreStartException(failure, "fail", exitCode: 23, logTail: ["last line"])
        {
            ConfigError = new CoreConfigError(CoreConfigErrorCode.UnsupportedFeature, "security.allowInsecure"),
        };

        await _manager.ConnectAsync(ValidProfile(), s_preferences, TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Failed, _manager.Status.State);
        Assert.Equal(expected, _manager.Status.Failure!.Kind);
        Assert.DoesNotContain(_events, e => e.StartsWith("proxy-on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SystemProxyFailureStopsCore()
    {
        _proxy.FailEnable = true;

        await _manager.ConnectAsync(ValidProfile(), s_preferences, TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionFailureKind.SystemProxyFailed, _manager.Status.Failure!.Kind);
        Assert.Contains("core-dispose", _events);
        Assert.Contains("proxy-restore", _events);
    }

    [Fact]
    public async Task SwitchingProfileStopsPreviousConnectionFirst()
    {
        await _manager.ConnectAsync(ValidProfile("first"), s_preferences, TestContext.Current.CancellationToken);
        var first = _launcher.Last!;
        _events.Clear();

        await _manager.ConnectAsync(ValidProfile("second"), s_preferences, TestContext.Current.CancellationToken);

        Assert.Equal(["proxy-restore", "core-dispose", "start", "watchdog", "proxy-on:2001"], _events);
        Assert.Equal("second", _manager.Status.Profile!.Name);

        // Завершение старого ядра после переключения не считается падением нового подключения.
        first.Exit(new CoreExit(1, Expected: false, DateTimeOffset.Now));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionState.Connected, _manager.Status.State);
    }

    [Fact]
    public async Task DisconnectAfterFailureResetsState()
    {
        _launcher.FailWith = new CoreStartException(CoreStartFailure.ExecutableNotFound, "fail");
        await _manager.ConnectAsync(ValidProfile(), s_preferences, TestContext.Current.CancellationToken);

        await _manager.DisconnectAsync();

        Assert.Equal(ConnectionStatus.Disconnected, _manager.Status);
    }

    private sealed class FakeLauncher(List<string> events) : ICoreLauncher
    {
        public CoreStartException? FailWith { get; set; }

        public FakeSession? Last { get; private set; }

        public Task<ICoreSession> StartAsync(Profile profile, CoreStartPreferences preferences, CancellationToken cancellationToken)
        {
            events.Add("start");
            if (FailWith is not null)
            {
                throw FailWith;
            }

            Last = new FakeSession(events);
            return Task.FromResult<ICoreSession>(Last);
        }
    }

    private sealed class FakeSession(List<string> events) : ICoreSession
    {
        private readonly TaskCompletionSource<CoreExit> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            events.Add("core-dispose");
            Exit(new CoreExit(0, Expected: true, DateTimeOffset.Now));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSystemProxy(List<string> events) : ISystemProxy
    {
        public bool FailEnable { get; set; }

        public void Enable(SystemProxySettings settings)
        {
            if (FailEnable)
            {
                throw new InvalidOperationException("WinINet failed");
            }

            events.Add($"proxy-on:{settings.Port}");
        }

        public bool Restore()
        {
            events.Add("proxy-restore");
            return true;
        }

        public SystemProxyRecovery RecoverAfterCrash() => SystemProxyRecovery.NothingToRecover;
    }
}
