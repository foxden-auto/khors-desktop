using System.Diagnostics;
using Khors.Platform.Windows.Processes;
using Xunit;

namespace Khors.Platform.Windows.Tests;

public class JobObjectChildProcessGuardTests
{
    /// <summary>Закрытие задания (как при завершении KHORS) завершает привязанный процесс.</summary>
    [Fact]
    public void ClosingGuardKillsAttachedProcess()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Job Object — только Windows");

        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;

        var guard = new JobObjectChildProcessGuard();
        guard.Attach(child);
        Assert.False(child.HasExited);

        guard.Dispose();

        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(5)), "Процесс не завершился после закрытия Job Object");
    }
}
