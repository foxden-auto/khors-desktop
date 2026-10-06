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
using Khors.Engines.Auto;
using Khors.Engines.Connection;
using Khors.Engines.Latency;
using Khors.Ipc;
using Khors.Engines.Storage;
using Khors.Engines.Geo;
using Khors.Engines.Subscriptions;
using Khors.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Khors.App;

public partial class App : Application, IDisposable
{
    // Замер профиля для «Авто»: короче обычного теста — неответивший сервер не задерживает подбор.
    private static readonly TimeSpan s_autoProbeTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Версия приложения для рукопожатия со службой.</summary>
    internal static string AppVersion { get; } = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

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

        // Автообновление подписок (ROADMAP 2.2): первая проверка через несколько секунд после запуска.
        _services.GetRequiredService<SubscriptionScheduler>().Start();

        // Гео-базы (ROADMAP 3.6): проверка раз в сутки, первая — вскоре после запуска.
        _services.GetRequiredService<GeoManager>().Start();

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
        services.AddSingleton<ICoreLauncher>(sp =>
        {
            var masker = sp.GetRequiredService<SecretMasker>();
            var tun = new ServiceTunLauncher(sp.GetRequiredService<IIpcClientTransport>(), AppVersion, masker);
            return new SelectingCoreLauncher(masker, sp.GetService<IChildProcessGuard>(), tun, sp.GetRequiredService<AppPaths>().GeoDirectory);
        });
        services.AddSingleton(sp => new ConnectionManager(sp.GetRequiredService<ICoreLauncher>(), sp.GetService<ISystemProxy>(), SystemProxyWatchdog.EnsureStarted));
        services.AddSingleton<IAppClipboard>(_ => new WindowClipboard(() => _window));
        services.AddSingleton<IDesktopDialogs>(sp => new WindowDialogs(() => _window, sp.GetRequiredService<IAppClipboard>(), sp.GetService<IScreenCapture>()));
        services.AddSingleton(sp =>
        {
            var connection = sp.GetRequiredService<ConnectionManager>();
            // При сетевой ошибке подписка повторно загружается через подключённый KHORS.
            return new SubscriptionUpdater(
                sp.GetRequiredService<ProfileRepository>(),
                () => connection.Status is { State: ConnectionState.Connected, HttpPort: { } port } ? port : null);
        });
        services.AddSingleton(sp => new SubscriptionScheduler(
            sp.GetRequiredService<ProfileRepository>(),
            sp.GetRequiredService<SubscriptionUpdater>(),
            () => sp.GetRequiredService<SettingsStore>().Current));
        services.AddSingleton(sp =>
        {
            var connection = sp.GetRequiredService<ConnectionManager>();
            var settings = sp.GetRequiredService<SettingsStore>();
            var profiles = sp.GetRequiredService<ProfileRepository>();
            var probe = new LatencyProbe(
                connection,
                sp.GetRequiredService<ICoreLauncher>(),
                () => LatencyTester.TestUrlOrDefault(settings.Current.LatencyTestUrl),
                s_autoProbeTimeout);
            return new AutoConnector(connection, probe, () => profiles.Profiles, () => CoreStartPreferences.From(settings.Current));
        });
        services.AddSingleton(sp =>
        {
            var connection = sp.GetRequiredService<ConnectionManager>();
            return new GeoDatabaseUpdater(
                sp.GetRequiredService<AppPaths>().GeoDirectory,
                () => connection.Status is { State: ConnectionState.Connected, HttpPort: { } port } ? port : null);
        });
        services.AddSingleton(sp => new GeoManager(
            sp.GetRequiredService<GeoDatabaseUpdater>(),
            new ServiceGeoClient(sp.GetRequiredService<IIpcClientTransport>(), AppVersion),
            sp.GetRequiredService<SettingsStore>()));
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

    /// <summary>Шаг выхода: ошибка записывается в трассировку и не мешает следующим шагам и завершению.</summary>
    private static async Task RunExitStepAsync(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceError($"KHORS exit step failed: {ex.GetType().Name}");
        }
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

        // Каждый шаг очистки — отдельно: сбой одного не должен оставить процесс висеть без окна и трея.
        // Завершение приложения выполняется всегда (finally).
        try
        {
            if (_services is not null)
            {
                await RunExitStepAsync(() => _services.GetRequiredService<SubscriptionScheduler>().DisposeAsync().AsTask()).ConfigureAwait(true);
                await RunExitStepAsync(() => _services.GetRequiredService<GeoManager>().DisposeAsync().AsTask()).ConfigureAwait(true);

                // «Авто» — до отключения, чтобы не начало подбирать сервер заново.
                await RunExitStepAsync(() => _services.GetRequiredService<AutoConnector>().DisposeAsync().AsTask()).ConfigureAwait(true);

                // Отключение возвращает системный прокси и останавливает ядро.
                await RunExitStepAsync(() => _services.GetRequiredService<ConnectionManager>().DisposeAsync().AsTask()).ConfigureAwait(true);
                await RunExitStepAsync(() =>
                {
                    _services.GetRequiredService<MainWindowViewModel>().Dispose();
                    return Task.CompletedTask;
                }).ConfigureAwait(true);
            }

            _tray?.Dispose();
            _tray = null;
            if (_services is not null)
            {
                await RunExitStepAsync(() => _services.DisposeAsync().AsTask()).ConfigureAwait(true);
                _services = null;
            }
        }
        finally
        {
            desktop.Shutdown();
        }
    }
}
