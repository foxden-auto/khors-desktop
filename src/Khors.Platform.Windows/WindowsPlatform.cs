using Khors.Platform.Windows.Processes;
using Khors.Platform.Windows.Proxy;
using Microsoft.Extensions.DependencyInjection;

namespace Khors.Platform.Windows;

/// <summary>Регистрация реализаций платформенных интерфейсов для Windows.</summary>
public static class WindowsPlatform
{
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IChildProcessGuard, JobObjectChildProcessGuard>();
        services.AddSingleton<ISystemProxy>(_ => new WindowsSystemProxy(StateDirectory));

        // Остальные реализации — по мере появления (ROADMAP 3.x, 4.x).
        return services;
    }

    /// <summary>Данные пользователя: <c>%APPDATA%\KHORS</c> (профили, настройки).</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KHORS");

    /// <summary>Состояние для отката изменений системы: <c>%APPDATA%\KHORS\state</c>.</summary>
    public static string StateDirectory { get; } = Path.Combine(DataDirectory, "state");
}
