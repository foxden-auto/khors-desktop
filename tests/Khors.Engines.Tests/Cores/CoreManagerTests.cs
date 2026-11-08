using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Khors.Engines.Cores;
using Khors.Engines.Geo;
using Khors.Ipc;
using Xunit;

namespace Khors.Engines.Tests.Cores;

/// <summary>Окно и служба: служба обновляется, только если её копия старее; несовместимое ядро в службу не отправляется.</summary>
public sealed class CoreManagerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("khors-cores-manager-").FullName;
    private readonly FakeServiceCores _service = new();
    private string? _incompatible;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CoreManager Manager(out CoreUpdater updater)
    {
        var archive = Zip();
        var web = new Dictionary<string, byte[]>
        {
            ["https://api.github.com/repos/XTLS/Xray-core/git/matching-refs/tags/v26."] = CoreReleasesTests.Tags("v26.10.31", "v26.11.1"),
            ["https://api.github.com/repos/XTLS/Xray-core/releases/tags/v26.11.1"] = Encoding.UTF8.GetBytes(
                CoreReleasesTests.Release("v26.11.1", "Xray-windows-64.zip", digest: "sha256:" + Convert.ToHexStringLower(SHA256.HashData(archive))).ToJsonString()),
            ["https://api.github.com/repos/SagerNet/sing-box/git/matching-refs/tags/v1.14."] = CoreReleasesTests.Tags("v1.14.2"),
            ["https://api.github.com/repos/SagerNet/sing-box/releases/tags/v1.14.2"] = Encoding.UTF8.GetBytes(
                CoreReleasesTests.Release("v1.14.2", "sing-box-1.14.2-windows-amd64.zip").ToJsonString()),
            ["https://github.com/XTLS/Xray-core/releases/download/v26.11.1/Xray-windows-64.zip"] = archive,
        };
        updater = new CoreUpdater(
            new CoreStore(_root),
            fetch: (url, _, _, _, _) => Task.FromResult(web.TryGetValue(url.ToString(), out var data) ? new GeoFetchResult(data, null) : new GeoFetchResult(null, GeoUpdateError.HttpError)),
            check: (_, _, _, _) => Task.FromResult(_incompatible));
        return new CoreManager(updater, _service);
    }

    private static byte[] Zip()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "xray.exe", "LICENSE" })
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(name));
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task UpdateGoesToWindowThenService()
    {
        var manager = Manager(out var updater);
        using (updater)
        {
            await manager.CheckAsync(TestContext.Current.CancellationToken);
            var xray = manager.Current.Single(s => s.Kind == CoreKind.Xray);
            Assert.Equal("26.11.1", xray.Available?.Version);
            Assert.Null(manager.Current.Single(s => s.Kind == CoreKind.SingBox).Available);

            var outcomes = await manager.UpdateAsync(TestContext.Current.CancellationToken);

            Assert.Equal([new CoreActionOutcome(CoreKind.Xray, "26.11.1", null, null, null, null)], outcomes);
            Assert.Equal("26.11.1", updater.Store.Status(CoreKind.Xray).Version);
            Assert.Equal([(IpcCore.Xray, "26.11.1")], _service.Updates);
            Assert.Null(manager.Current.Single(s => s.Kind == CoreKind.Xray).Available);
        }
    }

    [Fact]
    public async Task IncompatibleCoreIsNotSentToService()
    {
        var manager = Manager(out var updater);
        using (updater)
        {
            _incompatible = "bad config";
            await manager.CheckAsync(TestContext.Current.CancellationToken);

            var outcome = Assert.Single(await manager.UpdateAsync(TestContext.Current.CancellationToken));

            Assert.Equal(CoreUpdateError.Incompatible, outcome.LocalError);
            Assert.Empty(_service.Updates);
        }
    }

    [Fact]
    public async Task RollbackReachesServiceWhenItHasPreviousVersion()
    {
        var manager = Manager(out var updater);
        using (updater)
        {
            await manager.CheckAsync(TestContext.Current.CancellationToken);
            await manager.UpdateAsync(TestContext.Current.CancellationToken);

            await manager.RollbackAsync(CoreKind.Xray, TestContext.Current.CancellationToken);

            Assert.True(updater.Store.Status(CoreKind.Xray).Shipped);
            Assert.Equal([IpcCore.Xray], _service.Rollbacks);
        }
    }

    private sealed class FakeServiceCores : IServiceCoresClient
    {
        private string _xray = "26.10.31";
        private string? _previous;

        public List<(IpcCore, string)> Updates { get; } = [];

        public List<IpcCore> Rollbacks { get; } = [];

        public Task<CoresStatusResponse?> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult<CoresStatusResponse?>(Status());

        public Task<CoresStatusResponse?> UpdateAsync(CoreKind kind, string version, CancellationToken cancellationToken)
        {
            Updates.Add((kind == CoreKind.Xray ? IpcCore.Xray : IpcCore.SingBox, version));
            _previous = _xray;
            _xray = version;
            return Task.FromResult<CoresStatusResponse?>(Status());
        }

        public Task<CoresStatusResponse?> RollbackAsync(CoreKind kind, CancellationToken cancellationToken)
        {
            Rollbacks.Add(kind == CoreKind.Xray ? IpcCore.Xray : IpcCore.SingBox);
            _xray = _previous ?? _xray;
            _previous = null;
            return Task.FromResult<CoresStatusResponse?>(Status());
        }

        private CoresStatusResponse Status() =>
            new([new IpcCoreState(IpcCore.Xray, _xray, _xray == "26.10.31", _previous), new IpcCoreState(IpcCore.SingBox, "1.14.2", true)]);
    }
}
