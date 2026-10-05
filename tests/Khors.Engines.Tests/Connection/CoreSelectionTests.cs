using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Engines.Connection;
using Xunit;

namespace Khors.Engines.Tests.Connection;

/// <summary>Автовыбор и ручной выбор ядра (docs/SPEC.md, 3.4). Ссылки — с вымышленными ключами и адресами.</summary>
public class CoreSelectionTests
{
    private const string Uuid = "3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b";
    private const string RealityQuery = "security=reality&sni=www.example.org&fp=chrome&pbk=Zx8Q2mT9vK4rL7pW1sN6bY3cH5jD0fG8aE2uI9oP4kM&sid=6ba85179e30d4fc2";

    // VMess с alterId = 64 и TLS.
    private const string LegacyVmess = "vmess://eyJ2IjoiMiIsInBzIjoibGVnYWN5IiwiYWRkIjoidm0uZXhhbXBsZS5jb20iLCJwb3J0IjoiNDQzIiwiaWQiOiIzZjFjMmE5ZS03YjRkLTRlOGEtOWMyMS01ZDZlN2Y4MDlhMWIiLCJhaWQiOiI2NCIsInNjeSI6ImF1dG8iLCJuZXQiOiJ0Y3AiLCJ0eXBlIjoibm9uZSIsInRscyI6InRscyIsInNuaSI6InZtLmV4YW1wbGUuY29tIn0=";

    [Theory]
    [InlineData($"vless://{Uuid}@vpn.example.com:443?flow=xtls-rprx-vision&type=tcp&{RealityQuery}", CoreKind.Xray)]
    [InlineData($"vless://{Uuid}@vpn.example.com:443?type=xhttp&path=%2Fxh&{RealityQuery}&support-x25519mlkem768=true", CoreKind.Xray)]
    [InlineData("tuic://8d2e6f10-4b3a-4c5d-9e8f-0a1b2c3d4e5f:TuicPa55@203.0.113.20:443?sni=tuic.example.com", CoreKind.SingBox)]
    [InlineData("hysteria2://Hy2Pa55@hy.example.com:443?sni=hy.example.com", CoreKind.SingBox)]
    [InlineData($"vless://{Uuid}@cdn.example.net:443?type=ws&path=%2Fws&security=tls&sni=cdn.example.net&allowInsecure=1", CoreKind.SingBox)]
    [InlineData($"vless://{Uuid}@cdn.example.net:443?type=ws&path=%2Fws&security=tls&sni=cdn.example.net&allowInsecure=1&pcs=9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", CoreKind.Xray)]
    [InlineData("trojan://Fictional-Pa55@trojan.example.com:443?security=none", CoreKind.SingBox)]
    [InlineData("trojan://Fictional-Pa55@192.168.1.10:443?security=none", CoreKind.Xray)]
    [InlineData("ss://Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTpTc1BhNTV3MHJk@192.0.2.44:8388/?plugin=obfs-local%3Bobfs%3Dhttp", CoreKind.SingBox)]
    [InlineData(LegacyVmess, CoreKind.SingBox)]
    public void AutoPrefersXrayAndFallsBackToSingBox(string link, CoreKind expected)
    {
        var choice = CoreSelection.Select(Parse(link));

        Assert.Equal(new CoreChoice(expected, Automatic: true, Unsupported: null), choice);
    }

    [Fact]
    public void AutoWhenNoCoreFitsReportsTheDefaultCoresReason()
    {
        // HTTP-маскировка TCP есть только в Xray, а VLESS без TLS к публичному адресу Xray не запускает.
        var profile = Parse($"vless://{Uuid}@vpn.example.com:80?type=tcp&headerType=http&host=www.example.org&security=none");

        Assert.Equal(new CoreChoice(CoreKind.Xray, Automatic: true, Unsupported: "security"), CoreSelection.Select(profile));
    }

    [Theory]
    [InlineData(CorePreference.SingBox, $"vless://{Uuid}@vpn.example.com:443?type=tcp&{RealityQuery}", CoreKind.SingBox, null)]
    [InlineData(CorePreference.SingBox, $"vless://{Uuid}@vpn.example.com:443?type=xhttp&path=%2Fxh&{RealityQuery}", CoreKind.SingBox, "transport")]
    [InlineData(CorePreference.SingBox, $"vless://{Uuid}@vpn.example.com:443?type=tcp&{RealityQuery}&support-x25519mlkem768=true", CoreKind.SingBox, "security.supportsX25519MlKem768")]
    [InlineData(CorePreference.Xray, "tuic://8d2e6f10-4b3a-4c5d-9e8f-0a1b2c3d4e5f:TuicPa55@203.0.113.20:443?sni=tuic.example.com", CoreKind.Xray, "protocol")]
    [InlineData(CorePreference.Xray, LegacyVmess, CoreKind.Xray, "protocol.alterId")]
    public void ManualChoiceIsKeptAndExplainsIncompatibility(CorePreference preference, string link, CoreKind expected, string? unsupported)
    {
        var choice = CoreSelection.Select(Parse(link) with { Core = preference });

        Assert.Equal(new CoreChoice(expected, Automatic: false, unsupported), choice);
        Assert.Equal(unsupported is null, choice.IsSupported);
    }

    private static Profile Parse(string link)
    {
        var parsed = ShareLinkParser.Parse(link);
        Assert.True(parsed.IsSuccess, $"Ссылка не разбирается: {parsed.Error}");
        return parsed.Profile;
    }
}
