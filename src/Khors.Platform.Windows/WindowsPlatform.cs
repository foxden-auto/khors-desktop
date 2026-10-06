using Khors.Ipc;
using Khors.Platform.Windows.Ipc;
using Khors.Platform.Windows.Processes;
using Khors.Platform.Windows.Proxy;
using Khors.Platform.Windows.Screen;
using Khors.Platform.Windows.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        services.AddSingleton<IScreenCapture, GdiScreenCapture>();
        services.AddSingleton<IServiceControl, WindowsServiceControl>();

        // Клиент IPC доверяет только процессу службы KhorsService (канал мог занять чужой процесс).
        services.AddSingleton<IIpcClientTransport>(sp =>
        {
            var service = sp.GetRequiredService<IServiceControl>();
            return new NamedPipeIpcClient(NamedPipeIpcServer.ServicePipeName, pid => service.GetProcessId() == pid);
        });

        // Остальные реализации — по мере появления (ROADMAP 3.x, 4.x).
        return services;
    }

    /// <summary>Для процесса службы: жизненный цикл службы Windows и сервер IPC на named pipe.</summary>
    public static IServiceCollection AddWindowsServiceHost(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddWindowsService(options => options.ServiceName = WindowsServiceControl.ServiceName);
        services.AddSingleton<IIpcServerTransport>(_ => NamedPipeIpcServer.ForCurrentProcess());
        services.AddSingleton(_ =>
        {
            var paths = new ServicePaths(ServiceDataDirectory.DefaultPath);
            ServiceDataDirectory.Ensure(paths.DataDirectory, Path.GetFileName(paths.GeoDirectory));
            return paths;
        });
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
