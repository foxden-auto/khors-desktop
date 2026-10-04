namespace Khors.Platform;

/// <summary>
/// Системный прокси ОС для режима «Системный прокси» (docs/SPEC.md, 3.3).
/// Любое изменение имеет путь отката: прежние настройки сохраняются в журнал до изменения
/// и возвращаются при отключении, при сбое и при следующем запуске (CLAUDE.md, правило 9).
/// </summary>
public interface ISystemProxy
{
    /// <summary>Направляет системный прокси на локальный HTTP-вход ядра.</summary>
    void Enable(SystemProxySettings settings);

    /// <summary>Возвращает настройки, бывшие до <see cref="Enable"/>. Без журнала ничего не делает.</summary>
    /// <returns><c>true</c>, если настройки восстановлены.</returns>
    bool Restore();

    /// <summary>
    /// Вызывается при запуске: если после аварийного завершения остался журнал — восстанавливает
    /// прежние настройки, но только если прокси всё ещё указывает на KHORS.
    /// </summary>
    SystemProxyRecovery RecoverAfterCrash();
}

/// <param name="Host">Адрес локального HTTP-входа (loopback).</param>
/// <param name="Bypass">Адреса, идущие мимо прокси.</param>
public sealed record SystemProxySettings(string Host, int Port, IReadOnlyList<string> Bypass)
{
    /// <summary>Локальные адреса и частные сети — напрямую.</summary>
    public static IReadOnlyList<string> DefaultBypass { get; } =
    [
        "localhost", "127.*", "10.*",
        .. Enumerable.Range(16, 16).Select(n => $"172.{n}.*"),
        "192.168.*", "169.254.*", "<local>",
    ];

    public static SystemProxySettings ForLocalHttp(int port) => new("127.0.0.1", port, DefaultBypass);
}

public enum SystemProxyRecovery
{
    /// <summary>Журнала нет — восстанавливать нечего.</summary>
    NothingToRecover,

    /// <summary>Прокси указывал на KHORS — прежние настройки восстановлены.</summary>
    Restored,

    /// <summary>Пользователь сменил прокси после сбоя — его настройки не тронуты, журнал удалён.</summary>
    ChangedByUser,

    /// <summary>Журнал повреждён — настройки не тронуты, журнал удалён.</summary>
    JournalCorrupted,
}
