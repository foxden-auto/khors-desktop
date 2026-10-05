using System.Diagnostics;
using System.Text;
using Khors.Core.Diagnostics;
using Khors.Core.Generators;
using Khors.Core.Profiles;
using Khors.Engines.Connection;
using Khors.Engines.Processes;
using Xunit;

namespace Khors.Engines.Tests;

/// <summary>
/// Настоящий sing-box (из cores/) без интернета: профиль указывает на недоступный локальный сервер,
/// трафик к локальному эхо-серверу идёт напрямую по правилу частных адресов.
/// </summary>
public class SingBoxLauncherTests
{
    private const string Uuid = "8d2e6f10-4b3a-4c5d-9e8f-0a1b2c3d4e5f";
    private const string Key32 = "Zx8Q2mT9vK4rL7pW1sN6bY3cH5jD0fG8aE2uI9oP4kM=";

    private static readonly SecretMasker s_masker = new(Encoding.UTF8.GetBytes("singbox-launcher-test"));
    private static readonly CoreStartOptions s_anyPorts = new() { PreferredSocksPort = null, PreferredHttpPort = null };

    private static Profile Tuic() => new()
    {
        Id = Guid.Empty,
        Name = "local tuic",
        Server = new ServerEndpoint("127.0.0.1", 1),
        Protocol = new TuicSettings { Uuid = new Secret(Uuid), Password = new Secret("TuicPa55") },
        Security = new TlsSecurity { Sni = "tuic.example.com" },
    };

    private static Profile WireGuard() => new()
    {
        Id = Guid.Empty,
        Name = "local wg",
        Server = new ServerEndpoint("127.0.0.1", 1),
        Protocol = new WireGuardSettings { PrivateKey = new Secret(Key32), PeerPublicKey = new Secret(Key32), LocalAddresses = new[] { "10.250.0.2/32" } },
    };

    private static void RequireSingBox() =>
        Assert.SkipWhen(CoreLocator.Find(CoreKind.SingBox) is null, "sing-box не скачан: dotnet run tools/cores/fetch-cores.cs");

    [Theory]
    [InlineData("tuic")]
    [InlineData("wireguard")]
    public async Task SingBoxStartsAndPassesTrafficThroughSocksAndHttp(string kind)
    {
        RequireSingBox();
        var ct = TestContext.Current.CancellationToken;
        await using var echo = new EchoServer();

        await using var session = await CoreLauncher.StartAsync(CoreKind.SingBox, kind == "tuic" ? Tuic() : WireGuard(), s_anyPorts, s_masker, guard: null, ct);

        Assert.Equal(CoreKind.SingBox, session.Core);
        Assert.Equal("ping-socks", await ProxyClient.EchoViaSocks5Async(session.SocksPort, echo.Port, "ping-socks", ct));
        Assert.Equal("ping-http", await ProxyClient.EchoViaHttpConnectAsync(session.HttpPort, echo.Port, "ping-http", ct));
    }

    [Fact]
    public async Task SingBoxCrashIsDetectedWithinThreeSeconds()
    {
        RequireSingBox();
        var ct = TestContext.Current.CancellationToken;
        await using var session = await CoreLauncher.StartAsync(CoreKind.SingBox, Tuic(), s_anyPorts, s_masker, guard: null, ct);

        var stopwatch = Stopwatch.StartNew();
        using (var external = Process.GetProcessById(session.Process.ProcessId))
        {
            external.Kill();
        }

        var exit = await session.Process.Completion.WaitAsync(TimeSpan.FromSeconds(3), ct);

        Assert.False(exit.Expected);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ProfileUnsupportedBySingBoxNamesTheCore()
    {
        RequireSingBox();
        var profile = Tuic() with { Security = new TlsSecurity { Sni = "tuic.example.com", PinnedPeerCertSha256 = new[] { new string('a', 64) } } };

        var error = await Assert.ThrowsAsync<CoreStartException>(() =>
            CoreLauncher.StartAsync(CoreKind.SingBox, profile, s_anyPorts, s_masker, guard: null, TestContext.Current.CancellationToken));

        Assert.Equal(CoreStartFailure.ConfigNotGenerated, error.Failure);
        Assert.Equal(CoreKind.SingBox, error.Core);
        Assert.Equal(new CoreConfigError(CoreConfigErrorCode.UnsupportedFeature, "security.pinnedPeerCertSha256"), error.ConfigError);
    }

    [Theory]
    [InlineData(CorePreference.Auto, "tuic", CoreKind.SingBox)]
    [InlineData(CorePreference.Auto, "vless", CoreKind.Xray)]
    [InlineData(CorePreference.SingBox, "vless", CoreKind.SingBox)]
    [InlineData(CorePreference.Xray, "tuic", CoreKind.Xray)]
    public void CoreIsSelectedByProtocolOrUserChoice(CorePreference preference, string protocol, CoreKind expected)
    {
        var profile = (protocol == "tuic" ? Tuic() : Tuic() with { Protocol = new VlessSettings { Id = new Secret(Uuid) }, Security = new NoSecurity() }) with { Core = preference };

        Assert.Equal(expected, CoreSelection.For(profile));
    }
}
