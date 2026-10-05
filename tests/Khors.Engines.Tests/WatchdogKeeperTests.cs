using System.Diagnostics;
using Xunit;

namespace Khors.Engines.Tests;

/// <summary>Вместо настоящего сторожа — посторонний процесс: проверяется только «держать запущенным».</summary>
public sealed class WatchdogKeeperTests : IDisposable
{
    private readonly List<int> _started = [];

    public void Dispose()
    {
        foreach (var id in _started)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill();
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private static ProcessStartInfo LongRunning() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }
        : new ProcessStartInfo("sleep", "60") { UseShellExecute = false };

    private static ProcessStartInfo ExitsImmediately() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true }
        : new ProcessStartInfo("true") { UseShellExecute = false };

    [Fact]
    public void RepeatedEnsureStartedKeepsSingleProcess()
    {
        using var keeper = new WatchdogKeeper(LongRunning);

        keeper.EnsureStarted();
        _started.Add(keeper.ProcessId!.Value);
        keeper.EnsureStarted();

        Assert.Equal(1, keeper.Starts);
    }

    [Fact]
    public async Task KilledWatchdogIsStartedAgain()
    {
        using var keeper = new WatchdogKeeper(LongRunning);
        keeper.EnsureStarted();
        var first = keeper.ProcessId!.Value;
        _started.Add(first);

        using (var process = Process.GetProcessById(first))
        {
            process.Kill();
        }

        var deadline = Stopwatch.StartNew();
        while (keeper.Starts < 2 && deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, keeper.Starts);
        var second = keeper.ProcessId!.Value;
        _started.Add(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task CrashLoopStopsAfterLimitAndEnsureStartedResetsIt()
    {
        using var keeper = new WatchdogKeeper(ExitsImmediately, maxRestarts: 3);
        keeper.EnsureStarted();

        var deadline = Stopwatch.StartNew();
        while (!keeper.GaveUp && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(keeper.GaveUp);
        Assert.Equal(4, keeper.Starts); // первый запуск + 3 перезапуска

        keeper.EnsureStarted();
        Assert.False(keeper.GaveUp);
        Assert.Equal(5, keeper.Starts);
    }
}
