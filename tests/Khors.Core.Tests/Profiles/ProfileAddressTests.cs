using System.Net;
using System.Text.Json.Nodes;
using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Core.Storage;
using Xunit;

namespace Khors.Core.Tests.Profiles;

public class ProfileAddressTests
{
    private static readonly IPAddress s_ip = IPAddress.Parse("203.0.113.7");

    private static Profile Parse(string link) => ShareLinkParser.Parse(link).Profile!;

    [Fact]
    public void TlsSniAndWsHostKeepServerName()
    {
        var profile = Parse("vless://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@cdn.example.com:443?encryption=none&security=tls&type=ws&path=%2Fws#WS");

        var resolved = ProfileAddress.WithResolvedHost(profile, s_ip);

        Assert.Equal(new ServerEndpoint("203.0.113.7", 443), resolved.Server);
        Assert.Equal("cdn.example.com", Assert.IsType<TlsSecurity>(resolved.Security).Sni);
        Assert.Equal("cdn.example.com", Assert.IsType<WsTransport>(resolved.Transport).Host);
    }

    [Fact]
    public void ExplicitNamesAreNotReplaced()
    {
        var profile = Parse("vless://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@vpn.example.com:443?encryption=none&security=tls&sni=front.example.net&type=grpc&serviceName=svc&authority=api.example.net#GRPC");

        var resolved = ProfileAddress.WithResolvedHost(profile, s_ip);

        Assert.Equal("front.example.net", Assert.IsType<TlsSecurity>(resolved.Security).Sni);
        Assert.Equal("api.example.net", Assert.IsType<GrpcTransport>(resolved.Transport).Authority);
    }

    [Fact]
    public void RealityKeepsItsOwnSni()
    {
        var profile = TestProfiles.VlessReality();

        var resolved = ProfileAddress.WithResolvedHost(profile, s_ip);

        Assert.Equal("203.0.113.7", resolved.Server.Host);
        Assert.Equal(profile.Security, resolved.Security);
        Assert.Equal(profile.Transport, resolved.Transport);
    }

    [Fact]
    public void IpAddressIsLeftAsIs()
    {
        var profile = Parse("trojan://secret@198.51.100.3:443?security=tls#T");

        Assert.Same(profile, ProfileAddress.WithResolvedHost(profile, s_ip));
    }

    public static TheoryData<string> LinkVectors => new(
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Vectors", "links"), "*.json")
            .Where(f => JsonNode.Parse(File.ReadAllText(f))!["expected"] is not null)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal));

    /// <summary>Профиль передаётся службе как JSON (режим TUN) и возвращается тем же.</summary>
    [Theory]
    [MemberData(nameof(LinkVectors))]
    public void ProfileJsonRoundTrip(string name)
    {
        var link = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "links", name + ".json")))!["link"]!.GetValue<string>();
        var profile = Parse(link);

        Assert.Equal(profile, StorageJson.ParseProfile(StorageJson.SerializeProfile(profile)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000000","name":"x","server":{"host":"a","port":1},"protocol":{"type":"exec"}}""")]
    public void BadProfileJsonIsRejected(string json) => Assert.Null(StorageJson.ParseProfile(json));
}
