namespace Khors.Platform;

/// <summary>
/// Наличие сети и её смена (ROADMAP 4.3): кабель, Wi-Fi ↔ Ethernet, другая точка доступа. Собственный адаптер
/// туннеля KHORS не учитывается — его подъём и снятие не считаются сменой сети.
/// </summary>
public interface INetworkMonitor : IDisposable
{
    /// <summary>Есть хотя бы один работающий сетевой интерфейс со шлюзом (кроме loopback и туннеля KHORS).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Набор сетей изменился (появилась, пропала, сменилась). Приходит в фоновом потоке, один раз на серию
    /// изменений, когда они утихли.
    /// </summary>
    event EventHandler? Changed;
}
