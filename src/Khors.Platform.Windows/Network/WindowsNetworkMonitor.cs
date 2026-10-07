using System.Net;
using System.Net.NetworkInformation;

namespace Khors.Platform.Windows.Network;

/// <summary>
/// Сеть по уведомлениям Windows об адресах (<see cref="NetworkChange"/>). Уведомления приходят пачками и на любой
/// адаптер, поэтому после затишья (<see cref="SettleDelay"/>) сравнивается «отпечаток» сетей: работающие интерфейсы
/// со шлюзом, их адреса и шлюзы. Адаптер туннеля KHORS, loopback и туннельные интерфейсы в отпечаток не входят.
/// </summary>
public sealed class WindowsNetworkMonitor : INetworkMonitor
{
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    private readonly string _tunnelInterface;
    private readonly Timer _settle;
    private readonly Lock _lock = new();
    private string _signature;
    private bool _available;
    private int _disposed;

    /// <param name="tunnelInterface">Имя адаптера туннеля KHORS — не учитывается.</param>
    public WindowsNetworkMonitor(string tunnelInterface = "KHORS")
    {
        _tunnelInterface = tunnelInterface;
        (_signature, _available) = Snapshot(tunnelInterface);
        _settle = new Timer(_ => Settle());
        NetworkChange.NetworkAddressChanged += OnChanged;
        NetworkChange.NetworkAvailabilityChanged += OnChanged;
    }

    public bool IsAvailable => Volatile.Read(ref _available);

    public event EventHandler? Changed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        NetworkChange.NetworkAddressChanged -= OnChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnChanged;
        _settle.Dispose();
    }

    /// <summary>Отпечаток сетей и есть ли хоть одна; без адаптера <paramref name="tunnelInterface"/>.</summary>
    internal static (string Signature, bool Available) Snapshot(string tunnelInterface)
    {
        var parts = new List<string>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return (string.Empty, false);
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || string.Equals(nic.Name, tunnelInterface, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var gateways = properties.GatewayAddresses
                .Select(g => g.Address)
                .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
                .Select(a => a.ToString())
                .Order(StringComparer.Ordinal)
                .ToList();
            if (gateways.Count == 0)
            {
                continue;
            }

            var addresses = properties.UnicastAddresses
                .Select(a => a.Address)
                .Where(a => !a.IsIPv6LinkLocal)
                .Select(a => a.ToString())
                .Order(StringComparer.Ordinal);
            parts.Add($"{nic.Id}|{string.Join(',', addresses)}|{string.Join(',', gateways)}");
        }

        parts.Sort(StringComparer.Ordinal);
        return (string.Join(';', parts), parts.Count > 0);
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _settle.Change(SettleDelay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Монитор закрыт между проверкой и таймером.
            }
        }
    }

    private void Settle()
    {
        var (signature, available) = Snapshot(_tunnelInterface);
        lock (_lock)
        {
            if (signature == _signature)
            {
                return;
            }

            _signature = signature;
            Volatile.Write(ref _available, available);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
