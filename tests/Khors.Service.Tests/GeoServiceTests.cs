using Khors.Engines.Geo;
using Khors.Ipc;
using Khors.Service.Geo;
using Khors.Service.Ipc;
using Khors.Service.Tun;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Khors.Service.Tests;

/// <summary>Команды гео-баз службы: без сети (загрузка подменена), каталог — временный.</summary>
public sealed class GeoServiceTests : IDisposable
{
    private static readonly ServiceInfo s_info = new("1.2.3", DateTimeOffset.UnixEpoch);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "khors-service-geo-test-" + Guid.NewGuid().ToString("N"));
    private readonly GeoDatabaseUpdater _updater;
    private int _fetches;

    public GeoServiceTests()
    {
        _updater = new GeoDatabaseUpdater(_directory, fetch: (url, proxy, max, timeout, ct) =>
        {
            Interlocked.Increment(ref _fetches);
            Assert.Null(proxy);
            return Task.FromResult(new GeoFetchResult(null, GeoUpdateError.Network));
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _updater.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task<ServiceRequestHandler> GreetedAsync(ServiceGeo? geo)
    {
        var handler = new ServiceRequestHandler(s_info, new TunController(new FakeTunStarter(), NullLogger<TunController>.Instance), _ => { }, geo);
        await handler.HandleAsync(new HelloRequest(IpcProtocol.Version, "0.1.0"), Ct);
        return handler;
    }

    [Fact]
    public async Task StatusDoesNotDownload()
    {
        await using var handler = await GreetedAsync(new ServiceGeo(_updater));

        var status = Assert.IsType<GeoStatusResponse>(await handler.HandleAsync(new GetGeoStatusRequest(), Ct));

        Assert.Equal([new IpcGeoFile(IpcGeoKind.Site), new IpcGeoFile(IpcGeoKind.Ip)], status.Files);
        Assert.Equal(0, _fetches);
    }

    [Fact]
    public async Task UpdateReportsErrorUntilNextSuccess()
    {
        var geo = new ServiceGeo(_updater);
        await using var handler = await GreetedAsync(geo);

        var updated = Assert.IsType<GeoStatusResponse>(await handler.HandleAsync(new UpdateGeoRequest(), Ct));

        Assert.All(updated.Files, f => Assert.Equal("Network", f.Error));
        Assert.True(_fetches > 0);
        Assert.Equal(updated.Files, geo.Status().Files);
    }

    [Fact]
    public async Task WithoutGeoCommandsAreUnknown()
    {
        await using var handler = await GreetedAsync(null);

        Assert.Equal(new ErrorResponse(IpcErrorCode.UnknownCommand), await handler.HandleAsync(new UpdateGeoRequest(), Ct));
    }
}
