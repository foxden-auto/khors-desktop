using Khors.Core.Profiles;
using Khors.Engines.Auto;
using Khors.Engines.Latency;
using Xunit;

namespace Khors.Engines.Tests.Auto;

public class AutoPolicyTests
{
    private static Profile Profile(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Server = new ServerEndpoint($"{name}.example.com", 443),
        Protocol = new VlessSettings { Id = new Secret("3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b") },
        Security = new TlsSecurity { Sni = $"{name}.example.com" },
    };

    private static LatencyResult Ms(int ms) => LatencyResult.Success(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void RankOrdersRespondingProfilesByDelay()
    {
        Profile a = Profile("a"), b = Profile("b"), c = Profile("c"), d = Profile("d"), e = Profile("e");
        var results = new Dictionary<Guid, LatencyResult>
        {
            [a.Id] = Ms(200),
            [b.Id] = new(LatencyStatus.Timeout),
            [c.Id] = Ms(90),
            [e.Id] = Ms(200),
        };

        var ranked = AutoPolicy.Rank([a, b, c, d, e], results);

        Assert.Equal([c, a, e], ranked);
    }

    [Theory]
    [InlineData(100, 300, true)]
    [InlineData(80, 500, true)]
    [InlineData(100, 140, false)] // быстрее, но меньше чем в 1,5 раза
    [InlineData(400, 550, false)] // на 150 мс, но меньше чем в 1,5 раза
    [InlineData(50, 120, false)] // в 2,4 раза, но лишь на 70 мс
    [InlineData(100, 100, false)]
    public void NotablyFasterNeedsBothRatioAndGain(int candidate, int current, bool expected) =>
        Assert.Equal(expected, AutoPolicy.IsNotablyFaster(TimeSpan.FromMilliseconds(candidate), TimeSpan.FromMilliseconds(current)));

    [Fact]
    public void CandidatesAreProfilesThatSomeCoreCanRun()
    {
        Assert.True(AutoPolicy.IsCandidate(Profile("ok")));
        Assert.False(AutoPolicy.IsCandidate(Profile("bad") with { Server = new ServerEndpoint("bad.example.com", 0) }));

        // XHTTP запускает только Xray — кандидат; XHTTP с allowInsecure не запустит ни одно ядро
        // (Xray 26 убрал allowInsecure, в sing-box нет XHTTP).
        Assert.True(AutoPolicy.IsCandidate(Profile("xhttp") with { Transport = new XhttpTransport() }));
        Assert.False(AutoPolicy.IsCandidate(Profile("none") with
        {
            Transport = new XhttpTransport(),
            Security = new TlsSecurity { Sni = "none.example.com", AllowInsecure = true },
        }));
    }
}
