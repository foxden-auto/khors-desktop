using Khors.Core.Generators;
using Khors.Core.Generators.Xray;
using Khors.Core.Profiles;
using Xunit;

namespace Khors.Core.Tests.Profiles;

/// <summary>Правила валидатора для Hysteria2, TUIC, WireGuard (ROADMAP 2.3).</summary>
public class UdpProtocolValidatorTests
{
    private const string Key32 = "Zx8Q2mT9vK4rL7pW1sN6bY3cH5jD0fG8aE2uI9oP4kM=";

    private static Profile Hysteria(Hysteria2Settings protocol, SecuritySettings? security = null) => new()
    {
        Id = Guid.Empty,
        Name = "hy2",
        Server = new ServerEndpoint("hy.example.com", 443),
        Protocol = protocol,
        Security = security ?? new TlsSecurity { Sni = "hy.example.com" },
    };

    private static Hysteria2Settings Hy2 => new() { Password = new Secret("Hy2Pa55") };

    private static Profile Tuic(string congestion = "bbr", string udp = "native", string uuid = "8d2e6f10-4b3a-4c5d-9e8f-0a1b2c3d4e5f") => new()
    {
        Id = Guid.Empty,
        Name = "tuic",
        Server = new ServerEndpoint("tuic.example.com", 443),
        Protocol = new TuicSettings { Uuid = new Secret(uuid), Password = new Secret("p"), CongestionControl = congestion, UdpRelayMode = udp },
        Security = new TlsSecurity(),
    };

    private static Profile WireGuard(WireGuardSettings? settings = null) => new()
    {
        Id = Guid.Empty,
        Name = "wg",
        Server = new ServerEndpoint("wg.example.net", 51820),
        Protocol = settings ?? new WireGuardSettings { PrivateKey = new Secret(Key32), PeerPublicKey = new Secret(Key32), LocalAddresses = new[] { "10.0.0.2/32" } },
    };

    private static WireGuardSettings Wg => (WireGuardSettings)WireGuard().Protocol;

    public static TheoryData<Profile, ProfileIssueCode> Broken => new()
    {
        { Hysteria(Hy2 with { Password = new Secret("") }), ProfileIssueCode.PasswordEmpty },
        { Hysteria(Hy2 with { Obfs = "salamander" }), ProfileIssueCode.ObfsInvalid },
        { Hysteria(Hy2 with { Obfs = "xor", ObfsPassword = new Secret("x") }), ProfileIssueCode.ObfsInvalid },
        { Hysteria(Hy2 with { Ports = "443,30000-20000" }), ProfileIssueCode.PortsInvalid },
        { Hysteria(Hy2 with { Ports = "443,70000" }), ProfileIssueCode.PortsInvalid },
        { Hysteria(Hy2, new NoSecurity()), ProfileIssueCode.QuicRequiresTls },
        { Tuic(congestion: "reno"), ProfileIssueCode.TuicModeUnknown },
        { Tuic(udp: "tcp"), ProfileIssueCode.TuicModeUnknown },
        { Tuic(uuid: "not-a-uuid"), ProfileIssueCode.IdInvalid },
        { WireGuard(Wg with { PrivateKey = new Secret("short") }), ProfileIssueCode.WireGuardKeyInvalid },
        { WireGuard(Wg with { PreSharedKey = new Secret("AAAA") }), ProfileIssueCode.WireGuardKeyInvalid },
        { WireGuard(Wg with { LocalAddresses = default }), ProfileIssueCode.WireGuardAddressInvalid },
        { WireGuard(Wg with { LocalAddresses = new[] { "10.0.0.2/33" } }), ProfileIssueCode.WireGuardAddressInvalid },
        { WireGuard(Wg with { Reserved = new[] { 1, 2 } }), ProfileIssueCode.WireGuardReservedInvalid },
        { WireGuard(Wg with { Reserved = new[] { 1, 2, 256 } }), ProfileIssueCode.WireGuardReservedInvalid },
        { WireGuard(Wg with { Mtu = 100 }), ProfileIssueCode.MtuOutOfRange },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void BrokenProfileIsReported(Profile profile, ProfileIssueCode code) =>
        Assert.Contains(ProfileValidator.Validate(profile), i => i.Code == code && i.Severity == ProfileIssueSeverity.Error);

    [Fact]
    public void ValidProfilesHaveNoErrors()
    {
        Profile[] valid =
        [
            Hysteria(Hy2 with { Obfs = "salamander", ObfsPassword = new Secret("o"), Ports = "443,20000-30000" }),
            Tuic(),
            WireGuard(Wg with { LocalAddresses = new[] { "10.0.0.2/32", "fd00::2/128" }, Reserved = new[] { 0, 0, 0 }, Mtu = 1280 }),
        ];

        Assert.All(valid, p => Assert.DoesNotContain(ProfileValidator.Validate(p), i => i.Severity == ProfileIssueSeverity.Error));
    }

    [Fact]
    public void XrayRejectsUdpProtocolsAsSingBoxOnly()
    {
        var options = new XrayConfigOptions { SocksPort = 10808, HttpPort = 10809 };

        Assert.All(
            new[] { Hysteria(Hy2), Tuic(), WireGuard() },
            p => Assert.Equal(new CoreConfigError(CoreConfigErrorCode.UnsupportedFeature, "protocol"), XrayConfigGenerator.Generate(p, options).Error));
    }

    [Fact]
    public void ToStringOfUdpProtocolsHidesSecrets()
    {
        Assert.DoesNotContain("Hy2Pa55", Hy2.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Key32, Wg.ToString(), StringComparison.Ordinal);
    }
}
