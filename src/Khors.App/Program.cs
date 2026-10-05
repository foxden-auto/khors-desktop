using Avalonia;
using Avalonia.Controls;
using Khors.Engines;

namespace Khors.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Роль сторожа системного прокси — до любого UI. Синхронное ожидание допустимо: UI-поток ещё не запущен.
        if (SystemProxyWatchdog.TryRunAsync(args, PlatformComposition.CreateSystemProxy).GetAwaiter().GetResult() is { } watchdogExitCode)
        {
            return watchdogExitCode;
        }

        using var instance = PlatformComposition.CreateSingleInstance();
        if (!instance.IsFirst)
        {
            instance.SignalFirstInstance();
            return 0;
        }

        App.SingleInstance = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    // Используется также дизайнером Avalonia.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
