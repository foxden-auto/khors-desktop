using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Khors.App.Resources;
using Khors.App.Services;
using Khors.App.ViewModels;
using Khors.App.Views;
using Khors.Core.Diagnostics;
using Khors.Engines;
using Khors.Engines.Connection;
using Khors.Engines.Storage;
using Khors.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Khors.App;

public partial class App : Application, IDisposable
{
    private ServiceProvider? _services;
    private MainWindow? _window;
    private TrayController? _tray;
    private bool _exiting;

    /// <summary>Единственный экземпляр, созданный в <see cref="Program.Main"/>.</summary>
    internal static ISingleInstance? SingleInstance { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Страховка на случай выхода в обход <see cref="ExitAsync"/>: убрать иконку из трея и освободить сервисы.</summary>
    public void Dispose()
    {
        _tray?.Dispose();
        _tray = null;
        _services?.Dispose();
        _services = null;
        GC.SuppressFinalize(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        _services = BuildServices();
        ApplyLanguage(_services.GetRequiredService<SettingsStore>().Current.Language);

        // Следы прошлого аварийного завершения (системный прокси) — до первого подключения (CLAUDE.md, правило 9).
        var recovered = _services.GetService<ISystemProxy>()?.RecoverAfterCrash() == SystemProxyRecovery.Restored;

        var viewModel = _services.GetRequiredService<MainWindowViewModel>();
        viewModel.ShowStartupNotices(recovered);

        _window = new MainWindow { DataContext = viewModel };
        desktop.MainWindow = _window;
        _tray = new TrayController(this, viewModel, ShowWindow, () => _ = ExitAsync(desktop));

        if (SingleInstance is not null)
        {
            SingleInstance.ActivationRequested += (_, _) => Dispatcher.UIThread.Post(ShowWindow);
        }

        // Завершение сеанса Windows и т.п.: сначала отключаемся и возвращаем прокси.
        desktop.ShutdownRequested += (_, e) =>
        {
            if (!_exiting)
            {
                e.Cancel = true;
                _ = ExitAsync(desktop);
            }
        };

        base.OnFrameworkInitializationCompleted();
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection().AddPlatform();
        services.AddSingleton(new SecretMasker());
        services.AddSingleton(sp => ProfileRepository.Open(sp.GetRequiredService<AppPaths>().ProfilesFile));
        services.AddSingleton(sp => SettingsStore.Open(sp.GetRequiredService<AppPaths>().SettingsFile));
        services.AddSingleton<ICoreLauncher>(sp => new XrayCoreLauncher(sp.GetRequiredService<SecretMasker>(), sp.GetService<IChildProcessGuard>()));
        services.AddSingleton(sp => new ConnectionManager(sp.GetRequiredService<ICoreLauncher>(), sp.GetService<ISystemProxy>(), SystemProxyWatchdog.EnsureStarted));
        services.AddSingleton<IClipboardText>(_ => new WindowClipboardText(() => _window));
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }

    private static void ApplyLanguage(string? language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return;
        }

        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = culture;
        Strings.Culture = culture;
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private async Task ExitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        if (_window is not null)
        {
            _window.AllowClose = true;
        }

        if (_services is not null)
        {
            // Отключение возвращает системный прокси и останавливает ядро.
            await _services.GetRequiredService<ConnectionManager>().DisposeAsync().ConfigureAwait(true);
            _services.GetRequiredService<MainWindowViewModel>().Dispose();
        }

        _tray?.Dispose();
        _tray = null;
        if (_services is not null)
        {
            await _services.DisposeAsync().ConfigureAwait(true);
            _services = null;
        }

        desktop.Shutdown();
    }
}
