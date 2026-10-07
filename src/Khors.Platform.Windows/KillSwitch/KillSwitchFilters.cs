using System.Net;

namespace Khors.Platform.Windows.KillSwitch;

/// <summary>Уровень WFP: разрешение исходящего соединения (ALE connect) для IPv4 или IPv6.</summary>
internal enum WfpLayer
{
    ConnectV4,
    ConnectV6,
}

/// <summary>Условие фильтра; все условия одного фильтра должны выполниться вместе.</summary>
internal abstract record WfpCondition;

/// <summary>Соединение внутри компьютера (флаг IS_LOOPBACK).</summary>
internal sealed record LoopbackCondition : WfpCondition;

/// <summary>Соединение уходит через адаптер с этим LUID.</summary>
internal sealed record InterfaceCondition(ulong Luid) : WfpCondition;

/// <summary>Соединение открывает программа по этому пути.</summary>
internal sealed record ApplicationCondition(string Path) : WfpCondition;

/// <summary>Адрес назначения — в подсети.</summary>
internal sealed record RemoteNetworkCondition(IPNetwork Network) : WfpCondition;

/// <summary>UDP на порт назначения.</summary>
internal sealed record UdpRemotePortCondition(ushort Port) : WfpCondition;

/// <param name="Weight">Вес внутри sublayer KHORS (0–15): разрешения выше блокировки.</param>
internal sealed record WfpFilterSpec(string Name, WfpLayer Layer, bool Permit, byte Weight, IReadOnlyList<WfpCondition> Conditions);

/// <summary>
/// Набор фильтров Kill Switch для политики — чистая функция (проверяется тестами без прав администратора).
/// В sublayer KHORS побеждает подходящий фильтр с наибольшим весом: разрешения (вес <see cref="PermitWeight"/>)
/// перекрывают общую блокировку (вес <see cref="BlockWeight"/>). Блокировка в sublayer KHORS действует, даже если
/// брандмауэр Windows соединение разрешает.
/// </summary>
internal static class KillSwitchFilters
{
    public const byte BlockWeight = 0;
    public const byte PermitWeight = 10;

    private const ushort DhcpServerPort = 67;
    private const ushort Dhcp6ServerPort = 547;

    /// <param name="tunnelLuid">
    /// LUID адаптера туннеля; <c>null</c> — адаптера сейчас нет (тогда трафик только блокируется). IPv6 через адаптер —
    /// только при <see cref="KillSwitchPolicy.TunnelIpv6"/>.
    /// </param>
    public static IReadOnlyList<WfpFilterSpec> Build(KillSwitchPolicy policy, ulong? tunnelLuid)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var filters = new List<WfpFilterSpec>();
        foreach (var layer in Enum.GetValues<WfpLayer>())
        {
            var suffix = layer == WfpLayer.ConnectV4 ? "IPv4" : "IPv6";
            filters.Add(Permit($"KHORS Kill Switch: loopback ({suffix})", layer, new LoopbackCondition()));

            if (tunnelLuid is { } luid && (layer == WfpLayer.ConnectV4 || policy.TunnelIpv6))
            {
                filters.Add(Permit($"KHORS Kill Switch: tunnel ({suffix})", layer, new InterfaceCondition(luid)));
            }

            foreach (var path in policy.Applications)
            {
                filters.Add(Permit($"KHORS Kill Switch: {Path.GetFileName(path)} ({suffix})", layer, new ApplicationCondition(path)));
            }

            // DHCP — иначе адрес не обновится и пропадёт и физическая сеть, и туннель.
            filters.Add(Permit($"KHORS Kill Switch: DHCP ({suffix})", layer, new UdpRemotePortCondition(layer == WfpLayer.ConnectV4 ? DhcpServerPort : Dhcp6ServerPort)));

            if (policy.AllowLocalNetwork)
            {
                foreach (var network in KillSwitchPolicy.LocalNetworks.Where(n => IsLayerFamily(n, layer)))
                {
                    filters.Add(Permit($"KHORS Kill Switch: local network {network} ({suffix})", layer, new RemoteNetworkCondition(network)));
                }
            }

            filters.Add(new WfpFilterSpec($"KHORS Kill Switch: block ({suffix})", layer, Permit: false, BlockWeight, []));
        }

        return filters;
    }

    private static WfpFilterSpec Permit(string name, WfpLayer layer, WfpCondition condition) =>
        new(name, layer, Permit: true, PermitWeight, [condition]);

    private static bool IsLayerFamily(IPNetwork network, WfpLayer layer) =>
        (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) == (layer == WfpLayer.ConnectV4);
}
