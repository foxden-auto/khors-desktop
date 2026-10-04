using System.Text;
using System.Text.Json.Nodes;
using Khors.Core.Diagnostics;
using Khors.Core.Import;
using Khors.Core.Profiles;
using Xunit;

namespace Khors.Core.Tests.Import;

/// <summary>
/// Тестовые векторы ссылок: <c>Vectors/links/*.json</c> с полями <c>link</c> и <c>expected</c> (профиль
/// в формате <see cref="ProfileJson"/> без <c>id</c>) или <c>error</c> (<c>code</c>, <c>field</c>).
/// Все данные вымышленные.
/// </summary>
public class ShareLinkVectorTests
{
    private static readonly string s_directory = Path.Combine(AppContext.BaseDirectory, "Vectors", "links");
    private static readonly string[] s_schemes = ["vless://", "vmess://", "trojan://", "ss://"];

    public static TheoryData<string> AllVectors => new(VectorNames(_ => true));

    public static TheoryData<string> SuccessVectors => new(VectorNames(v => v["expected"] is not null));

    [Theory]
    [MemberData(nameof(AllVectors))]
    public void VectorParsesAsExpected(string name)
    {
        var vector = Load(name);
        var result = ShareLinkParser.Parse(vector["link"]!.GetValue<string>());

        if (vector["expected"] is JsonObject expected)
        {
            Assert.True(result.IsSuccess, $"Ожидался профиль, получена ошибка {result.Error}");
            expected["id"] = Guid.Empty.ToString();
            var expectedProfile = ProfileJson.Deserialize(expected.ToJsonString());

            Assert.Equal(ProfileJson.Serialize(expectedProfile), ProfileJson.Serialize(result.Profile));
            Assert.Equal(expectedProfile, result.Profile);
        }
        else
        {
            var error = vector["error"]!;
            Assert.False(result.IsSuccess, "Ожидалась ошибка разбора");
            Assert.Equal(Enum.Parse<LinkParseErrorCode>(error["code"]!.GetValue<string>()), result.Error.Code);
            Assert.Equal(error["field"]?.GetValue<string>(), result.Error.Field);
        }
    }

    /// <summary>Маскировщик убирает из ссылки все секреты, и замаскированная ссылка остаётся разбираемой.</summary>
    [Theory]
    [MemberData(nameof(SuccessVectors))]
    public void MaskedLinkContainsNoSecrets(string name)
    {
        var link = Load(name)["link"]!.GetValue<string>();
        var profile = ShareLinkParser.Parse(link).Profile!;
        var secrets = ProfileSecrets.Of(profile);

        var masked = new SecretMasker(Encoding.UTF8.GetBytes("vector-test")).MaskText(link);

        Assert.All(secrets, s =>
        {
            Assert.DoesNotContain(s, masked, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Uri.EscapeDataString(s), masked, StringComparison.OrdinalIgnoreCase);
        });

        var reparsed = ShareLinkParser.Parse(masked);
        Assert.True(reparsed.IsSuccess, $"Замаскированная ссылка не разбирается: {reparsed.Error}");
        var reparsedJson = ProfileJson.Serialize(reparsed.Profile);
        Assert.All(secrets, s => Assert.DoesNotContain(s, reparsedJson, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(SuccessVectors))]
    public void ParsedProfileHasNoValidationErrors(string name)
    {
        var profile = ShareLinkParser.Parse(Load(name)["link"]!.GetValue<string>()).Profile!;

        Assert.DoesNotContain(ProfileValidator.Validate(profile), i => i.Severity == ProfileIssueSeverity.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyLinkIsRejected(string link) =>
        Assert.Equal(LinkParseErrorCode.Empty, ShareLinkParser.Parse(link).Error?.Code);

    [Fact]
    public void EverySupportedSchemeHasVectors()
    {
        var links = VectorNames(_ => true).Select(n => Load(n)["link"]!.GetValue<string>()).ToList();

        Assert.All(s_schemes, scheme =>
            Assert.Contains(links, l => l.StartsWith(scheme, StringComparison.Ordinal)));
    }

    private static IEnumerable<string> VectorNames(Func<JsonObject, bool> filter) =>
        Directory.EnumerateFiles(s_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(n => filter(Load(n)))
            .Order(StringComparer.Ordinal);

    private static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(s_directory, name + ".json")))!.AsObject();
}
