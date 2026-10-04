using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Khors.Core.Diagnostics;
using Xunit;

namespace Khors.Core.Tests.Diagnostics;

// Все ключи, UUID, пароли и адреса вымышленные: IP из RFC 5737 / RFC 3849, домены example.*.
public partial class SecretMaskerTests
{
    private const string Uuid = "3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b";
    private const string RealityPublicKey = "Zx8Q2mT9vK4rL7pW1sN6bY3cH5jD0fG8aE2uI9oP4kM";
    private const string ShortId = "6ba85179e30d4fc2";

    private readonly SecretMasker _masker = new(Encoding.UTF8.GetBytes("khors-test-key"));

    public static TheoryData<string, string[], string[]> TextVectors => new()
    {
        {
            $"vless://{Uuid}@vpn.example.com:443?encryption=none&flow=xtls-rprx-vision&security=reality&sni=www.example.org&fp=chrome&pbk={RealityPublicKey}&sid={ShortId}&type=tcp#Germany%201",
            [Uuid, "vpn.example.com", "www.example.org", RealityPublicKey, ShortId],
            ["vless://", ":443?", "encryption=none", "flow=xtls-rprx-vision", "security=reality", "fp=chrome", "type=tcp", "#Germany%201"]
        },
        {
            "trojan://S3cr3t-Pa55@198.51.100.7:8443?security=tls&sni=cdn.example.net&type=ws&host=cdn.example.net&path=%2Fws#tr",
            ["S3cr3t-Pa55", "198.51.100.7", "cdn.example.net"],
            ["trojan://", ":8443?", "security=tls", "type=ws", "path=%2Fws", "#tr"]
        },
        {
            "hysteria2://hy2-auth-Secret@[2001:db8::10]:443/?sni=hy.example.com&obfs=salamander&obfs-password=0bfsPa55w0rd#hy",
            ["hy2-auth-Secret", "2001:db8::10", "hy.example.com", "0bfsPa55w0rd"],
            ["hysteria2://", ":443/?", "obfs=salamander", "#hy"]
        },
        {
            "tuic://8d2e6f10-4b3a-4c5d-9e8f-0a1b2c3d4e5f:TuicPa55@203.0.113.20:443?congestion_control=bbr&alpn=h3&sni=tuic.example.com#t",
            ["8d2e6f10-4b3a-4c5d-9e8f-0a1b2c3d4e5f", "TuicPa55", "203.0.113.20", "tuic.example.com"],
            ["tuic://", "congestion_control=bbr", "alpn=h3"]
        },
        {
            $"ss://{Base64("chacha20-ietf-poly1305:SsPa55w0rd")}@192.0.2.44:8388#ss",
            ["SsPa55w0rd", Base64("chacha20-ietf-poly1305:SsPa55w0rd"), "192.0.2.44"],
            ["ss://chacha20-ietf-poly1305:", ":8388#ss"]
        },
        {
            "ss://2022-blake3-aes-128-gcm:Plain2022Key@192.0.2.45:8388",
            ["Plain2022Key", "192.0.2.45"],
            ["ss://2022-blake3-aes-128-gcm:", ":8388"]
        },
        {
            $"ss://{Base64("aes-256-gcm:LegacyPa55@ss.example.net:8389")}#old",
            ["LegacyPa55", "ss.example.net", Base64("aes-256-gcm:LegacyPa55@ss.example.net:8389")],
            ["ss://aes-256-gcm:", ":8389#old"]
        },
        {
            "https://sub.example.com/api/v1/client/subscribe?token=a1b2c3d4e5f6g7h8",
            ["sub.example.com", "a1b2c3d4e5f6g7h8"],
            ["https://", "/api/v1/client/subscribe?token="]
        },
        {
            "https://panel.example.net/sub/Xk29fJ3kLm0pQr7s",
            ["panel.example.net", "Xk29fJ3kLm0pQr7s"],
            ["https://", "/sub/"]
        },
        {
            "2026/10/04 12:34:56.789012 from 192.0.2.10:51234 accepted tcp:www.example.com:443 [socks -> proxy]",
            ["192.0.2.10", "www.example.com"],
            ["2026/10/04 12:34:56.789012 from ", ":51234 accepted tcp:", ":443 [socks -> proxy]"]
        },
        {
            "+0300 2026-10-04 12:34:56 ERROR [3141592 0ms] outbound/vless[proxy]: dial tcp 203.0.113.9:443: i/o timeout",
            ["203.0.113.9"],
            ["+0300 2026-10-04 12:34:56 ERROR [3141592 0ms] outbound/vless[proxy]: dial tcp ", ":443: i/o timeout"]
        },
        {
            $"publicKey: {RealityPublicKey}, shortId={ShortId} password='Hunter2Pass' uuid={Uuid}",
            [RealityPublicKey, ShortId, "Hunter2Pass", Uuid],
            ["publicKey: ", "shortId=", "password='"]
        },
        {
            $"user {Uuid} connected",
            [Uuid],
            ["user ", " connected"]
        },
        {
            "dial 2001:db8:85a3::8a2e:370:7334 port 443",
            ["2001:db8:85a3::8a2e:370:7334"],
            ["dial ", " port 443"]
        },
        {
            "Host: cdn.example.net",
            ["cdn.example.net"],
            ["Host: "]
        },
        {
            $"bad link (vless://{Uuid}@vpn.example.com:443).",
            [Uuid, "vpn.example.com"],
            ["bad link (vless://", ":443)."]
        },
    };

