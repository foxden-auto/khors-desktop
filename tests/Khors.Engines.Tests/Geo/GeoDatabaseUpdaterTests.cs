using System.Net;
using System.Security.Cryptography;
using System.Text;
using Khors.Engines.Geo;
using Khors.Engines.Tests.Storage;
using Xunit;

namespace Khors.Engines.Tests.Geo;

/// <summary>Загрузка гео-баз на подменной «сети»: файлы маленькие и вымышленные, но в настоящем формате.</summary>
public sealed class GeoDatabaseUpdaterTests : IDisposable
{
    private static readonly byte[] s_site = GeoFiles.Site(("CATEGORY-RU", "example.ru"), ("PRIVATE", "localhost"));
    private static readonly byte[] s_siteNew = GeoFiles.Site(("CATEGORY-RU", "example.ru"), ("CATEGORY-RU", "example.su"), ("PRIVATE", "localhost"));
    private static readonly byte[] s_ip = GeoFiles.Ip(("RU", [203, 0, 113, 0]), ("PRIVATE", [10, 0, 0, 0]));

    private readonly TempDirectory _directory = new();
    private readonly Dictionary<string, Func<IWebProxy?, GeoFetchResult>> _responses = [];
    private readonly List<(Uri Url, bool ViaProxy)> _requests = [];
    private int? _localPort;

    public void Dispose() => _directory.Dispose();

    private GeoDatabaseUpdater Create() => new(_directory.Path, () => _localPort, (url, proxy, max, timeout, ct) =>
    {
        _requests.Add((url, proxy is not null));
        return Task.FromResult(_responses.TryGetValue(url.AbsoluteUri, out var respond) ? respond(proxy) : new GeoFetchResult(null, GeoUpdateError.HttpError));
    });

    private void Serve(GeoSource source, int mirror, byte[] data, byte[]? checksumOf = null)
    {
        var m = source.Mirrors[mirror];
        _responses[m.Data.AbsoluteUri] = _ => new GeoFetchResult(data, null);
        _responses[m.Checksum.AbsoluteUri] = _ => new GeoFetchResult(Checksum(checksumOf ?? data, source.Mirrors[0].Data.Segments[^1]), null);
    }

