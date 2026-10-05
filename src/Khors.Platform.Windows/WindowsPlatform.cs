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
        services.AddSingleton(new AppPaths(DataDirectory, StateDirectory));
        services.AddSingleton<IChildProcessGuard, JobObjectChildProcessGuard>();
        services.AddSingleton<ISystemProxy>(_ => new WindowsSystemProxy(StateDirectory));

        // Остальные реализации — по мере появления (ROADMAP 3.x, 4.x).
        return services;
    }

    /// <summary>
    /// Данные пользователя: <c>%APPDATA%\KHORS</c> (профили, настройки). Переменная окружения <c>KHORS_DATA_DIR</c>
    /// переносит все данные и журналы отката в другой каталог — для тестов и переносимого режима.
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("KHORS_DATA_DIR") is { Length: > 0 } overridden
            ? Path.GetFullPath(overridden)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KHORS");

    /// <summary>Состояние для отката изменений системы: <c>%APPDATA%\KHORS\state</c>.</summary>
    public static string StateDirectory { get; } = Path.Combine(DataDirectory, "state");
}
