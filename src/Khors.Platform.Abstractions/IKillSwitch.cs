using System.Net;

namespace Khors.Platform;

/// <summary>
/// Kill Switch (docs/SPEC.md, 4.6): весь исходящий трафик блокируется, кроме разрешённого политикой.
/// Windows — WFP (свой sublayer и фильтры «KHORS …»), Linux — nftables. Требует прав службы.
/// </summary>
public interface IKillSwitch
{
    /// <summary>
    /// Ставит фильтры политики вместо прежних. Новые фильтры появляются раньше, чем снимаются старые, поэтому
    /// между ними трафик не проходит. Ошибка — исключение, прежние фильтры остаются.
    /// </summary>
    void Apply(KillSwitchPolicy policy);

    /// <summary>Снимает все фильтры KHORS (и временные, и постоянные) вместе с их служебными объектами.</summary>
    void Remove();

    /// <summary>В системе есть постоянные фильтры KHORS (остались с прошлого запуска или от режима «Постоянный»).</summary>
    bool HasPersistentFilters();
}

/// <summary>Что разрешено при включённом Kill Switch; остальной исходящий трафик блокируется.</summary>
/// <param name="Persistent">
/// Постоянные фильтры: переживают завершение службы и перезагрузку. Иначе — временные: исчезают вместе
/// с процессом службы.
/// </param>
/// <param name="AllowLocalNetwork">Локальные сети (частные адреса, link-local, multicast) — разрешены.</param>
/// <param name="Applications">Полные пути программ, которым разрешено всё (ядра в каталоге службы).</param>
/// <param name="TunnelInterface">Имя адаптера туннеля, через который трафик разрешён; <c>null</c> — туннеля нет.</param>
public sealed record KillSwitchPolicy(
    bool Persistent,
    bool AllowLocalNetwork,
    IReadOnlyList<string> Applications,
    string? TunnelInterface)
{
    /// <summary>Частные и служебные сети, разрешённые при <see cref="AllowLocalNetwork"/>.</summary>
    public static IReadOnlyList<IPNetwork> LocalNetworks { get; } =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("255.255.255.255/32"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("ff00::/8"),
    ];

    // Пути программ не секрет, но список длинный — в журнал не выводим.
    public override string ToString() =>
        $"KillSwitchPolicy {{ Persistent = {Persistent}, AllowLocalNetwork = {AllowLocalNetwork}, Applications = {Applications.Count}, TunnelInterface = {TunnelInterface} }}";
}
