using Khors.Core.Profiles;
using Xunit;

namespace Khors.Core.Tests.Profiles;

// Вымышленные данные: IP из RFC 5737, домены example.*, ключи сгенерированы для тестов.
internal static class TestProfiles
{
    public const string Uuid = "3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b";
    public const string RealityPublicKey = "Zx8Q2mT9vK4rL7pW1sN6bY3cH5jD0fG8aE2uI9oP4kM";
    public const string ShortId = "6ba85179e30d4fc2";
    public const string Host = "vpn.example.com";
    public const string Sni = "www.example.org";
    public const string Password = "Fictional-Pa55";

    public static readonly DateTimeOffset UpdatedAt = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    public static Profile VlessReality() => new()
    {
        Id = Guid.Parse("0f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a"),
        Name = "Германия 1",
        Group = "Тест",
        UpdatedAt = UpdatedAt,
        Server = new ServerEndpoint(Host, 443),
        Protocol = new VlessSettings { Id = new Secret(Uuid), Flow = "xtls-rprx-vision" },
        Transport = new TcpTransport(),
        Security = new RealitySecurity
        {
            Sni = Sni,
            PublicKey = new Secret(RealityPublicKey),
            ShortId = new Secret(ShortId),
            SpiderX = "/",
        },
        UnknownParams = new[] { new UnknownParam("future", "value-1"), new UnknownParam("future", "value-2") },
    };

    public static Profile VmessWsTls() => new()
    {
        Id = Guid.Parse("1a2b3c4d-5e6f-4a1b-8c2d-3e4f5a6b7c8d"),
        Name = "vmess",
        SubscriptionId = Guid.Parse("9e8d7c6b-5a4f-4e3d-9c2b-1a0f9e8d7c6b"),
        UpdatedAt = UpdatedAt,
        Core = CorePreference.Xray,
        Server = new ServerEndpoint("198.51.100.7", 8443),
        Protocol = new VmessSettings { Id = new Secret(Uuid), AlterId = 0, Cipher = "aes-128-gcm" },
        Transport = new WsTransport { Path = "/ws?ed=2048", Host = "cdn.example.net" },
        Security = new TlsSecurity { Sni = "cdn.example.net", Alpn = new[] { "h2", "http/1.1" }, Fingerprint = "firefox" },
        Mux = new MuxSettings { Enabled = true, Concurrency = 8 },
    };

    public static Profile TrojanGrpc() => new()
    {
        Id = Guid.Parse("2b3c4d5e-6f70-4b1c-9d2e-3f4a5b6c7d8e"),
        Name = "trojan",
        UpdatedAt = UpdatedAt,
        Core = CorePreference.SingBox,
        Server = new ServerEndpoint("2001:db8::10", 443),
        Protocol = new TrojanSettings { Password = new Secret(Password) },
        Transport = new GrpcTransport { ServiceName = "tun", Mode = "multi", Authority = "grpc.example.net" },
        Security = new TlsSecurity { Sni = "grpc.example.net" },
    };

    public static Profile ShadowsocksPlugin() => new()
    {
        Id = Guid.Parse("3c4d5e6f-7081-4c2d-8e3f-4a5b6c7d8e9f"),
        Name = "ss",
        UpdatedAt = UpdatedAt,
        Server = new ServerEndpoint("192.0.2.44", 8388),
        Protocol = new ShadowsocksSettings
        {
            Method = "chacha20-ietf-poly1305",
            Password = new Secret(Password),
            Plugin = "obfs-local",
            PluginOptions = "obfs=http;obfs-host=obfs.example.com",
        },
    };

    public static Profile VlessXhttpReality() => VlessReality() with
    {
        Protocol = new VlessSettings { Id = new Secret(Uuid) },
        Transport = new XhttpTransport { Path = "/xh", Mode = "stream-one", Extra = """{"xPaddingBytes":"100-1000"}""" },
    };

    public static Profile VlessHttpUpgrade() => VlessReality() with
    {
        Protocol = new VlessSettings { Id = new Secret(Uuid) },
        Transport = new HttpUpgradeTransport { Path = "/up", Host = "up.example.net" },
        Security = new TlsSecurity { Sni = "up.example.net" },
    };

    public static TheoryData<Profile> All => new()
    {
        VlessReality(),
        VmessWsTls(),
        TrojanGrpc(),
        ShadowsocksPlugin(),
        VlessXhttpReality(),
        VlessHttpUpgrade(),
    };
}
