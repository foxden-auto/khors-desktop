using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Core.Tests.Profiles;
using Xunit;

namespace Khors.Core.Tests.Import;

public class ProfileImporterTests
{
    private const string Vless = $"vless://{TestProfiles.Uuid}@vpn.example.com:443?security=reality&sni=www.example.org&pbk={TestProfiles.RealityPublicKey}&sid={TestProfiles.ShortId}#Один";
    private const string Trojan = "trojan://Fictional-Pa55@trojan.example.com:443#Два";

    private static readonly DateTimeOffset s_now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static Func<Guid> Sequence()
    {
        var n = 0;
        return () => new Guid(++n, 0, 0, new byte[8]);
    }

    [Fact]
    public void ImportsSeveralLinksSeparatedByNewlinesAndSpaces()
    {
        var result = ProfileImporter.Import($"  {Vless}\r\n\r\n{Trojan}  ", [], Sequence(), s_now);

        Assert.Equal(2, result.Added.Count);
        Assert.Equal(["Один", "Два"], result.Added.Select(p => p.Name));
        Assert.Equal([new Guid(1, 0, 0, new byte[8]), new Guid(2, 0, 0, new byte[8])], result.Added.Select(p => p.Id));
        Assert.All(result.Added, p => Assert.Equal(s_now, p.UpdatedAt));
        Assert.Equal(0, result.Duplicates);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SameConnectionUnderAnotherNameIsDuplicate()
    {
        var renamed = Vless.Replace("#Один", "#Другое имя", StringComparison.Ordinal);

        var result = ProfileImporter.Import($"{Vless}\n{renamed}\n{Vless}", [], Sequence(), s_now);

        Assert.Single(result.Added);
        Assert.Equal(2, result.Duplicates);
    }

    [Fact]
    public void ExistingProfileIsNotImportedAgain()
    {
        var existing = ProfileImporter.Import(Trojan, [], Sequence(), s_now).Added;

        var result = ProfileImporter.Import($"{Trojan}\n{Vless}", existing, Sequence(), s_now);

        Assert.Equal(["Один"], result.Added.Select(p => p.Name));
        Assert.Equal(1, result.Duplicates);
    }

    [Fact]
    public void BrokenLinksAreReportedByPositionWithoutTheirText()
    {
        const string secret = "Very-Secret-Pa55";
        var result = ProfileImporter.Import($"{Vless}\ntrojan://@host.example.com:443\nhysteria9://{secret}@x:1\n{Trojan}", [], Sequence(), s_now);

        Assert.Equal(2, result.Added.Count);
        Assert.Equal(
            [new ImportLineError(2, new LinkParseError(LinkParseErrorCode.MissingCredentials, "userinfo")), new ImportLineError(3, new LinkParseError(LinkParseErrorCode.UnsupportedScheme, "scheme"))],
            result.Errors);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(TestProfiles.Uuid, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RawSpacesInNameStayInName()
    {
        var text = $"{Trojan.Replace("#Два", "#Germany 1 fast", StringComparison.Ordinal)} {Vless}\nhysteria9://x@y:1 tail";

        var result = ProfileImporter.Import(text, [], Sequence(), s_now);

        Assert.Equal(["Germany 1 fast", "Один"], result.Added.Select(p => p.Name));
        Assert.Equal([3], result.Errors.Select(e => e.Line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void EmptyTextImportsNothing(string text)
    {
        var result = ProfileImporter.Import(text, [], Sequence(), s_now);

        Assert.Empty(result.Added);
        Assert.Empty(result.Errors);
    }
}
