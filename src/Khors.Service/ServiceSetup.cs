using Khors.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace Khors.Service;

/// <summary>
/// <c>khors-service.exe install|uninstall</c> — запускается окном KHORS с повышением прав (UAC).
/// Источник файлов при установке — каталог этого исполняемого файла. Код выхода: 0 — успех, 1 — ошибка.
/// </summary>
internal static class ServiceSetup
{
    public static bool IsSetupCommand(string[] args) => args is ["install"] or ["uninstall"];

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
            else
            {
                control.Uninstall();
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
}