    private static byte[] Checksum(byte[] data, string name) => Encoding.ASCII.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(data))}  {name}\n");

    private string PathOf(GeoSource source) => _directory.File(source.FileName);

    [Fact]
    public async Task DownloadsVerifiesAndInstallsBothFiles()
    {
        Serve(GeoSources.Site, 0, s_site);
        Serve(GeoSources.Ip, 0, s_ip);
        using var updater = Create();

        var outcomes = await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal([new GeoUpdateOutcome(GeoDatabaseKind.Site, GeoUpdateResult.Updated), new GeoUpdateOutcome(GeoDatabaseKind.Ip, GeoUpdateResult.Updated)], outcomes);
        Assert.Equal(s_site, File.ReadAllBytes(PathOf(GeoSources.Site)));
        Assert.Equal(s_ip, File.ReadAllBytes(PathOf(GeoSources.Ip)));
        Assert.All(updater.GetStatus(), s => Assert.True(s.Present));
        Assert.Equal(["geoip.dat", "geosite.dat"], Directory.GetFiles(_directory.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task SameChecksumDownloadsOnlyChecksum()
    {
        Serve(GeoSources.Site, 0, s_site);
        Serve(GeoSources.Ip, 0, s_ip);
        using var updater = Create();
        await updater.UpdateAsync(TestContext.Current.CancellationToken);
        _requests.Clear();

        var outcomes = await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.All(outcomes, o => Assert.Equal(GeoUpdateResult.UpToDate, o.Result));
        Assert.All(_requests, r => Assert.EndsWith(".sha256sum", r.Url.AbsoluteUri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChecksumMismatchKeepsInstalledFileAndTriesMirror()
    {
        Serve(GeoSources.Site, 0, s_site);
        Serve(GeoSources.Ip, 0, s_ip);
        using var updater = Create();
        await updater.UpdateAsync(TestContext.Current.CancellationToken);

        // Основной источник отдаёт файл, не совпадающий с суммой; зеркало — верный новый файл.
        Serve(GeoSources.Site, 0, s_siteNew, checksumOf: s_site.Concat(new byte[] { 1 }).ToArray());
        Serve(GeoSources.Site, 1, s_siteNew);

        var site = (await updater.UpdateAsync(TestContext.Current.CancellationToken))[0];

        Assert.Equal(new GeoUpdateOutcome(GeoDatabaseKind.Site, GeoUpdateResult.Updated), site);
        Assert.Equal(s_siteNew, File.ReadAllBytes(PathOf(GeoSources.Site)));
    }

    [Theory]
    [InlineData("html")]
    [InlineData("no-required-category")]
    public async Task InvalidFileIsNotInstalled(string kind)
    {
        Serve(GeoSources.Site, 0, s_site);
        Serve(GeoSources.Ip, 0, s_ip);
        using var updater = Create();
        await updater.UpdateAsync(TestContext.Current.CancellationToken);

        var bad = kind == "html" ? Encoding.ASCII.GetBytes("<html>rate limited</html>") : GeoFiles.Ip(("US", [198, 51, 100, 0]));
        Serve(GeoSources.Ip, 0, bad);
        Serve(GeoSources.Ip, 1, bad);

        var ip = (await updater.UpdateAsync(TestContext.Current.CancellationToken))[1];

        Assert.Equal(new GeoUpdateOutcome(GeoDatabaseKind.Ip, GeoUpdateResult.Failed, GeoUpdateError.Invalid), ip);
        Assert.Equal(s_ip, File.ReadAllBytes(PathOf(GeoSources.Ip)));
    }

    [Fact]
    public async Task NetworkErrorRetriesThroughLocalProxy()
    {
        _localPort = 20809;
        foreach (var source in GeoSources.All)
        {
            var data = source.Kind == GeoDatabaseKind.Site ? s_site : s_ip;
            foreach (var mirror in source.Mirrors)
            {
                _responses[mirror.Checksum.AbsoluteUri] = proxy => proxy is null ? new GeoFetchResult(null, GeoUpdateError.Network) : new GeoFetchResult(Checksum(data, "x.dat"), null);
                _responses[mirror.Data.AbsoluteUri] = proxy => proxy is null ? new GeoFetchResult(null, GeoUpdateError.Network) : new GeoFetchResult(data, null);
            }
        }

        using var updater = Create();

        var outcomes = await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.All(outcomes, o => Assert.Equal((GeoUpdateResult.Updated, true), (o.Result, o.ViaProxy)));
    }

    [Fact]
    public async Task NoNetworkReportsFailureWithoutFiles()
    {
        using var updater = Create();

        var outcomes = await updater.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.All(outcomes, o => Assert.Equal((GeoUpdateResult.Failed, GeoUpdateError.HttpError), (o.Result, o.Error)));
        Assert.All(updater.GetStatus(), s => Assert.False(s.Present));
        Assert.Equal(4, _requests.Count);
    }

    [Theory]
    [InlineData("4ff4b2ec242a9e87b296ba27ddb85f8311e8d3e071eaebac5be9231c60e408dd  geoip.dat\n", true)]
    [InlineData("4FF4B2EC242A9E87B296BA27DDB85F8311E8D3E071EAEBAC5BE9231C60E408DD", true)]
    [InlineData("4ff4b2ec", false)]
    [InlineData("<html>404</html>", false)]
    public void ParsesChecksumFile(string text, bool valid)
    {
        Assert.Equal(valid, GeoDatabaseUpdater.ParseChecksum(Encoding.ASCII.GetBytes(text)) is not null);
    }
}

/// <summary>Маленькие гео-базы в формате protobuf v2fly.</summary>
internal static class GeoFiles
{
    public static byte[] Site(params (string Code, string Domain)[] rules) =>
        [.. rules.GroupBy(r => r.Code).SelectMany(g => Field(1, [.. Field(1, Encoding.UTF8.GetBytes(g.Key)), .. g.SelectMany(r => Field(2, [.. Varint(1, 2), .. Field(2, Encoding.UTF8.GetBytes(r.Domain))]))]))];

    public static byte[] Ip(params (string Code, byte[] Network)[] entries) =>
        [.. entries.SelectMany(e => Field(1, [.. Field(1, Encoding.UTF8.GetBytes(e.Code)), .. Field(2, [.. Field(1, e.Network), .. Varint(2, 24)])]))];

    private static byte[] Field(int number, byte[] payload) => [.. VarintBytes((ulong)(number << 3 | 2)), .. VarintBytes((ulong)payload.Length), .. payload];

    private static byte[] Varint(int number, ulong value) => [.. VarintBytes((ulong)(number << 3)), .. VarintBytes(value)];

    private static byte[] VarintBytes(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value == 0 ? b : (byte)(b | 0x80));
        }
        while (value != 0);

        return [.. bytes];
    }
}
