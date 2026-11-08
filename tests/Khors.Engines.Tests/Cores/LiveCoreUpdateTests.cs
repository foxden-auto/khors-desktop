using Khors.Engines.Cores;
using Xunit;

namespace Khors.Engines.Tests.Cores;

/// <summary>
/// Настоящий GitHub (сеть): проверка обновлений и установка последнего релиза серии во временное хранилище с проверкой
/// конфигами KHORS. Включается переменной <c>KHORS_LIVE_CORE_UPDATE=1</c>.
/// </summary>
public sealed class LiveCoreUpdateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("khors-live-cores-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(CoreKind.Xray)]
    [InlineData(CoreKind.SingBox)]
    public async Task LatestReleaseOfSeriesInstallsAndPassesCheck(CoreKind kind)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KHORS_LIVE_CORE_UPDATE") == "1", "Сетевой тест: KHORS_LIVE_CORE_UPDATE=1");
        using var updater = new CoreUpdater(new CoreStore(_root));

        var check = await updater.CheckAsync(kind, TestContext.Current.CancellationToken);
        Assert.True(check.Error is null, $"Проверка: {check.Error}");
        var latest = Assert.IsType<CoreRelease>(check.Choice?.Latest);
        TestContext.Current.SendDiagnosticMessage($"{kind}: latest {latest.Version}, newer series {check.Choice?.NewerSeries ?? "-"}");

        var outcome = await updater.InstallAsync(latest, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, $"{outcome.Error} {outcome.Detail}");
        Assert.Equal(latest.Version, updater.Store.Status(kind).Version);
    }
}
