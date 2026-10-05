using System.Text.Json.Nodes;
using Khors.Core.Import;
using Khors.Core.Profiles;
using Xunit;

namespace Khors.Core.Tests.Import;

/// <summary>
/// Экспорт в ссылку — обратное преобразование к разбору: ссылка, собранная из профиля, разбирается в тот же профиль.
/// Проверяется на всех векторах ссылок и на профилях из конфигов подписок (YAML, JSON).
/// </summary>
public class ShareLinkExporterTests
{
    private static readonly string s_vectors = Path.Combine(AppContext.BaseDirectory, "Vectors");

    public static TheoryData<string> LinkVectors => new(
        Directory.EnumerateFiles(Path.Combine(s_vectors, "links"), "*.json")
            .Where(f => JsonNode.Parse(File.ReadAllText(f))!["expected"] is not null)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal));

    public static TheoryData<string, int> SubscriptionProfiles
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var file in Directory.EnumerateFiles(Path.Combine(s_vectors, "subscriptions"), "*.expected.json").Order(StringComparer.Ordinal))
            {
                var count = JsonNode.Parse(File.ReadAllText(file))!["profiles"]!.AsArray().Count;
                for (var i = 0; i < count; i++)
                {
                    data.Add(Path.GetFileName(file), i);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(LinkVectors))]
    public void LinkVectorSurvivesRoundTrip(string name)
    {
        var link = JsonNode.Parse(File.ReadAllText(Path.Combine(s_vectors, "links", name + ".json")))!["link"]!.GetValue<string>();
        var profile = ShareLinkParser.Parse(link).Profile!;

        AssertRoundTrip(profile);
    }

    [Theory]
    [MemberData(nameof(SubscriptionProfiles))]
    public void SubscriptionProfileSurvivesRoundTrip(string file, int index)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(s_vectors, "subscriptions", file)))!["profiles"]![index]!.AsObject();
        node["id"] = Guid.Empty.ToString();
        var profile = ProfileJson.Deserialize(node.ToJsonString());

        // В ссылке нет мультиплекса и путей из конфигов (smux.enabled и т. п.) — их не сравниваем.
        var comparable = profile with
        {
            Mux = null,
            UnknownParams = new(profile.UnknownParams.Where(p => !p.Key.Contains('.', StringComparison.Ordinal))),
        };
        AssertRoundTrip(comparable);
    }

    [Fact]
    public void VmessUsesV2rayNFormatAndFallsBackToXrayStandardForReality()
    {
        var plain = Parse("vmess://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@vm.example.com:443?type=ws&path=%2Fvm&security=tls&sni=vm.example.com#vm");
        var reality = Parse("vmess://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@vm.example.com:443?type=tcp&security=reality&sni=www.example.org&pbk=Zx8Q2mT9vK4rL7pW1sN6bY3cH5jD0fG8aE2uI9oP4kM#vm");

        Assert.DoesNotContain("@", ShareLinkExporter.Export(plain), StringComparison.Ordinal);
        Assert.StartsWith("vmess://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@", ShareLinkExporter.Export(reality), StringComparison.Ordinal);
    }

    [Fact]
    public void ShadowsocksFollowsSip002()
    {
        var aead = Parse("ss://Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTpTc1BhNTV3MHJk@192.0.2.44:8388#ss");
        var ss2022 = Parse("ss://2022-blake3-aes-128-gcm:Aq3%2BZt%2F9Kp1Lm0Xy7Wq2Bw%3D%3D@[2001:db8::20]:8388#2022");

        Assert.Equal("ss://Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTpTc1BhNTV3MHJk@192.0.2.44:8388#ss", ShareLinkExporter.Export(aead));
        Assert.Equal("ss://2022-blake3-aes-128-gcm:Aq3%2BZt%2F9Kp1Lm0Xy7Wq2Bw%3D%3D@[2001:db8::20]:8388#2022", ShareLinkExporter.Export(ss2022));
    }

    [Fact]
    public void ManualCoreChoiceIsNotExported()
    {
        var profile = Parse("trojan://Fictional-Pa55@vpn.example.com:443?security=tls&sni=vpn.example.com#tr") with { Core = CorePreference.SingBox };

        Assert.Equal(CorePreference.Auto, Parse(ShareLinkExporter.Export(profile)).Core);
    }

    private static void AssertRoundTrip(Profile profile)
    {
        var link = ShareLinkExporter.Export(profile);
        var parsed = ShareLinkParser.Parse(link);

        Assert.True(parsed.IsSuccess, $"Экспортированная ссылка не разбирается: {parsed.Error}");
        Assert.Equal(ProfileJson.Serialize(profile), ProfileJson.Serialize(parsed.Profile));
    }

    private static Profile Parse(string link)
    {
        var parsed = ShareLinkParser.Parse(link);
        Assert.True(parsed.IsSuccess, $"Ссылка не разбирается: {parsed.Error}");
        return parsed.Profile;
    }
}
