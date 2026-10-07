using Khors.Platform;
using Khors.Ipc;
using Khors.Service.KillSwitch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Khors.Service;

/// <summary>
/// <c>khors-service.exe install|uninstall|reset</c> — запускается окном KHORS с повышением прав (UAC).
/// Источник файлов при установке — каталог этого исполняемого файла. <c>reset</c> — «Сбросить сетевые настройки KHORS»,
/// когда служба не установлена или не отвечает (<see cref="NetworkReset"/> без TUN). Код выхода: 0 — успех, 1 — ошибка.
/// </summary>
internal static class ServiceSetup
{
    public static bool IsSetupCommand(string[] args) => args is ["install"] or ["uninstall"] or ["reset"];

    public static int Run(string command)
    {
        using var services = new ServiceCollection().AddPlatform().BuildServiceProvider();
        var control = services.GetRequiredService<IServiceControl>();
        try
        {
            if (command == "install")
            {
                control.Install(AppContext.BaseDirectory);
            }
            else if (command == "reset")
            {
                return Reset(services);
            }
            else
            {
                control.Uninstall();

                // После остановки службы её временные фильтры уже сняты; постоянные (Kill Switch «Постоянно») снимаем сами,
                // и адаптер KHORS, если остался, — после удаления программы в системе ничего не остаётся (docs/SPEC.md, 7.6).
                services.GetRequiredService<IKillSwitch>().Remove();
                services.GetRequiredService<ITunAdapterCleanup>().RemoveAdapters(KillSwitchController.TunnelInterface);
            }

            Console.WriteLine($"KHORS service {command}: done.");
            return 0;
        }
#pragma warning disable CA1031 // Любая ошибка установки — код выхода 1 и текст для администратора.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"KHORS service {command} failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Сброс без службы: настройка Kill Switch службы — «Выключен», фильтры WFP и адаптер KHORS — сняты.</summary>
    private static int Reset(IServiceProvider services)
    {
        // Каталог службы создаёт и защищает только сама служба: нет каталога — нет и настройки, которую нужно сбросить.
        var paths = services.GetRequiredService<ServicePaths>();
        var settingsFile = Directory.Exists(paths.DataDirectory)
            ? Path.Combine(paths.DataDirectory, "killswitch.json")
            : Path.Combine(Path.GetTempPath(), $"khors-killswitch-{Guid.NewGuid():N}.json");
        var killSwitch = new KillSwitchController(
            services.GetRequiredService<IKillSwitch>(),
            settingsFile,
            [],
            NullLogger<KillSwitchController>.Instance);
        var result = new NetworkReset(null, killSwitch, services.GetRequiredService<ITunAdapterCleanup>()).RunAsync().GetAwaiter().GetResult();
        if (!settingsFile.StartsWith(paths.DataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(settingsFile);
        }

        foreach (var item in result.Items)
        {
            Console.WriteLine($"KHORS reset {item.Step}: {item.Outcome} {item.Error}");
        }

        return result.Items.Any(i => i.Outcome == IpcResetOutcome.Failed) ? 1 : 0;
    }
}
