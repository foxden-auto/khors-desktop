using System.Diagnostics;
using System.Globalization;
using Khors.Platform;
using Xunit;

namespace Khors.Engines.Tests;

public class SystemProxyWatchdogTests
{
    [Fact]
    public async Task OrdinaryStartIsNotAWatchdog()
    {
        var proxy = new FakeSystemProxy();

        Assert.Null(await SystemProxyWatchdog.TryRunAsync(["vless://…"], () => proxy, TestContext.Current.CancellationToken));
        Assert.Equal(0, proxy.Recoveries);
    }

    [Fact]
    public async Task WatchdogRecoversProxyRightAfterParentIsKilled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var parent = StartLongProcess();
        var proxy = new FakeSystemProxy();

        var watchdog = SystemProxyWatchdog.TryRunAsync(Arguments(parent.Id, parent.StartTime), () => proxy, ct);
        await Task.Delay(300, ct);
        Assert.False(watchdog.IsCompleted, "Сторож не должен срабатывать, пока родитель жив");

        var stopwatch = Stopwatch.StartNew();
        parent.Kill();
        Assert.Equal(0, await watchdog.WaitAsync(TimeSpan.FromSeconds(3), ct));

        Assert.Equal(1, proxy.Recoveries);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ReusedProcessIdIsTreatedAsExitedParent()
    {
        using var unrelated = StartLongProcess();
        var proxy = new FakeSystemProxy();
        try
        {
            // Тот же PID, но другое время запуска — это не наш родитель.
            var exitCode = await SystemProxyWatchdog.TryRunAsync(
                Arguments(unrelated.Id, unrelated.StartTime.AddMinutes(-5)),
                () => proxy,
                TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(1, proxy.Recoveries);
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            unrelated.Kill();
        }
    }

    [Fact]
    public async Task MalformedArgumentsAreRejected()
    {
        var proxy = new FakeSystemProxy();

        Assert.Equal(2, await SystemProxyWatchdog.TryRunAsync([SystemProxyWatchdog.Argument, "x"], () => proxy, TestContext.Current.CancellationToken));
        Assert.Equal(0, proxy.Recoveries);
    }

    private static string[] Arguments(int processId, DateTime startTime) =>
    [
        SystemProxyWatchdog.Argument,
        processId.ToString(CultureInfo.InvariantCulture),
        startTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
    ];

    private static Process StartLongProcess() =>
        Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }
            : new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;

    private sealed class FakeSystemProxy : ISystemProxy
    {
        public int Recoveries { get; private set; }

        public void Enable(SystemProxySettings settings)
        {
        }

        public bool Restore() => false;

        public SystemProxyRecovery RecoverAfterCrash()
        {
            Recoveries++;
            return SystemProxyRecovery.Restored;
        }
    }
}
