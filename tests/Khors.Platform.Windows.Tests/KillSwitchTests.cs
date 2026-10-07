using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Khors.Platform.Windows.KillSwitch;
using Xunit;
using static Khors.Platform.Windows.KillSwitch.WfpNative;

namespace Khors.Platform.Windows.Tests;

public class KillSwitchFiltersTests
{
    private static readonly KillSwitchPolicy s_policy = new(
        Persistent: false,
        AllowLocalNetwork: true,
        [@"C:\Program Files\KHORS\service\cores\sing-box\sing-box.exe"],
        "KHORS");

    [Fact]
    public void EachLayerHasPermitsAndFinalBlock()
    {
        var filters = KillSwitchFilters.Build(s_policy, tunnelLuid: 42);

        foreach (var layer in Enum.GetValues<WfpLayer>())
        {
            var onLayer = filters.Where(f => f.Layer == layer).ToList();
            var block = Assert.Single(onLayer, f => !f.Permit);
            Assert.Empty(block.Conditions);
            Assert.All(onLayer.Where(f => f.Permit), f => Assert.True(f.Weight > block.Weight));
            Assert.Contains(onLayer, f => f.Conditions is [LoopbackCondition]);
            Assert.Contains(onLayer, f => f.Conditions is [InterfaceCondition(42)]);
            Assert.Contains(onLayer, f => f.Conditions is [ApplicationCondition(var path)] && path.EndsWith("sing-box.exe", StringComparison.Ordinal));
            Assert.Contains(onLayer, f => f.Conditions is [UdpRemotePortCondition]);
            Assert.All(onLayer, f => Assert.StartsWith("KHORS", f.Name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void WithoutTunnelOnlyBlocksAndExceptions()
    {
        var filters = KillSwitchFilters.Build(s_policy, tunnelLuid: null);

        Assert.DoesNotContain(filters, f => f.Conditions.Any(c => c is InterfaceCondition));
    }

    [Fact]
    public void LocalNetworksFollowPolicyAndAddressFamily()
    {
        var allowed = KillSwitchFilters.Build(s_policy, null);
        var denied = KillSwitchFilters.Build(s_policy with { AllowLocalNetwork = false }, null);

        var networks = allowed.SelectMany(f => f.Conditions.OfType<RemoteNetworkCondition>().Select(c => (f.Layer, c.Network))).ToList();
        Assert.Equal(KillSwitchPolicy.LocalNetworks.Count, networks.Count);
        Assert.All(networks, n => Assert.Equal(n.Layer == WfpLayer.ConnectV4, n.Network.BaseAddress.AddressFamily == AddressFamily.InterNetwork));
        Assert.DoesNotContain(denied, f => f.Conditions.Any(c => c is RemoteNetworkCondition));
    }

    [Theory]
    [InlineData("192.168.0.0/16", 0xC0A80000u, 0xFFFF0000u)]
    [InlineData("10.0.0.0/8", 0x0A000000u, 0xFF000000u)]
    [InlineData("255.255.255.255/32", 0xFFFFFFFFu, 0xFFFFFFFFu)]
    public void IPv4IsInHostByteOrder(string network, uint address, uint mask)
    {
        var value = WfpKillSwitch.V4(IPNetwork.Parse(network));

        Assert.Equal(address, value.Address);
        Assert.Equal(mask, value.Mask);
    }

    /// <summary>Раскладка совпадает с C-структурами Windows SDK для x64 (иначе WFP прочитал бы мусор).</summary>
    [Fact]
    public void StructLayoutMatchesWindowsSdk()
    {
        Assert.Equal(16, Marshal.SizeOf<FwpByteBlob>());
        Assert.Equal(16, Marshal.SizeOf<FwpValue>());
        Assert.Equal(72, Marshal.SizeOf<FwpmSession>());
        Assert.Equal(64, Marshal.SizeOf<FwpmProvider>());
        Assert.Equal(72, Marshal.SizeOf<FwpmSubLayer>());
        Assert.Equal(20, Marshal.SizeOf<FwpmAction>());
        Assert.Equal(40, Marshal.SizeOf<FwpmFilterCondition>());
        Assert.Equal(200, Marshal.SizeOf<FwpmFilter>());
        Assert.Equal(72, Marshal.SizeOf<FwpmFilterEnumTemplate>());
        Assert.Equal(8, Marshal.SizeOf<FwpV4AddrAndMask>());
        Assert.Equal(17, Marshal.SizeOf<FwpV6AddrAndMask>());

        Assert.Equal(32, (int)Marshal.OffsetOf<FwpmFilter>(nameof(FwpmFilter.Flags)));
        Assert.Equal(64, (int)Marshal.OffsetOf<FwpmFilter>(nameof(FwpmFilter.LayerKey)));
        Assert.Equal(96, (int)Marshal.OffsetOf<FwpmFilter>(nameof(FwpmFilter.Weight)));
        Assert.Equal(120, (int)Marshal.OffsetOf<FwpmFilter>(nameof(FwpmFilter.FilterCondition)));
        Assert.Equal(128, (int)Marshal.OffsetOf<FwpmFilter>(nameof(FwpmFilter.Action)));
        Assert.Equal(152, (int)Marshal.OffsetOf<FwpmFilter>(nameof(FwpmFilter.ProviderContextLow)));
        Assert.Equal(184, (int)Marshal.OffsetOf<FwpmFilter>(nameof(FwpmFilter.EffectiveWeight)));
        Assert.Equal(24, (int)Marshal.OffsetOf<FwpmFilterCondition>(nameof(FwpmFilterCondition.ConditionValue)));
        Assert.Equal(64, (int)Marshal.OffsetOf<FwpmSubLayer>(nameof(FwpmSubLayer.Weight)));
        Assert.Equal(48, (int)Marshal.OffsetOf<FwpmFilterEnumTemplate>(nameof(FwpmFilterEnumTemplate.FilterCondition)));
        Assert.Equal(64, (int)Marshal.OffsetOf<FwpmFilterEnumTemplate>(nameof(FwpmFilterEnumTemplate.CalloutKey)));
    }
}

/// <summary>
/// Настоящий WFP: на время теста блокирует исходящий трафик компьютера (кроме loopback и локальной сети) и снимает
/// фильтры в конце. Нужны права администратора; включается только KHORS_WFP_TESTS=1.
/// </summary>
public class WfpKillSwitchIntegrationTests
{
    [Fact]
    public async Task BlocksOutboundAndCleansUp()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("KHORS_WFP_TESTS") == "1",
            "Блокирует сеть компьютера и требует прав администратора; включается KHORS_WFP_TESTS=1");

        var ct = TestContext.Current.CancellationToken;
        using var killSwitch = new WfpKillSwitch();
        try
        {
            killSwitch.Apply(new KillSwitchPolicy(Persistent: false, AllowLocalNetwork: true, [], TunnelInterface: null));
            Assert.False(killSwitch.HasPersistentFilters());

            // Блокировка WFP отвечает сразу отказом в доступе, а не тайм-аутом.
            var blocked = await Assert.ThrowsAsync<SocketException>(() => ConnectAsync(IPAddress.Parse("1.1.1.1"), 443, ct));
            Assert.Equal(SocketError.AccessDenied, blocked.SocketErrorCode);

            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                await ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            }

            killSwitch.Apply(new KillSwitchPolicy(Persistent: true, AllowLocalNetwork: false, [], TunnelInterface: null));
            Assert.True(killSwitch.HasPersistentFilters());
        }
        finally
        {
            killSwitch.Remove();
        }

        Assert.False(killSwitch.HasPersistentFilters());
        var error = await Record.ExceptionAsync(() => ConnectAsync(IPAddress.Parse("1.1.1.1"), 443, ct));
        Assert.False(error is SocketException { SocketErrorCode: SocketError.AccessDenied }, "Фильтры не сняты");
    }

    private static async Task ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(address, port, timeout.Token);
    }
}
