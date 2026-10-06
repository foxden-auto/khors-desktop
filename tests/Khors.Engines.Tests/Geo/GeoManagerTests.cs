using System.Security.Cryptography;
using System.Text;
using Khors.Engines.Geo;
using Khors.Engines.Storage;
using Khors.Engines.Tests.Storage;
using Khors.Ipc;
using Xunit;

namespace Khors.Engines.Tests.Geo;

public sealed class GeoManagerTests : IDisposable
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] s_site = GeoFiles.Site(("CATEGORY-RU", "example.ru"), ("PRIVATE", "localhost"));
    private static readonly byte[] s_ip = GeoFiles.Ip(("RU", [203, 0, 113, 0]), ("PRIVATE", [10, 0, 0, 0]));

    private readonly TempDirectory _directory = new();
    private readonly FixedTimeProvider _time = new(s_now);
    private readonly FakeServiceGeo _service = new();
    private readonly SettingsStore _settings;
    private bool _networkUp = true;

    public GeoManagerTests() => _settings = SettingsStore.Open(_directory.File("settings.json"), _time);

    public void Dispose() => _directory.Dispose();

    private GeoManager Create(GeoDatabaseUpdater updater) => new(updater, _service, _settings, _time);

    private GeoDatabaseUpdater Updater() => new(Path.Combine(_directory.Path, "geo"), fetch: (url, proxy, max, timeout, ct) =>
    {
        if (!_networkUp)
        {
            return Task.FromResult(new GeoFetchResult(null, GeoUpdateError.Network));
        }

        var data = url.AbsoluteUri.Contains("geoip", StringComparison.Ordinal) ? s_ip : s_site;
        return Task.FromResult(url.AbsoluteUri.EndsWith(".sha256sum", StringComparison.Ordinal)
            ? new GeoFetchResult(Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(data))), null)
            : new GeoFetchResult(data, null));
    });

    [Fact]
    public async Task SuccessfulUpdateRemembersCheckAndUpdatesService()
    {
        using var updater = Updater();
        await using var manager = Create(updater);
        Assert.True(manager.IsDue());

        Assert.True(await manager.UpdateNowAsync(TestContext.Current.CancellationToken));

        Assert.Equal(s_now, _settings.Current.GeoLastCheck);
        Assert.Equal(1, _service.Updates);
        Assert.All(manager.Current.Local, f => Assert.True(f.Present));
        Assert.Empty(manager.Current.LocalErrors);
        Assert.Equal(FakeServiceGeo.Files, manager.Current.Service);
        Assert.False(manager.IsDue());
    }

    [Fact]
    public async Task FailureIsRetriedAfterAnHourNotEveryCheck()
    {
        _networkUp = false;
        using var updater = Updater();
        await using var manager = Create(updater);

        Assert.False(await manager.UpdateNowAsync(TestContext.Current.CancellationToken));

        Assert.Null(_settings.Current.GeoLastCheck);
        Assert.Equal(GeoUpdateError.Network, manager.Current.LocalErrors[GeoDatabaseKind.Ip]);
        _time.Now = s_now.AddMinutes(30);
        Assert.False(manager.IsDue());
        _time.Now = s_now.AddHours(1);
        Assert.True(manager.IsDue());
    }

    [Theory]
    [InlineData(23, true, false)]
    [InlineData(24, true, true)]
    [InlineData(48, false, false)]
    [InlineData(-1, true, true)]
    public void DueOncePerDayWhenEnabled(int hoursSinceCheck, bool autoUpdate, bool due)
    {
        _settings.Update(s => s with { GeoAutoUpdate = autoUpdate, GeoLastCheck = s_now.AddHours(-hoursSinceCheck) });
        using var updater = Updater();
        var manager = Create(updater);

        Assert.Equal(due, manager.IsDue());
    }

    private sealed class FakeServiceGeo : IServiceGeoClient
    {
        public static readonly IReadOnlyList<IpcGeoFile> Files = [new(IpcGeoKind.Site, 10, s_now), new(IpcGeoKind.Ip, 20, s_now)];

        public int Updates { get; private set; }

        public Task<GeoStatusResponse?> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult<GeoStatusResponse?>(new GeoStatusResponse(Files));

        public Task<GeoStatusResponse?> UpdateAsync(CancellationToken cancellationToken)
        {
            Updates++;
            return Task.FromResult<GeoStatusResponse?>(new GeoStatusResponse(Files));
        }
    }
}