    public static TheoryData<string> TextWithoutSecrets => new()
    {
        "app/proxyman/outbound: failed to process outbound traffic > proxy/vless/outbound: failed to find an available destination > common/retry: [transport/internet/reality: REALITY: processed invalid connection] > reality verification failed",
        "listening on 127.0.0.1:10808, http://localhost:2080, socks5://[::1]:1080",
        "mac 00:1a:2b:3c:4d:5e at 23:59:59",
        "panic at main.go:123 +0x1d",
        "Xray 26.9.9 (Xray, Penetrates Everything.)",
        "route 0.0.0.0/0 via tun",
    };

    [Theory]
    [MemberData(nameof(TextVectors))]
    public void MaskTextRemovesSecretsAndKeepsContext(string input, string[] secrets, string[] kept)
    {
        var masked = _masker.MaskText(input);

        AssertNoLeak(masked, secrets);
        Assert.All(kept, k => Assert.Contains(k, masked, StringComparison.Ordinal));
        Assert.Matches(MaskInOutput(), masked);
    }

    [Theory]
    [MemberData(nameof(TextVectors))]
    public void MaskTextIsIdempotent(string input, string[] secrets, string[] kept)
    {
        _ = secrets;
        _ = kept;
        var once = _masker.MaskText(input);

        Assert.Equal(once, _masker.MaskText(once));
    }

    [Theory]
    [MemberData(nameof(TextWithoutSecrets))]
    public void MaskTextLeavesTextWithoutSecretsUnchanged(string input) =>
        Assert.Equal(input, _masker.MaskText(input));

