using Khors.Platform.Windows.Processes;
using Microsoft.Extensions.DependencyInjection;

namespace Khors.Platform.Windows;

/// <summary>Регистрация реализаций платформенных интерфейсов для Windows.</summary>
public static class WindowsPlatform
{
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IChildProcessGuard, JobObjectChildProcessGuard>();

        // Остальные реализации — по мере появления (ROADMAP 1.5, 3.x, 4.x).
        return services;
    }
}
