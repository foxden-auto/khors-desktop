using System.ComponentModel;
using Khors.Core.Dns;
using Khors.Core.Import;
using Khors.Core.Routing;
using Khors.Ipc;
using Khors.Platform;
using Khors.Service.KillSwitch;
using Khors.Service.Tun;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Khors.Service.Tests;

/// <summary>Подменный Kill Switch: настоящий WFP требует прав администратора.</summary>
internal sealed class FakeKillSwitch : IKillSwitch
{
    public List<string> Calls { get; } = [];

    public KillSwitchPolicy? Current { get; private set; }

    public bool Persistent { get; set; }

    public bool Fail { get; set; }

    public void Apply(KillSwitchPolicy policy)
    {
        if (Fail)
        {
            throw new Win32Exception(5, "FwpmFilterAdd0 failed: 0x00000005.");
        }

        Current = policy;
        Persistent = policy.Persistent;
        Calls.Add($"apply:{(policy.Persistent ? "persistent" : "dynamic")}:{policy.TunnelInterface ?? "-"}:{(policy.AllowLocalNetwork ? "lan" : "nolan")}");
    }

    public void Remove()
    {
        Current = null;
        Persistent = false;
        Calls.Add("remove");
    }

    public bool HasPersistentFilters() => Persistent;
}