    [Fact]
    public void MaskTextKeepsVmessLinkDecodableWithMaskedJson()
    {
        var json = $$"""{"v":"2","ps":"vm","add":"vm.example.com","port":"443","id":"{{Uuid}}","aid":"0","net":"ws","type":"none","host":"cdn.example.net","path":"/vm","tls":"tls","sni":"vm.example.com"}""";
        var link = "vmess://" + Base64(json);

        var masked = _masker.MaskText(link);

        Assert.StartsWith("vmess://", masked, StringComparison.Ordinal);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(masked["vmess://".Length..]));
        AssertNoLeak(decoded, [Uuid, "vm.example.com", "cdn.example.net"]);
        using var document = JsonDocument.Parse(decoded);
        Assert.Equal("ws", document.RootElement.GetProperty("net").GetString());
        Assert.Equal("443", document.RootElement.GetProperty("port").GetString());
        Assert.Equal(masked, _masker.MaskText(masked));
    }

    [Fact]
    public void MaskJsonMasksXrayConfig()
    {
        var config = $$"""
            {
              // Комментарии допустимы в конфигах Xray.
              "log": { "loglevel": "warning" },
              "inbounds": [ { "listen": "127.0.0.1", "port": 10808, "protocol": "socks" } ],
              "outbounds": [
                {
                  "protocol": "vless",
                  "tag": "proxy",
                  "settings": {
                    "vnext": [ {
                      "address": "vpn.example.com",
                      "port": 443,
                      "users": [ { "id": "{{Uuid}}", "encryption": "none", "flow": "xtls-rprx-vision" } ]
                    } ]
                  },
                  "streamSettings": {
                    "network": "tcp",
                    "security": "reality",
                    "realitySettings": {
                      "serverName": "www.example.org",
                      "fingerprint": "chrome",
                      "publicKey": "{{RealityPublicKey}}",
                      "shortId": "{{ShortId}}",
                      "shortIds": [ "a1b2", "c3d4e5f6" ],
                      "spiderX": "/"
                    }
                  }
                }
              ],
              "dns": { "servers": [ "https://198.51.100.53/dns-query", "localhost" ] }
            }
            """;

        var masked = _masker.MaskJson(config);

        AssertNoLeak(masked, [Uuid, "vpn.example.com", "www.example.org", RealityPublicKey, ShortId, "a1b2", "c3d4e5f6", "198.51.100.53"]);
        using var document = JsonDocument.Parse(masked);
        var root = document.RootElement;
        Assert.Equal("127.0.0.1", root.GetProperty("inbounds")[0].GetProperty("listen").GetString());
        Assert.Equal(10808, root.GetProperty("inbounds")[0].GetProperty("port").GetInt32());
        var outbound = root.GetProperty("outbounds")[0];
        Assert.Equal("proxy", outbound.GetProperty("tag").GetString());
        var user = outbound.GetProperty("settings").GetProperty("vnext")[0].GetProperty("users")[0];
        Assert.Equal("xtls-rprx-vision", user.GetProperty("flow").GetString());
        Assert.True(SecretMasker.IsMask(user.GetProperty("id").GetString()!));
        var reality = outbound.GetProperty("streamSettings").GetProperty("realitySettings");
        Assert.Equal("chrome", reality.GetProperty("fingerprint").GetString());
        Assert.Equal("/", reality.GetProperty("spiderX").GetString());
        Assert.StartsWith("{sid-", reality.GetProperty("shortIds")[0].GetString(), StringComparison.Ordinal);
        Assert.Contains("/dns-query", masked, StringComparison.Ordinal);
        Assert.Equal("localhost", root.GetProperty("dns").GetProperty("servers")[1].GetString());
    }

    [Fact]
    public void MaskJsonMasksSingBoxConfig()
    {
        var config = $$"""
            {
              "dns": {
                "servers": [ { "tag": "dns-remote", "address": "tls://192.0.2.53" } ],
                "rules": [ { "server": "dns-remote" } ]
              },
              "outbounds": [
                {
                  "type": "vless", "tag": "proxy",
                  "server": "203.0.113.30", "server_port": 443,
                  "uuid": "{{Uuid}}",
                  "tls": {
                    "enabled": true, "server_name": "www.example.org",
                    "utls": { "enabled": true, "fingerprint": "chrome" },
                    "reality": { "enabled": true, "public_key": "{{RealityPublicKey}}", "short_id": "{{ShortId}}" }
                  }
                },
                {
                  "type": "wireguard", "tag": "wg",
                  "private_key": "WgPrivKeyFakeAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                  "peers": [ {
                    "server": "wg.example.net", "server_port": 51820,
                    "public_key": "WgPubKeyFakeBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=",
                    "pre_shared_key": "WgPskFakeCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC=",
                    "allowed_ips": [ "0.0.0.0/0", "::/0" ],
                    "endpoint": "198.51.100.9:51820"
                  } ]
                },
                {
                  "type": "hysteria2", "tag": "hy2",
                  "server": "hy.example.com", "password": "Hy2Pa55",
                  "obfs": { "type": "salamander", "password": "ObfsPa55" }
                }
              ]
            }
            """;

        var masked = _masker.MaskJson(config);

        AssertNoLeak(masked,
        [
            Uuid, "203.0.113.30", "www.example.org", RealityPublicKey, ShortId, "192.0.2.53",
            "WgPrivKeyFake", "WgPubKeyFake", "WgPskFake", "wg.example.net", "198.51.100.9",
            "hy.example.com", "Hy2Pa55", "ObfsPa55",
        ]);
        using var document = JsonDocument.Parse(masked);
        var root = document.RootElement;
        Assert.Equal("dns-remote", root.GetProperty("dns").GetProperty("rules")[0].GetProperty("server").GetString());
        var peer = root.GetProperty("outbounds")[1].GetProperty("peers")[0];
        Assert.Equal(51820, peer.GetProperty("server_port").GetInt32());
        Assert.Equal("0.0.0.0/0", peer.GetProperty("allowed_ips")[0].GetString());
        Assert.EndsWith(":51820", peer.GetProperty("endpoint").GetString(), StringComparison.Ordinal);
        Assert.Equal("salamander", root.GetProperty("outbounds")[2].GetProperty("obfs").GetProperty("type").GetString());
    }

    [Fact]
    public void MaskJsonIsIdempotent()
    {
        var json = $$"""{ "address": "vpn.example.com:443", "id": "{{Uuid}}", "note": "server=203.0.113.1" }""";
        var once = _masker.MaskJson(json);

        Assert.Equal(once, _masker.MaskJson(once));
    }

    [Fact]
    public void MaskJsonFallsBackToTextForInvalidJson()
    {
        var masked = _masker.MaskJson($"not json: {Uuid} at vpn.example.com:443");

        AssertNoLeak(masked, [Uuid, "vpn.example.com"]);
    }

    [Fact]
    public void SameValueGetsSameMaskWithinOneMasker()
    {
        var fromLink = _masker.MaskText($"vless://{Uuid}@vpn.example.com:443");
        var fromJson = _masker.MaskJson($$"""{ "id": "{{Uuid}}", "address": "VPN.example.com" }""");
        var uuidMask = _masker.Mask(SecretKind.Uuid, Uuid);
        var hostMask = _masker.Mask(SecretKind.Host, "vpn.example.com");

        Assert.Contains(uuidMask, fromLink, StringComparison.Ordinal);
        Assert.Contains(uuidMask, fromJson, StringComparison.Ordinal);
        Assert.Contains(hostMask, fromLink, StringComparison.Ordinal);
        Assert.Contains(hostMask, fromJson, StringComparison.Ordinal);
        Assert.NotEqual(uuidMask, _masker.Mask(SecretKind.Uuid, "8d2e6f10-4b3a-4c5d-9e8f-0a1b2c3d4e5f"));
    }

    [Fact]
    public void DifferentMaskersProduceUnlinkableMasks()
    {
        var first = new SecretMasker().Mask(SecretKind.Password, "Hunter2Pass");
        var second = new SecretMasker().Mask(SecretKind.Password, "Hunter2Pass");

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(SecretKind.Uuid, "uuid")]
    [InlineData(SecretKind.Password, "password")]
    [InlineData(SecretKind.Key, "key")]
    [InlineData(SecretKind.ShortId, "sid")]
    [InlineData(SecretKind.Host, "host")]
    [InlineData(SecretKind.Token, "token")]
    public void MaskHasKindLabelAndShortTag(SecretKind kind, string label)
    {
        var mask = _masker.Mask(kind, "fictional-value.example.com");

        Assert.Matches($"^\\{{{label}-[0-9a-f]{{6}}\\}}$", mask);
        Assert.True(SecretMasker.IsMask(mask));
        Assert.Equal(mask, _masker.Mask(kind, mask));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("localhost")]
    [InlineData("0.0.0.0")]
    [InlineData("")]
    public void LoopbackAndEmptyHostsAreNotMasked(string host) =>
        Assert.Equal(host, _masker.Mask(SecretKind.Host, host));

    private static void AssertNoLeak(string output, IEnumerable<string> secrets) =>
        Assert.All(secrets, s => Assert.DoesNotContain(s, output, StringComparison.OrdinalIgnoreCase));

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    [GeneratedRegex(@"\{(?:uuid|password|key|sid|host|token)-[0-9a-f]{6}\}")]
    private static partial Regex MaskInOutput();
}
