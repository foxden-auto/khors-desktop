using Khors.Core.Generators;
using Khors.Core.Generators.Xray;
using Khors.Core.Profiles;
using Khors.Core.Tests.Profiles;
using Xunit;

namespace Khors.Core.Tests.Generators;

public class XrayConfigGeneratorTests
{
    private static readonly XrayConfigOptions s_options = new() { SocksPort = 10808, HttpPort = 10809 };

    private static Profile PlainVless(string host) => TestProfiles.VlessReality() with
    {
        Server = new ServerEndpoint(host, 8080),
        Protocol = new VlessSettings { Id = new Secret(TestProfiles.Uuid) },
        Security = new NoSecurity(),
        UnknownParams = default,
    };

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.10")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("localhost")]
    public void PlaintextVlessToPrivateAddressIsAllowed(string host) =>
        Assert.True(XrayConfigGenerator.Generate(PlainVless(host), s_options).IsSuccess);

    [Theory]
    [InlineData("172.32.0.1")]
    [InlineData("203.0.113.5")]
    [InlineData("2001:db8::1")]
    [InlineData("vpn.example.com")]
    public void PlaintextVlessToPublicAddressIsRejected(string host)
    {
        var result = XrayConfigGenerator.Generate(PlainVless(host), s_options);

        Assert.Equal(new CoreConfigError(CoreConfigErrorCode.UnsupportedFeature, "security"), result.Error);
    }

    [Fact]
    public void PlaintextVlessWithVlessEncryptionIsAllowed()
    {
        var profile = PlainVless("vpn.example.com") with
        {
            Protocol = new VlessSettings { Id = new Secret(TestProfiles.Uuid), Encryption = "mlkem768x25519plus.native.0rtt.fictional" },
        };

        Assert.True(XrayConfigGenerator.Generate(profile, s_options).IsSuccess);
    }

    [Fact]
    public void LegacyVmessAlterIdIsRejected()
    {
        var profile = TestProfiles.VmessWsTls() with
        {
            Protocol = new VmessSettings { Id = new Secret(TestProfiles.Uuid), AlterId = 64 },
        };

        Assert.Equal(new CoreConfigError(CoreConfigErrorCode.UnsupportedFeature, "protocol.alterId"), XrayConfigGenerator.Generate(profile, s_options).Error);
    }

    [Fact]
    public void AllowInsecureWithPinnedCertificateIsAccepted()
    {
        var profile = TestProfiles.TrojanGrpc() with
        {
            Core = CorePreference.Auto,
            Security = new TlsSecurity
            {
                Sni = "grpc.example.net",
                AllowInsecure = true,
                PinnedPeerCertSha256 = new[] { "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08" },
            },
        };

        var result = XrayConfigGenerator.Generate(profile, s_options);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain("allowInsecure", result.Json, StringComparison.Ordinal);
        Assert.Contains("pinnedPeerCertSha256", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidProfileIsRejectedWithFirstErrorField()
    {
        var profile = TestProfiles.VlessReality() with { Server = new ServerEndpoint(TestProfiles.Host, 0) };

        Assert.Equal(new CoreConfigError(CoreConfigErrorCode.ProfileInvalid, "server.port"), XrayConfigGenerator.Generate(profile, s_options).Error);
    }

    [Fact]
    public void ResultToStringDoesNotLeakConfig()
    {
        var result = XrayConfigGenerator.Generate(TestProfiles.VlessReality(), s_options);

        Assert.DoesNotContain(TestProfiles.Uuid, result.ToString(), StringComparison.Ordinal);
    }
}
