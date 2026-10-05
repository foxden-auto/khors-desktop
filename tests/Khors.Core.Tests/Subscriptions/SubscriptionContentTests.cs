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
