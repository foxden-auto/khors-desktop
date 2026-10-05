namespace Khors.Platform;

public enum ServiceState
{
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,

    /// <summary>Состояние не удалось узнать.</summary>
    Unknown,
}

public enum ServiceSetupAction
{
    Install,
    Uninstall,
}

public enum ServiceSetupResult
{
    Succeeded,

    /// <summary>Пользователь отказал в повышении прав.</summary>
    Cancelled,

    /// <summary>Рядом с приложением нет исполняемого файла службы.</summary>
    SetupNotFound,

    Failed,
}

/// <summary>
/// Привилегированная служба (Windows — служба KhorsService, Linux — юнит systemd).
/// Установка копирует службу и ядра в каталог, куда может писать только администратор, и регистрирует её оттуда:
/// подмена файлов из-под обычного пользователя не должна давать прав службы.
/// </summary>
public interface IServiceControl
{
    ServiceState GetState();

    /// <summary>Процесс работающей службы; <c>null</c> — служба не запущена.</summary>
    int? GetProcessId();

    /// <summary>
    /// Установка или обновление: остановка, копирование файлов службы и ядер из <paramref name="sourceDirectory"/>,
    /// регистрация с автозапуском, запуск. Требует прав администратора.
    /// </summary>
    void Install(string sourceDirectory);

    /// <summary>Остановка, удаление регистрации и файлов службы. Требует прав администратора.</summary>
    void Uninstall();

    /// <summary>Из UI без прав администратора: запуск установки или удаления с запросом повышения прав.</summary>
    Task<ServiceSetupResult> RunElevatedSetupAsync(ServiceSetupAction action, CancellationToken cancellationToken);
}
