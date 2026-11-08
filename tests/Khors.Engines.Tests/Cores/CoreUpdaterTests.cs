using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Khors.Engines.Cores;
using Khors.Engines.Geo;
using Xunit;

namespace Khors.Engines.Tests.Cores;

/// <summary>Хранилище версий и обновление на подменной сети; настоящие ядра — только в проверке совместимости.</summary>
public sealed class CoreUpdaterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("khors-cores-").FullName;
    private readonly Dictionary<string, byte[]> _web = [];
    private readonly List<string> _requests = [];
    private string? _incompatible;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CoreUpdater Updater() => new(
        new CoreStore(_root),
        fetch: (url, _, _, _, _) =>
        {
            _requests.Add(url.ToString());
            return Task.FromResult(_web.TryGetValue(url.ToString(), out var data)
                ? new GeoFetchResult(data, null)
                : new GeoFetchResult(null, GeoUpdateError.HttpError));
        },
        check: (_, executable, _, _) => Task.FromResult(File.Exists(executable) ? _incompatible : "missing"));

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Публикует релиз Xray: архив на github.com и ответ API по тегу.</summary>
    private CoreRelease PublishXray(string version, byte[]? archive = null, byte[]? digestOf = null)
    {
        archive ??= Zip(("xray.exe", "xray " + version), ("LICENSE", "MPL"));
        var url = $"https://github.com/XTLS/Xray-core/releases/download/v{version}/Xray-windows-64.zip";
        _web[url] = archive;
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(digestOf ?? archive));
        var release = CoreReleasesTests.Release("v" + version, "Xray-windows-64.zip", digest: digest);
        _web[$"https://api.github.com/repos/XTLS/Xray-core/releases/tags/v{version}"] = Encoding.UTF8.GetBytes(release.ToJsonString());
        return new CoreRelease(CoreKind.Xray, version, new Uri(url), SHA256.HashData(digestOf ?? archive), archive.Length);
    }

    [Fact]
    public async Task InstallActivatesNewVersionAndKeepsShippedForRollback()
    {
        using var updater = Updater();

        var outcome = await updater.InstallAsync(PublishXray("26.11.1"), TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Equal(new CoreVersionStatus(CoreKind.Xray, "26.11.1", Shipped: false, PreviousVersion: "26.10.31"), updater.Store.Status(CoreKind.Xray));
        Assert.Equal("xray 26.11.1", File.ReadAllText(updater.Store.ActiveExecutable(CoreKind.Xray)!));
        Assert.True(File.Exists(Path.Combine(_root, "xray", "26.11.1", "LICENSE")));

        Assert.True(updater.Rollback(CoreKind.Xray));
        Assert.Equal(new CoreVersionStatus(CoreKind.Xray, "26.10.31", Shipped: true, PreviousVersion: null), updater.Store.Status(CoreKind.Xray));
        Assert.Null(updater.Store.ActiveExecutable(CoreKind.Xray));
        Assert.False(updater.Rollback(CoreKind.Xray));
    }

    [Fact]
    public async Task SecondUpdateRollsBackToFirstAndPrunesOlder()
    {
        using var updater = Updater();
        await updater.InstallAsync(PublishXray("26.11.1"), TestContext.Current.CancellationToken);
        await updater.InstallAsync(PublishXray("26.11.2"), TestContext.Current.CancellationToken);
        await updater.InstallAsync(PublishXray("26.12.0"), TestContext.Current.CancellationToken);

        Assert.Equal("26.11.2", updater.Store.Status(CoreKind.Xray).PreviousVersion);
        Assert.False(Directory.Exists(Path.Combine(_root, "xray", "26.11.1")));

        updater.Rollback(CoreKind.Xray);
        Assert.Equal("26.11.2", updater.Store.Status(CoreKind.Xray).Version);
    }

    [Fact]
    public async Task ChecksumMismatchKeepsCurrentVersion()
    {
        using var updater = Updater();

        var outcome = await updater.InstallAsync(PublishXray("26.11.1", digestOf: [1, 2, 3]), TestContext.Current.CancellationToken);

        Assert.Equal(CoreUpdateError.ChecksumMismatch, outcome.Error);
        Assert.True(updater.Store.Status(CoreKind.Xray).Shipped);
        Assert.False(Directory.Exists(Path.Combine(_root, "xray")));
    }

    [Fact]
    public async Task IncompatibleCoreIsDiscarded()
    {
        using var updater = Updater();
        _incompatible = "Failed to build XHTTP config";

        var outcome = await updater.InstallAsync(PublishXray("26.11.1"), TestContext.Current.CancellationToken);

        Assert.Equal(CoreUpdateError.Incompatible, outcome.Error);
        Assert.Equal("Failed to build XHTTP config", outcome.Detail);
        Assert.True(updater.Store.Status(CoreKind.Xray).Shipped);
        Assert.False(Directory.Exists(Path.Combine(_root, "xray", "26.11.1")));
    }

    [Fact]
    public async Task ArchiveWithoutExecutableIsRejected()
    {
        using var updater = Updater();

        var outcome = await updater.InstallAsync(PublishXray("26.11.1", Zip(("README.md", "x"))), TestContext.Current.CancellationToken);

        Assert.Equal(CoreUpdateError.InvalidArchive, outcome.Error);
    }

    [Fact]
    public async Task ServiceInstallsNamedVersionAfterCheckingReleaseItself()
    {
        using var updater = Updater();
        PublishXray("26.11.1");

        Assert.True((await updater.InstallVersionAsync(CoreKind.Xray, "26.11.1", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(CoreUpdateError.NotFound, (await updater.InstallVersionAsync(CoreKind.Xray, "27.0.0", TestContext.Current.CancellationToken)).Error);
        Assert.Equal(CoreUpdateError.NotFound, (await updater.InstallVersionAsync(CoreKind.Xray, "26.12.0", TestContext.Current.CancellationToken)).Error);
        Assert.DoesNotContain(_requests, r => r.Contains("27.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckTakesNewestTagWithRelease()
    {
        using var updater = Updater();
        PublishXray("26.11.1");
        _web["https://api.github.com/repos/XTLS/Xray-core/git/matching-refs/tags/v26."] = CoreReleasesTests.Tags("v26.10.31", "v26.11.1", "v26.12.0", "v26.12.1-beta");
        _web["https://api.github.com/repos/XTLS/Xray-core/git/matching-refs/tags/v27."] = CoreReleasesTests.Tags("v27.0.0");

        var outcome = await updater.CheckAsync(CoreKind.Xray, TestContext.Current.CancellationToken);

        // У тега 26.12.0 релиза нет — берётся следующий.
        Assert.Equal("26.11.1", outcome.Choice?.Latest?.Version);
        Assert.Equal("27.0.0", outcome.Choice?.NewerSeries);
        Assert.True(outcome.HasUpdate("26.10.31"));
        Assert.Contains("https://api.github.com/repos/XTLS/Xray-core/releases/tags/v26.12.0", _requests);
    }

    [Fact]
    public void TamperedStateCannotPointOutsideStore()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "cores.json"), """{"schemaVersion":1,"cores":{"xray":{"active":"..\\..\\evil","canRollback":true,"previous":"26.1.1"}}}""");

        var store = new CoreStore(_root);

        Assert.Null(store.ActiveExecutable(CoreKind.Xray));
        Assert.True(store.Status(CoreKind.Xray).Shipped);
    }

    [Fact]
    public void LocatorPrefersActiveStoreVersion()
    {
        var store = new CoreStore(_root);
        store.Install(CoreKind.SingBox, "1.14.3", Encoding.UTF8.GetBytes("sb"), Encoding.UTF8.GetBytes("GPL"));
        store.Activate(CoreKind.SingBox, "1.14.3");
        var previous = CoreLocator.Store;
        try
        {
            CoreLocator.Store = store;
            Assert.Equal(store.ActiveExecutable(CoreKind.SingBox), CoreLocator.Find(CoreKind.SingBox));
        }
        finally
        {
            CoreLocator.Store = previous;
        }
    }

    /// <summary>Ядра версий из cores.lock.json принимают все конфиги проверки (иначе любое обновление считалось бы несовместимым).</summary>
    [Theory]
    [InlineData(CoreKind.Xray)]
    [InlineData(CoreKind.SingBox)]
    public async Task ShippedCoresPassCompatibilityCheck(CoreKind kind)
    {
        var executable = CoreLocator.Find(kind);
        Assert.SkipWhen(executable is null, "Ядро не скачано: dotnet run tools/cores/fetch-cores.cs");

        Assert.Null(await CoreCompatibility.CheckAsync(kind, executable!, CoreCatalog.Get(kind).ShippedVersion, TestContext.Current.CancellationToken));
        Assert.StartsWith("version", await CoreCompatibility.CheckAsync(kind, executable!, "0.0.1", TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public void AssetsAreKnownForThisPlatform()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Обновление из релизов — пока только Windows x64.");
        Assert.Equal("sing-box-1.14.3-windows-amd64/sing-box.exe", CoreCatalog.SingBox.AssetFor("1.14.3")?.ExecutableEntry);
    }

    [Fact]
    public async Task NetworkErrorRetriesThroughConnectedKhors()
    {
        var proxies = new List<IWebProxy?>();
        using var updater = new CoreUpdater(
            new CoreStore(_root),
            localProxyPort: () => 10809,
            fetch: (url, proxy, _, _, _) =>
            {
                proxies.Add(proxy);
                return Task.FromResult(
                    proxy is null ? new GeoFetchResult(null, GeoUpdateError.Network)
                    : url.AbsolutePath.EndsWith("/v26.", StringComparison.Ordinal) ? new GeoFetchResult(CoreReleasesTests.Tags("v26.11.1"), null)
                    : url.AbsolutePath.EndsWith("/v26.11.1", StringComparison.Ordinal) ? new GeoFetchResult(Encoding.UTF8.GetBytes(CoreReleasesTests.Release("v26.11.1", "Xray-windows-64.zip").ToJsonString()), null)
                    : new GeoFetchResult(null, GeoUpdateError.HttpError));
            });

        var outcome = await updater.CheckAsync(CoreKind.Xray, TestContext.Current.CancellationToken);

        Assert.Equal("26.11.1", outcome.Choice?.Latest?.Version);
        Assert.Null(proxies[0]);
        Assert.Equal(new Uri("http://127.0.0.1:10809"), proxies[1]!.GetProxy(new Uri("https://api.github.com")));
    }
}
