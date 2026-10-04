using Khors.Platform;
using Khors.Platform.Windows.Processes;

namespace Khors.DevCli;

/// <summary>Единственное место, где утилита выбирает реализацию платформы (docs/SPEC.md, 3.5).</summary>
internal static class PlatformComposition
{
    public static IChildProcessGuard? CreateChildProcessGuard() =>
        OperatingSystem.IsWindows() ? new JobObjectChildProcessGuard() : null;
}
