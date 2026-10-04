using Khors.Core.Profiles;
using Xunit;

namespace Khors.Core.Tests.Profiles;

/// <summary>Профиль и его части можно передать в лог: ToString() не раскрывает секреты и адреса.</summary>
public class ProfileSecrecyTests
{
    public static TheoryData<Profile> Profiles => TestProfiles.All;

    [Theory]
    [MemberData(nameof(Profiles))]
    public void ToStringOfProfileAndPartsDoesNotLeak(Profile profile)
    {
        string[] sensitive =
        [
            TestProfiles.Uuid, TestProfiles.RealityPublicKey, TestProfiles.ShortId, TestProfiles.Password,
            TestProfiles.Host, TestProfiles.Sni, "198.51.100.7", "2001:db8::10", "192.0.2.44",
            "example.net", "example.com", "value-1",
        ];
        object?[] parts = [profile, profile.Server, profile.Protocol, profile.Transport, profile.Security, profile.Mux, profile.UnknownParams];

        foreach (var part in parts)
        {
            var text = part?.ToString() ?? string.Empty;
            Assert.All(sensitive, s => Assert.DoesNotContain(s, text, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void SecretComparesByValueButPrintsMask()
    {
        var secret = new Secret(TestProfiles.Uuid);

        Assert.Equal(new Secret(TestProfiles.Uuid), secret);
        Assert.NotEqual(new Secret("other"), secret);
        Assert.Equal("***", secret.ToString());
        Assert.Contains("Id = ***", TestProfiles.VlessReality().Protocol.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EquatableArrayComparesByContent()
    {
        EquatableArray<string> first = new[] { "h2", "http/1.1" };
        EquatableArray<string> same = new[] { "h2", "http/1.1" };
        EquatableArray<string> reordered = new[] { "http/1.1", "h2" };

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, reordered);
        Assert.Equal(default, new EquatableArray<string>([]));
        Assert.Empty(default(EquatableArray<string>));
    }
}
