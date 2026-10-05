using Khors.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Khors.Service;

/// <summary>Единственное место, где служба выбирает реализацию платформы (docs/SPEC.md, 3.5).</summary>
internal static class PlatformComposition
{
    /// <summary>Платформа и хост службы (жизненный цикл службы ОС, сервер IPC).</summary>
    public static IServiceCollection AddServicePlatform(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
        {
            return services.AddWindowsPlatform().AddWindowsServiceHost();
        }

        throw new PlatformNotSupportedException("KHORS service supports only Windows for now.");
    }

    /// <summary>Только платформа — для установки и удаления службы.</summary>
    public static IServiceCollection AddPlatform(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
        {
            return services.AddWindowsPlatform();
        }

        throw new PlatformNotSupportedException("KHORS service supports only Windows for now.");
    }
}
