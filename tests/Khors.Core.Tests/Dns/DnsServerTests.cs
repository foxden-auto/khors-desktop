using Khors.Core.Dns;
using Khors.Core.Storage;
using Xunit;

namespace Khors.Core.Tests.Dns;

public class DnsServerTests
{
    [Theory]
    [InlineData("https://1.1.1.1/dns-query", DnsServerType.Https, "1.1.1.1", null, null, "https://1.1.1.1/dns-query")]
    [InlineData("https://1.1.1.1", DnsServerType.Https, "1.1.1.1", null, null, "https://1.1.1.1/dns-query")]
    [InlineData("HTTPS://Dns.Example.COM:8443/resolve", DnsServerType.Https, "dns.example.com", 8443, "/resolve", "https://dns.example.com:8443/resolve")]
    [InlineData("https://dns.example.com:443/dns-query", DnsServerType.Https, "dns.example.com", null, null, "https://dns.example.com/dns-query")]
    [InlineData("tls://9.9.9.9", DnsServerType.Tls, "9.9.9.9", null, null, "tls://9.9.9.9")]
    [InlineData("tls://9.9.9.9:853", DnsServerType.Tls, "9.9.9.9", null, null, "tls://9.9.9.9")]
    [InlineData("tls://dns.example.com:8853/", DnsServerType.Tls, "dns.example.com", 8853, null, "tls://dns.example.com:8853")]
    [InlineData("udp://192.168.1.1", DnsServerType.Udp, "192.168.1.1", null, null, "udp://192.168.1.1")]
    [InlineData("udp://192.168.1.1:5353", DnsServerType.Udp, "192.168.1.1", 5353, null, "udp://192.168.1.1:5353")]
    [InlineData("tcp://8.8.8.8:53", DnsServerType.Tcp, "8.8.8.8", null, null, "tcp://8.8.8.8")]
    [InlineData("  8.8.8.8  ", DnsServerType.Udp, "8.8.8.8", null, null, "udp://8.8.8.8")]
    [InlineData("2606:4700:4700::1111", DnsServerType.Udp, "2606:4700:4700::1111", null, null, "udp://[2606:4700:4700::1111]")]
    [InlineData("https://[2606:4700:4700::1111]/dns-query", DnsServerType.Https, "2606:4700:4700::1111", null, null, "https://[2606:4700:4700::1111]/dns-query")]
    public void ParsesSupportedForms(string text, DnsServerType type, string host, int? port, string? path, string canonical)
    {
        var result = DnsServer.Parse(text);

        Assert.True(result.IsSuccess, $"Ошибка разбора: {result.Error}");
        Assert.Equal(type, result.Server.Type);
        Assert.Equal(host, result.Server.Host);
        Assert.Equal(port, result.Server.Port);
        Assert.Equal(path, result.Server.Path);
        Assert.Equal(canonical, result.Server.ToString());
        Assert.Equal(result.Server, DnsServer.Parse(canonical).Server);
    }

    [Theory]
    [InlineData(null, DnsServerParseError.Empty)]
    [InlineData("", DnsServerParseError.Empty)]
    [InlineData("   ", DnsServerParseError.Empty)]
    [InlineData("quic://dns.adguard-dns.com", DnsServerParseError.UnsupportedScheme)]
    [InlineData("h3://1.1.1.1/dns-query", DnsServerParseError.UnsupportedScheme)]
    [InlineData("sdns://AgcAAAAAAAAABzEuMC4wLjE", DnsServerParseError.UnsupportedScheme)]
    [InlineData("http://1.1.1.1/dns-query", DnsServerParseError.UnsupportedScheme)]
    [InlineData("dns.google", DnsServerParseError.Malformed)]
    [InlineData("1.1.1", DnsServerParseError.Malformed)]
    [InlineData("8.8.8.8:53", DnsServerParseError.Malformed)]
    [InlineData("tls://9.9.9.9/dns-query", DnsServerParseError.Malformed)]
    [InlineData("https://1.1.1.1/dns-query?dns=x", DnsServerParseError.Malformed)]
    [InlineData("https://user:pass@1.1.1.1/dns-query", DnsServerParseError.Malformed)]
    [InlineData("https://1.1.1.1/dns-query#frag", DnsServerParseError.Malformed)]
    [InlineData("https://1.1.1.1/dns query", DnsServerParseError.Malformed)]
    [InlineData("https://1.1.1.1/dns-query\", \"detour\": \"direct", DnsServerParseError.Malformed)]
    [InlineData("https://localhost/dns-query", DnsServerParseError.InvalidHost)]
    [InlineData("udp://1.1.1.1:0", DnsServerParseError.InvalidPort)]
    public void RejectsInvalidText(string? text, DnsServerParseError error)
    {
        var result = DnsServer.Parse(text);

        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
    }

    [Theory]
    [InlineData("udp://192.168.1.1", true)]
    [InlineData("udp://10.0.0.1", true)]
    [InlineData("udp://172.20.0.1", true)]
    [InlineData("udp://127.0.0.1:5353", true)]
    [InlineData("udp://[fd00::1]", true)]
    [InlineData("udp://172.32.0.1", false)]
    [InlineData("https://1.1.1.1/dns-query", false)]
    [InlineData("https://dns.example.com/dns-query", false)]
    public void DetectsLocalNetwork(string text, bool local)
    {
        Assert.Equal(local, DnsServer.Parse(text).Server!.IsLocalNetwork);
    }

    [Fact]
    public void PresetsAreDistinctAndUseAddresses()
    {
        Assert.Equal(DnsPresets.All.Count, DnsPresets.All.Select(p => p.Id).Distinct().Count());
        Assert.Equal(DnsPresets.All.Count, DnsPresets.All.Select(p => p.Server).Distinct().Count());
        Assert.All(DnsPresets.All, p => Assert.True(p.Server.HostIsAddress, p.Id));
        Assert.All(DnsPresets.All, p => Assert.False(p.Server.IsLocalNetwork, p.Id));
        Assert.Equal(AppSettings.DefaultRemoteDns, DnsPresets.Default.Server.ToString());
    }

    [Theory]
    [InlineData("tls://8.8.8.8", "google-dot")]
    [InlineData("https://8.8.8.8", "google-doh")]
    [InlineData("tls://8.8.4.4", null)]
    public void FindsPresetByServer(string text, string? id)
    {
        Assert.Equal(id, DnsPresets.Find(DnsServer.Parse(text).Server!)?.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не адрес")]
    public void BrokenSettingFallsBackToDefault(string? text)
    {
        Assert.Equal(DnsPresets.Default.Server, DnsPresets.ServerOrDefault(text));
    }
}
