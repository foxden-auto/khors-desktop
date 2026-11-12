using System.Text;
using System.Text.Json.Nodes;
using Khors.Core.Generators.SingBox;
using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Core.Storage;
using Khors.Core.Subscriptions;
using Xunit;

namespace Khors.Core.Tests.Import;

public class AmneziaWireGuardTests
{
    private const string PrivateKey = "RotLZicYjYUFFxy3SYlIvOJxpGmWtNthOuIC4yZdZ14=";
    private const string PublicKey = "S2BGSyYNxTf1wij4Qk/8nbbFhbbGfzsDwb4shXmfImw=";

    private const string Config = """
        [Interface]
        PrivateKey = RotLZicYjYUFFxy3SYlIvOJxpGmWtNthOuIC4yZdZ14=
        Address = 10.8.1.2/32, fd00::2
        DNS = 1.1.1.1
        MTU = 1280
        Jc = 4
        Jmin = 10
        Jmax = 50
        S1 = 1
        S2 = 2
        S3 = 3
        S4 = 4
        H1 = 100
        H2 = 200
        H3 = 300
        H4 = 400
        I1 = <b 0x0102>
        HeaderProtectionKey = fictional-header-key
        ContentPaddingAddition = 50-100
        RekeyAfterTime = 100-140
        RekeyTimeout = 4-6
        RejectAfterTime = 160-200
        KeepaliveTimeout = 8-12
        MaxHandshakeAttempts = 15-20
        RandomTrailers = true
        DisableCookies = true
        FutureOption = preserved

        [Peer]
        PublicKey = S2BGSyYNxTf1wij4Qk/8nbbFhbbGfzsDwb4shXmfImw=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = [2001:db8::10]:51820
        """;

    [Fact]
    public void WgQuickConfigAndVpnLinkImportAllOptions()
    {
        var direct = SubscriptionContent.Parse(Config);
        var link = "vpn://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Config)) + "#Test%20AWG";
        var fromLink = ShareLinkParser.Parse(link).Profile;

        Assert.Equal(SubscriptionFormat.AmneziaWireGuard, direct.Format);
        var profile = Assert.Single(direct.Profiles);
        Assert.Equal(fromLink, profile with { Name = "Test AWG" });
        Assert.Equal("2001:db8::10", profile.Server.Host);
        Assert.Equal(51820, profile.Server.Port);
        var awg = Assert.IsType<AmneziaWireGuardSettings>(profile.Protocol);
        Assert.Equal(["10.8.1.2/32", "fd00::2/128"], awg.LocalAddresses);
        Assert.Equal(4, awg.Jc);
        Assert.Equal(10, awg.Jmin);
        Assert.Equal(50, awg.Jmax);
        Assert.Equal("100-140", awg.RekeyAfterTime);
        Assert.Equal("50-100", awg.ContentPaddingAddition);
        Assert.Equal("<b 0x0102>", awg.I1);
        Assert.True(awg.RandomTrailers);
        Assert.True(awg.DisableCookies);
        Assert.Contains(profile.UnknownParams, p => p.Key == "interface.FutureOption" && p.Value == "preserved");
        Assert.Contains(profile.UnknownParams, p => p.Key == "interface.DNS");
    }

    [Fact]
    public void WireGuardLinkRoundTripPreservesAmnezia()
    {
        var parsed = SubscriptionContent.Parse(Config).Profiles.Single();

        var exported = ShareLinkExporter.Export(parsed);
        var roundTrip = ShareLinkParser.Parse(exported).Profile!;

        Assert.Equal(parsed.Protocol, roundTrip.Protocol);
        Assert.Equal(parsed.Server, roundTrip.Server);
        Assert.IsType<AmneziaWireGuardSettings>(roundTrip.Protocol);
    }

    [Fact]
    public void ClashAndSingBoxOptionsAreImported()
    {
        const string clash = """
            proxies:
              - name: AWG
                type: wireguard
                server: 198.51.100.8
                port: 51820
                ip: 10.8.1.2
                private-key: RotLZicYjYUFFxy3SYlIvOJxpGmWtNthOuIC4yZdZ14=
                public-key: S2BGSyYNxTf1wij4Qk/8nbbFhbbGfzsDwb4shXmfImw=
                amnezia-wg-option:
                  jc: 4
                  jmin: 10
                  jmax: 50
                  s1: 1
                  s2: 2
                  h1: 1
                  h2: 2
                  h3: 3
                  h4: 4
            """;
        var singBox = $$$"""
            {"endpoints":[{"type":"wireguard","tag":"AWG","address":["10.8.1.2/32"],"private_key":"{{{PrivateKey}}}","mtu":1280,"peers":[{"address":"198.51.100.8","port":51820,"public_key":"{{{PublicKey}}}","allowed_ips":["0.0.0.0/0"]}],"amnezia":{"jc":4,"jmin":10,"jmax":50,"h1":"1","random_trailers":true}}],"outbounds":[{"type":"direct","tag":"direct"}]}
            """;

        var fromClash = Assert.IsType<AmneziaWireGuardSettings>(Assert.Single(SubscriptionContent.Parse(clash).Profiles).Protocol);
        var fromSingBox = Assert.IsType<AmneziaWireGuardSettings>(Assert.Single(SubscriptionContent.Parse(singBox).Profiles).Protocol);

        Assert.Equal(4, fromClash.Jc);
        Assert.Equal(2, fromClash.S2);
        Assert.Equal(4, fromSingBox.Jc);
        Assert.True(fromSingBox.RandomTrailers);
    }

    [Fact]
    public void GeneratorAndStorageRetainAmneziaType()
    {
        var profile = SubscriptionContent.Parse(Config).Profiles.Single();

        var restored = StorageJson.ParseProfile(StorageJson.SerializeProfile(profile));
        var generated = SingBoxConfigGenerator.Generate(profile, new SingBoxConfigOptions { SocksPort = 10808, HttpPort = 10809 });
        var endpoint = JsonNode.Parse(generated.Json!)!["endpoints"]![0]!;
        var amnezia = endpoint["amnezia"]!;

        Assert.IsType<AmneziaWireGuardSettings>(restored!.Protocol);
        Assert.Equal(profile, restored);
        Assert.Equal(4, amnezia["jc"]!.GetValue<int>());
        Assert.Equal("50-100", amnezia["content_padding_addition"]!.GetValue<string>());
        Assert.True(amnezia["random_trailers"]!.GetValue<bool>());
        Assert.Equal("<b 0x0102>", amnezia["i1"]!.GetValue<string>());
    }
}