public sealed class KillSwitchControllerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("khors-killswitch-").FullName;
    private readonly FakeKillSwitch _wfp = new();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string SettingsFile => Path.Combine(_directory, "killswitch.json");

    private KillSwitchController Controller(IpcKillSwitchMode mode = IpcKillSwitchMode.Off, bool allowLan = true)
    {
        var controller = new KillSwitchController(_wfp, SettingsFile, [@"C:\Program Files\KHORS\service\cores\sing-box\sing-box.exe"], NullLogger<KillSwitchController>.Instance);
        if (mode != IpcKillSwitchMode.Off || !allowLan)
        {
            controller.Set(mode, allowLan);
            _wfp.Calls.Clear();
        }

        return controller;
    }

    [Fact]
    public void OffDoesNothing()
    {
        var controller = Controller();

        Assert.True(controller.TunStarting());
        Assert.True(controller.TunStarted());
        controller.TunStopped(byUser: false);

        Assert.Empty(_wfp.Calls);
        Assert.Equal(new KillSwitchStatusResponse(IpcKillSwitchMode.Off, true, Active: false, Blocking: false), controller.Status());
    }

    [Fact]
    public void WhileConnectedBlocksBeforeCoresAndReleasesOnUserDisconnect()
    {
        var controller = Controller(IpcKillSwitchMode.WhileConnected);

        Assert.Empty(_wfp.Calls);
        Assert.True(controller.TunStarting());
        Assert.True(controller.Status().Blocking);
        Assert.True(controller.TunStarted());
        Assert.Equal(new KillSwitchStatusResponse(IpcKillSwitchMode.WhileConnected, true, Active: true, Blocking: false), controller.Status());
        controller.TunStopped(byUser: true);

        Assert.Equal(["apply:dynamic:-:lan", "apply:dynamic:KHORS:lan", "remove"], _wfp.Calls);
        Assert.False(controller.Status().Active);
    }

    [Fact]
    public void CrashOrClosedWindowKeepsBlockingUntilRelease()
    {
        var controller = Controller(IpcKillSwitchMode.WhileConnected, allowLan: false);
        controller.TunStarting();
        controller.TunStarted();

        controller.TunStopped(byUser: false);

        Assert.Equal("apply:dynamic:-:nolan", _wfp.Calls[^1]);
        Assert.True(controller.Status().Blocking);

        controller.Release();

        Assert.Equal("remove", _wfp.Calls[^1]);
        Assert.False(controller.Status().Active);
    }

    [Fact]
    public void FailedStartReleasesWhileConnected()
    {
        var controller = Controller(IpcKillSwitchMode.WhileConnected);
        controller.TunStarting();

        controller.TunStartFailed();

        Assert.Equal(["apply:dynamic:-:lan", "remove"], _wfp.Calls);
    }

    [Fact]
    public void FailedReconnectKeepsBlockingAfterCrash()
    {
        var controller = Controller(IpcKillSwitchMode.WhileConnected);
        controller.TunStarting();
        controller.TunStarted();
        controller.TunStopped(byUser: false);

        controller.TunStarting();
        controller.TunStartFailed();

        Assert.True(controller.Status().Blocking);
        Assert.DoesNotContain("remove", _wfp.Calls);
    }

    [Fact]
    public void AlwaysBlocksWithoutTunnel()
    {
        var controller = Controller(IpcKillSwitchMode.Always);

        controller.Initialize();
        controller.TunStarting();
        controller.TunStarted();
        controller.TunStopped(byUser: true);
        controller.Release();

        Assert.Equal(["apply:persistent:-:lan", "apply:persistent:-:lan", "apply:persistent:KHORS:lan", "apply:persistent:-:lan", "apply:persistent:-:lan"], _wfp.Calls);
        Assert.True(controller.Status().Blocking);
    }

    [Fact]
    public void InitializeRemovesLeftoverPersistentFilters()
    {
        _wfp.Persistent = true;
        var controller = Controller();

        controller.Initialize();

        Assert.Equal(["remove"], _wfp.Calls);
    }

    [Fact]
    public void TurningOffRemovesFilters()
    {
        var controller = Controller(IpcKillSwitchMode.Always);
        controller.Initialize();

        controller.Set(IpcKillSwitchMode.Off, allowLocalNetwork: true);

        Assert.Equal("remove", _wfp.Calls[^1]);
        Assert.False(controller.Status().Active);
    }

    [Fact]
    public void WfpFailureIsReported()
    {
        var controller = Controller(IpcKillSwitchMode.WhileConnected);
        _wfp.Fail = true;

        Assert.False(controller.TunStarting());
        Assert.Contains("FwpmFilterAdd0", controller.Status().Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingSurvivesRestart()
    {
        Controller(IpcKillSwitchMode.Always, allowLan: false);

        var restarted = new KillSwitchController(_wfp, SettingsFile, [], NullLogger<KillSwitchController>.Instance);

        Assert.Equal(IpcKillSwitchMode.Always, restarted.Status().Mode);
        Assert.False(restarted.Status().AllowLocalNetwork);
    }

    [Fact]
    public async Task TunWithFailedKillSwitchDoesNotStart()
    {
        var controller = Controller(IpcKillSwitchMode.WhileConnected);
        _wfp.Fail = true;
        var starter = new FakeTunStarter();
        await using var tun = new TunController(starter, NullLogger<TunController>.Instance, controller);
        var profile = ShareLinkParser.Parse("hysteria2://Hy2Pa55@hy.example.com/?sni=hy.example.com").Profile!;

        var answer = await tun.StartAsync(new object(), profile, "warning", DnsPresets.Default.Server, new RoutingSettings(), _ => { }, TestContext.Current.CancellationToken);

        Assert.Equal(new TunFailedResponse(IpcTunFailure.KillSwitchFailed), answer);
        Assert.Empty(starter.Runs);
    }

    [Fact]
    public async Task OwnerDisconnectKeepsBlockingButStopTunReleases()
    {
        var controller = Controller(IpcKillSwitchMode.WhileConnected);
        await using var tun = new TunController(new FakeTunStarter(), NullLogger<TunController>.Instance, controller);
        var profile = ShareLinkParser.Parse("hysteria2://Hy2Pa55@hy.example.com/?sni=hy.example.com").Profile!;
        var ct = TestContext.Current.CancellationToken;

        var owner = new object();
        Assert.IsType<TunStartedResponse>(await tun.StartAsync(owner, profile, "warning", DnsPresets.Default.Server, new RoutingSettings(), _ => { }, ct));
        await tun.StopAsync(owner, byUser: false);
        Assert.True(controller.Status().Blocking);

        Assert.IsType<TunStartedResponse>(await tun.StartAsync(owner, profile, "warning", DnsPresets.Default.Server, new RoutingSettings(), _ => { }, ct));
        await tun.StopAsync(owner, byUser: true);
        Assert.False(controller.Status().Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TunnelIpv6FollowsRouting(bool tunIpv6)
    {
        var controller = Controller(IpcKillSwitchMode.WhileConnected);
        await using var tun = new TunController(new FakeTunStarter(), NullLogger<TunController>.Instance, controller);
        var profile = ShareLinkParser.Parse("hysteria2://Hy2Pa55@hy.example.com/?sni=hy.example.com").Profile!;

        Assert.IsType<TunStartedResponse>(await tun.StartAsync(new object(), profile, "warning", DnsPresets.Default.Server, new RoutingSettings(TunIpv6: tunIpv6), _ => { }, TestContext.Current.CancellationToken));

        Assert.Equal("KHORS", _wfp.Current?.TunnelInterface);
        Assert.Equal(tunIpv6, _wfp.Current?.TunnelIpv6);
    }

    [Fact]
    public void ResetTurnsOffAndRemovesFiltersForGood()
    {
        var controller = Controller(IpcKillSwitchMode.Always);
        controller.Initialize();

        Assert.True(controller.Reset());

        Assert.Equal("remove", _wfp.Calls[^1]);
        Assert.Equal(IpcKillSwitchMode.Off, controller.Status().Mode);
        Assert.False(controller.Status().Active);
        var restarted = new KillSwitchController(_wfp, SettingsFile, [], NullLogger<KillSwitchController>.Instance);
        Assert.Equal(IpcKillSwitchMode.Off, restarted.Status().Mode);
    }

    [Fact]
    public void ResetWithoutFiltersStillCleansUp()
    {
        var controller = Controller();

        Assert.Null(controller.Reset());
        Assert.Equal(["remove"], _wfp.Calls);
    }

    [Fact]
    public async Task NetworkResetStopsTunThenFiltersThenAdapter()
    {
        var controller = Controller(IpcKillSwitchMode.WhileConnected);
        var starter = new FakeTunStarter();
        await using var tun = new TunController(starter, NullLogger<TunController>.Instance, controller);
        var profile = ShareLinkParser.Parse("hysteria2://Hy2Pa55@hy.example.com/?sni=hy.example.com").Profile!;
        Assert.IsType<TunStartedResponse>(await tun.StartAsync(new object(), profile, "warning", DnsPresets.Default.Server, new RoutingSettings(), _ => { }, TestContext.Current.CancellationToken));
        var adapters = new FakeAdapters(() => Assert.False(tun.IsRunning)) { Count = 1 };

        var result = await new NetworkReset(tun, controller, adapters).RunAsync();

        Assert.Equal(
            [
                new IpcResetItem(IpcResetStep.Tunnel, IpcResetOutcome.Done),
                new IpcResetItem(IpcResetStep.KillSwitch, IpcResetOutcome.Done),
                new IpcResetItem(IpcResetStep.Adapter, IpcResetOutcome.Done),
            ],
            result.Items);
        Assert.Equal("KHORS", adapters.Name);
        Assert.False(tun.IsRunning);
    }

    [Fact]
    public async Task NetworkResetReportsFailedStepAndContinues()
    {
        var controller = Controller();
        _wfp.Fail = true;
        var adapters = new FakeAdapters(() => throw new Win32Exception(5, "DiUninstallDevice failed."));

        var result = await new NetworkReset(null, controller, adapters).RunAsync();

        Assert.Equal(IpcResetOutcome.NothingToDo, result.Items[0].Outcome);
        Assert.Equal(new IpcResetItem(IpcResetStep.Adapter, IpcResetOutcome.Failed, "DiUninstallDevice failed."), result.Items[1]);
    }

    [Fact]
    public async Task ServerChangeKeepsBlockingWithoutGap()
    {
        var controller = Controller(IpcKillSwitchMode.WhileConnected);
        await using var tun = new TunController(new FakeTunStarter(), NullLogger<TunController>.Instance, controller);
        var profile = ShareLinkParser.Parse("hysteria2://Hy2Pa55@hy.example.com/?sni=hy.example.com").Profile!;
        var ct = TestContext.Current.CancellationToken;
        var oldOwner = new object();
        var newOwner = new object();
        Assert.IsType<TunStartedResponse>(await tun.StartAsync(oldOwner, profile, "warning", DnsPresets.Default.Server, new RoutingSettings(), _ => { }, ct));
        _wfp.Calls.Clear();

        // Окно поднимает новое подключение раньше, чем закрывает старое (ConnectionManager, ROADMAP 4.5).
        Assert.IsType<TunStartedResponse>(await tun.StartAsync(newOwner, profile, "warning", DnsPresets.Default.Server, new RoutingSettings(), _ => { }, ct));
        await tun.StopAsync(oldOwner, byUser: true);

        Assert.DoesNotContain("remove", _wfp.Calls);
        Assert.True(tun.IsRunning);
        Assert.True(controller.Status().Active);
        Assert.False(controller.Status().Blocking);
    }

    private sealed class FakeAdapters(Action onRemove) : ITunAdapterCleanup
    {
        public int Count { get; init; }

        public string? Name { get; private set; }

        public int RemoveAdapters(string name)
        {
            Name = name;
            onRemove();
            return Count;
        }
    }
}
