using System.Text.Json;
using Khors.Core.Profiles;
using Xunit;

namespace Khors.Core.Tests.Profiles;

public class ProfileJsonTests
{
    public static TheoryData<Profile> Profiles => TestProfiles.All;

    [Theory]
    [MemberData(nameof(Profiles))]
    public void RoundTripPreservesProfile(Profile profile)
    {
        var json = ProfileJson.Serialize(profile);
        var restored = ProfileJson.Deserialize(json);

        Assert.Equal(profile, restored);
        Assert.Equal(json, ProfileJson.Serialize(restored));
    }

    [Fact]
    public void JsonUsesTypeDiscriminatorsAndStringEnums()
    {
        using var document = JsonDocument.Parse(ProfileJson.Serialize(TestProfiles.VlessReality()));
        var root = document.RootElement;

        Assert.Equal("auto", root.GetProperty("core").GetString());
        Assert.Equal("vless", root.GetProperty("protocol").GetProperty("type").GetString());
        Assert.Equal("tcp", root.GetProperty("transport").GetProperty("type").GetString());
        Assert.Equal("reality", root.GetProperty("security").GetProperty("type").GetString());
        Assert.Equal(TestProfiles.Uuid, root.GetProperty("protocol").GetProperty("id").GetString());
        Assert.Equal("chrome", root.GetProperty("security").GetProperty("fingerprint").GetString());
        Assert.Equal(2, root.GetProperty("unknownParams").GetArrayLength());
        Assert.False(root.TryGetProperty("subscriptionId", out _));
    }

    [Fact]
    public void DeserializeAcceptsDiscriminatorNotFirstAndAppliesDefaults()
    {
        const string json = """
            {
              "id": "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a",
              "name": "minimal",
              "server": { "host": "vpn.example.com", "port": 443 },
              "protocol": { "password": "Fictional-Pa55", "type": "trojan" }
            }
            """;

        var profile = ProfileJson.Deserialize(json);

        Assert.Equal(CorePreference.Auto, profile.Core);
        Assert.IsType<TrojanSettings>(profile.Protocol);
        Assert.IsType<TcpTransport>(profile.Transport);
        Assert.IsType<NoSecurity>(profile.Security);
        Assert.Empty(profile.UnknownParams);
    }

    [Fact]
    public void DeserializeAppliesDefaultsOfNestedSettings()
    {
        const string json = """
            {
              "id": "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a",
              "name": "defaults",
              "server": { "host": "vpn.example.com", "port": 443 },
              "protocol": { "type": "vless", "id": "3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b" },
              "transport": { "type": "grpc" },
              "security": { "type": "reality", "sni": "www.example.org", "publicKey": "Zx8Q2mT9vK4rL7pW1sN6bY3cH5jD0fG8aE2uI9oP4kM" }
            }
            """;

        var profile = ProfileJson.Deserialize(json);

        Assert.Equal("none", Assert.IsType<VlessSettings>(profile.Protocol).Encryption);
        var grpc = Assert.IsType<GrpcTransport>(profile.Transport);
        Assert.Equal("gun", grpc.Mode);
        Assert.Equal(string.Empty, grpc.ServiceName);
        Assert.Equal(RealitySecurity.DefaultFingerprint, Assert.IsType<RealitySecurity>(profile.Security).Fingerprint);
        var ws = ProfileJson.Deserialize(json.Replace("\"grpc\"", "\"ws\"", StringComparison.Ordinal)).Transport;
        Assert.Equal("/", Assert.IsType<WsTransport>(ws).Path);
    }

    [Theory]
    [InlineData("""{ "name": "x", "server": { "host": "h", "port": 1 }, "protocol": { "type": "trojan", "password": "p" } }""")]
    [InlineData("""{ "id": "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a", "name": "x", "server": { "host": "h", "port": 1 }, "protocol": { "type": "trojan" } }""")]
    [InlineData("""{ "id": "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a", "name": null, "server": { "host": "h", "port": 1 }, "protocol": { "type": "trojan", "password": "p" } }""")]
    [InlineData("""{ "id": "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a", "name": "x", "server": { "host": "h", "port": 1 }, "protocol": { "type": "future-protocol" } }""")]
    [InlineData("""{ "id": "0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a", "name": "x", "server": { "host": "h", "port": 1 }, "protocol": { "type": "trojan", "password": "p" }, "core": "unknown" }""")]
    [InlineData("not json")]
    public void DeserializeRejectsInvalidProfile(string json) =>
        Assert.Throws<JsonException>(() => ProfileJson.Deserialize(json));
}
