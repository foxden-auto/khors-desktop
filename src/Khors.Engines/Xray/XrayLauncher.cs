using Khors.Core.Diagnostics;
using Khors.Core.Profiles;
using Khors.Platform;

namespace Khors.Engines.Xray;

/// <summary>Запуск Xray для профиля — <see cref="CoreLauncher"/> с ядром Xray.</summary>
public static class XrayLauncher
{
    public static Task<CoreSession> StartAsync(
        Profile profile,
        CoreStartOptions options,
        SecretMasker masker,
        IChildProcessGuard? guard,
        CancellationToken cancellationToken) =>
        CoreLauncher.StartAsync(CoreKind.Xray, profile, options, masker, guard, cancellationToken);
}
