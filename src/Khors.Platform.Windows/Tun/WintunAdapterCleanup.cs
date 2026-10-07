using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Khors.Platform.Windows.Tun;

/// <summary>
/// Удаление адаптера wintun с именем KHORS через SetupAPI. Перебираются все сетевые устройства класса Net, в том числе
/// отсутствующие (битый адаптер, который sing-box не может ни создать, ни открыть, — ROADMAP, «Известные проблемы»).
/// Удаляется устройство, только если это wintun (hardware ID <c>Wintun</c>) и имя его подключения в реестре сети —
/// ровно заданное (<see cref="IsTunnelAdapter"/>): адаптеры Throne и других программ на wintun не трогаются.
/// </summary>
public sealed partial class WintunAdapterCleanup : ITunAdapterCleanup
{
    private const string NetClassKey = @"SYSTEM\CurrentControlSet\Control\Network\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const uint SpdrpHardwareId = 0x1;
    private const uint DicsFlagGlobal = 0x1;
    private const uint DiregDrv = 0x2;
    private const uint KeyQueryValue = 0x1;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;

    private static readonly Guid s_netClass = new("4d36e972-e325-11ce-bfc1-08002be10318");

    public int RemoveAdapters(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var classGuid = s_netClass;

        // Без DIGCF_PRESENT — и отсутствующие устройства.
        var devices = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, 0);
        if (devices == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetupDiGetClassDevs failed.");
        }

        try
        {
            var removed = 0;
            for (uint index = 0; ; index++)
            {
                var device = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(devices, index, ref device))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ErrorNoMoreItems)
                    {
                        return removed;
                    }

                    throw new Win32Exception(error, "SetupDiEnumDeviceInfo failed.");
                }

                if (!IsTunnelAdapter(HardwareIds(devices, ref device), ConnectionName(devices, ref device), name))
                {
                    continue;
                }

                if (!DiUninstallDevice(IntPtr.Zero, devices, ref device, 0, out _))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "DiUninstallDevice failed.");
                }

                removed++;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devices);
        }
    }

    /// <summary>Адаптер wintun с именем подключения <paramref name="name"/>.</summary>
    internal static bool IsTunnelAdapter(IReadOnlyList<string> hardwareIds, string? connectionName, string name) =>
        hardwareIds.Any(id => string.Equals(id, "Wintun", StringComparison.OrdinalIgnoreCase))
        && string.Equals(connectionName, name, StringComparison.OrdinalIgnoreCase);

    private static List<string> HardwareIds(IntPtr devices, ref SpDevInfoData device)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(devices, ref device, SpdrpHardwareId, out _, buffer, (uint)buffer.Length, out var required))
        {
            if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
            {
                return [];
            }

            buffer = new byte[required];
            if (!SetupDiGetDeviceRegistryPropertyW(devices, ref device, SpdrpHardwareId, out _, buffer, (uint)buffer.Length, out required))
            {
                return [];
            }
        }

        // REG_MULTI_SZ: строки UTF-16 через нуль.
        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, (uint)buffer.Length));
        return [.. text.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>Имя подключения: NetCfgInstanceId из ключа драйвера → <c>Network\{класс}\{id}\Connection\Name</c>.</summary>
    private static string? ConnectionName(IntPtr devices, ref SpDevInfoData device)
    {
        var handle = SetupDiOpenDevRegKey(devices, ref device, DicsFlagGlobal, 0, DiregDrv, KeyQueryValue);
        if (handle == new IntPtr(-1))
        {
            return null;
        }

        using var driverKey = RegistryKey.FromHandle(new SafeRegistryHandle(handle, ownsHandle: true));
        if (driverKey.GetValue("NetCfgInstanceId") is not string instanceId)
        {
            return null;
        }

        using var connection = Registry.LocalMachine.OpenSubKey($@"{NetClassKey}\{instanceId}\Connection");
        return connection?.GetValue("Name") as string;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(IntPtr devices, uint index, ref SpDevInfoData device);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceRegistryPropertyW(IntPtr devices, ref SpDevInfoData device, uint property, out uint dataType, [Out] byte[] buffer, uint bufferSize, out uint requiredSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial IntPtr SetupDiOpenDevRegKey(IntPtr devices, ref SpDevInfoData device, uint scope, uint hwProfile, uint keyType, uint desiredAccess);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr devices);

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DiUninstallDevice(IntPtr parent, IntPtr devices, ref SpDevInfoData device, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool needReboot);
}
