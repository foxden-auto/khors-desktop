using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Core.Subscriptions;
using Xunit;

namespace Khors.Core.Tests.Subscriptions;

/// <summary>Векторы Vectors/subscriptions: 4 вымышленные ссылки + повтор первой + неизвестная схема.</summary>
public class SubscriptionContentTests
{
    private static string Vector(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "subscriptions", name));

    [Theory]
    [InlineData("plain-list.txt", SubscriptionFormat.LinkList)]
    [InlineData("base64-list.txt", SubscriptionFormat.Base64LinkList)]
    public void LinkListsAreParsedWithoutDuplicates(string vector, SubscriptionFormat format)
    {
        var result = SubscriptionContent.Parse(Vector(vector));

        Assert.Equal(format, result.Format);
        Assert.Equal(["VlessSettings", "TrojanSettings", "VmessSettings", "ShadowsocksSettings"], result.Profiles.Select(p => p.Protocol.GetType().Name));
        Assert.All(result.Profiles, p => Assert.Equal(Guid.Empty, p.Id));
        Assert.Equal([new ImportLineError(6, new LinkParseError(LinkParseErrorCode.UnsupportedScheme, "scheme"))], result.Errors);
    }

    /// <summary>Векторы конфигов: рядом с файлом — *.expected.json с ожидаемыми профилями (без id) и ошибками.</summary>
    [Theory]
    [InlineData("clash.yaml", "clash.expected.json", SubscriptionFormat.ClashYaml)]
    [InlineData("singbox.json", "singbox.expected.json", SubscriptionFormat.SingBoxJson)]
    [InlineData("xray.json", "xray.expected.json", SubscriptionFormat.XrayJson)]
    public void ConfigFormatsAreParsedIntoProfiles(string vector, string expectedVector, SubscriptionFormat format)
    {
        var expected = System.Text.Json.Nodes.JsonNode.Parse(Vector(expectedVector))!;

        var result = SubscriptionContent.Parse(Vector(vector));

        Assert.Equal(format, result.Format);
        var expectedProfiles = expected["profiles"]!.AsArray().Select(node =>
        {
            node!["id"] = Guid.Empty.ToString();
            return ProfileJson.Deserialize(node.ToJsonString());
        }).ToList();
        Assert.Equal(expectedProfiles.Select(ProfileJson.Serialize), result.Profiles.Select(ProfileJson.Serialize));
        Assert.Equal(expectedProfiles, result.Profiles);
        Assert.Equal(
            expected["errors"]!.AsArray().Select(e => new ImportLineError(
                e!["line"]!.GetValue<int>(),
                new LinkParseError(Enum.Parse<LinkParseErrorCode>(e["code"]!.GetValue<string>()), e["field"]?.GetValue<string>()))),
            result.Errors);
    }

    [Theory]
    [InlineData("""{ "outbounds": [ { "type": "direct", "tag": "direct" } ] }""")]
    [InlineData("""{ "inbounds": [] }""")]
    [InlineData("""[1, 2, 3]""")]
    [InlineData("""{ broken json""")]
    public void JsonWithoutProxiesIsNotRecognized(string json) =>
        Assert.Empty(SubscriptionContent.Parse(json).Profiles);

    [Fact]
    public void YamlWithoutProxiesIsNotClash() =>
        Assert.Equal(SubscriptionFormat.Unknown, SubscriptionContent.Parse("proxies-count: 3\nrules: []\n").Format);

    [Fact]
    public void BrokenYamlIsNotClash() =>
        Assert.Equal(SubscriptionFormat.Unknown, SubscriptionContent.Parse("proxies: [unclosed").Format);

    [Theory]
    [InlineData("html-page.txt")]
    [InlineData("")]
    public void UnknownContentHasNoProfiles(string vector)
    {
        var result = SubscriptionContent.Parse(vector.Length == 0 ? "" : Vector(vector));

        Assert.Equal(SubscriptionFormat.Unknown, result.Format);
        Assert.Empty(result.Profiles);
    }

    [Fact]
    public void PastedBase64SubscriptionIsImportedAsLinks()
    {
        var result = ProfileImporter.Import(Vector("base64-list.txt"), [], Guid.NewGuid, DateTimeOffset.UnixEpoch);

        Assert.Equal(4, result.Added.Count);
        Assert.Equal(1, result.Duplicates);
        Assert.Single(result.Errors);
    }
}
