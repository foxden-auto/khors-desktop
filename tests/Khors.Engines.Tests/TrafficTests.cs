using System.Text;
using Khors.Core.Diagnostics;
using Khors.Core.Profiles;
using Khors.Engines.Traffic;
using Xunit;

namespace Khors.Engines.Tests;

/// <summary>Счётчики трафика: разбор ответов ядер и чтение у настоящих Xray и sing-box (только loopback).</summary>
public class TrafficTests
{
    private static readonly SecretMasker s_masker = new(Encoding.UTF8.GetBytes("traffic-test"));
    private static readonly CoreStartOptions s_withStats = new() { PreferredSocksPort = null, PreferredHttpPort = null, TrafficStats = true };

    [Fact]
    public void XrayVarsGiveProxyOutboundCounters()
    {
        const string json = """
            {"cmdline":["xray"],"memstats":{"Alloc":1},
             "stats":{"inbound":{},"outbound":{"proxy":{"downlink":4262,"uplink":89},"direct":{"downlink":7,"uplink":5}},"user":{}}}
            """;

        Assert.Equal(new TrafficCounters(89, 4262), TrafficReader.ParseXray(json));
    }

    [Fact]
    public void XrayWithoutTrafficYetGivesZeros() =>
        Assert.Equal(new TrafficCounters(0, 0), TrafficReader.ParseXray("""{"stats":{"inbound":{},"outbound":{},"user":{}}}"""));

    [Fact]
    public void SingBoxConnectionsGiveTotals() =>
        Assert.Equal(
            new TrafficCounters(79, 159),
            TrafficReader.ParseSingBox("""{"connections":[],"downloadTotal":159,"memory":4562944,"uploadTotal":79}"""));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"message":"Unauthorized"}""")]
    public void BrokenSingBoxAnswerIsNull(string json) => Assert.Null(TrafficReader.ParseSingBox(json));

    [Theory]
    [InlineData("")]
    [InlineData("[1]")]
    public void BrokenXrayAnswerIsNull(string json) => Assert.Null(TrafficReader.ParseXray(json));

    [Fact]
    public void EndpointDoesNotPrintSecret() =>
        Assert.DoesNotContain("s3cr3t", new TrafficEndpoint(CoreKind.SingBox, 1234, "s3cr3t").ToString(), StringComparison.Ordinal);

    [Fact]
    public async Task XrayReportsCounters()
    {
        Assert.SkipWhen(CoreLocator.Find(CoreKind.Xray) is null, "Xray не скачан: dotnet run tools/cores/fetch-cores.cs");
        var ct = TestContext.Current.CancellationToken;
        var profile = new Profile
        {
            Id = Guid.Empty,
            Name = "local",
            Server = new ServerEndpoint("127.0.0.1", 1),
            Protocol = new VlessSettings { Id = new Secret("3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b") },
        };

        await using var session = await CoreLauncher.StartAsync(CoreKind.Xray, profile, s_withStats, s_masker, guard: null, ct);

        Assert.NotNull(session.Traffic);
        Assert.NotNull(await session.ReadTrafficAsync(ct));
    }

    [Fact]
    public async Task SingBoxReportsCountersWithSecretOnly()
    {
        Assert.SkipWhen(CoreLocator.Find(CoreKind.SingBox) is null, "sing-box не скачан: dotnet run tools/cores/fetch-cores.cs");
        var ct = TestContext.Current.CancellationToken;
        var profile = new Profile
        {
            Id = Guid.Empty,
            Name = "local tuic",
            Server = new ServerEndpoint("127.0.0.1", 1),
            Protocol = new TuicSettings { Uuid = new Secret("8d2e6f10-4b3a-4c5d-9e8f-0a1b2c3d4e5f"), Password = new Secret("TuicPa55") },
            Security = new TlsSecurity { Sni = "tuic.example.com" },
        };
        await using var echo = new EchoServer();

        await using var session = await CoreLauncher.StartAsync(CoreKind.SingBox, profile, s_withStats, s_masker, guard: null, ct);
        await ProxyClient.EchoViaSocks5Async(session.SocksPort, echo.Port, "ping-traffic", ct);

        var endpoint = Assert.IsType<TrafficEndpoint>(session.Traffic);
        Assert.Equal(32, endpoint.Secret?.Length);
        var counters = await session.ReadTrafficAsync(ct);
        Assert.NotNull(counters);
        Assert.True(counters.Uplink >= "ping-traffic".Length, $"uplink {counters.Uplink}");

        // Без секрета Clash API не отвечает: управлять ядром может только KHORS.
        Assert.Null(await TrafficReader.ReadAsync(endpoint with { Secret = null }, ct));
    }
}
