using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Khors.Platform.Windows.Proxy;

/// <summary>
/// Настройки прокси пользователя через InternetQueryOption / InternetSetOption (INTERNET_OPTION_PER_CONNECTION_OPTION).
/// Права администратора не нужны; изменения сразу видят Edge, Chrome и другие программы на WinINet.
/// Меняется подключение LAN (pszConnection = NULL).
/// </summary>
internal sealed partial class WinInetProxySettings : IWinInetProxySettings
{
    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionPerConnectionOption = 75;

    private const int PerConnFlags = 1;
    private const int PerConnProxyServer = 2;
    private const int PerConnProxyBypass = 3;
    private const int PerConnAutoConfigUrl = 4;
    private const int PerConnFlagsUi = 10;

    public WinInetProxyState Read()
    {
        // FLAGS_UI (Windows 7+) учитывает «Автоматическое определение параметров»; при отказе — обычные FLAGS.
        var values = Query(PerConnFlagsUi) ?? Query(PerConnFlags)
            ?? throw new Win32Exception(Marshal.GetLastPInvokeError(), "InternetQueryOption failed.");

        return new WinInetProxyState(values.Flags, values.Server, values.Bypass, values.AutoConfigUrl);
    }

    public void Write(WinInetProxyState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var strings = new List<IntPtr>();
        var options = Marshal.AllocHGlobal(Marshal.SizeOf<PerConnOption>() * 4);
        var list = Marshal.AllocHGlobal(Marshal.SizeOf<PerConnOptionList>());
        try
        {
            IntPtr String(string? value)
            {
                var pointer = value is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(value);
                strings.Add(pointer);
                return pointer;
            }

            WriteOption(options, 0, new PerConnOption { Option = PerConnFlags, Value = state.Flags });
            WriteOption(options, 1, new PerConnOption { Option = PerConnProxyServer, Value = String(state.ProxyServer) });
            WriteOption(options, 2, new PerConnOption { Option = PerConnProxyBypass, Value = String(state.ProxyBypass) });
            WriteOption(options, 3, new PerConnOption { Option = PerConnAutoConfigUrl, Value = String(state.AutoConfigUrl) });

            Marshal.StructureToPtr(
                new PerConnOptionList
                {
                    Size = Marshal.SizeOf<PerConnOptionList>(),
                    Connection = IntPtr.Zero,
                    OptionCount = 4,
                    Options = options,
                },
                list,
                fDeleteOld: false);

            if (!InternetSetOption(IntPtr.Zero, InternetOptionPerConnectionOption, list, Marshal.SizeOf<PerConnOptionList>()))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InternetSetOption failed.");
            }

            // Сообщаем программам об изменении, иначе они применят его только после перезапуска.
            InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        finally
        {
            foreach (var pointer in strings.Where(p => p != IntPtr.Zero))
            {
                Marshal.FreeHGlobal(pointer);
            }

            Marshal.FreeHGlobal(options);
            Marshal.FreeHGlobal(list);
        }
    }

    private static (int Flags, string? Server, string? Bypass, string? AutoConfigUrl)? Query(int flagsOption)
    {
        int[] optionIds = [flagsOption, PerConnProxyServer, PerConnProxyBypass, PerConnAutoConfigUrl];
        var optionSize = Marshal.SizeOf<PerConnOption>();
        var options = Marshal.AllocHGlobal(optionSize * optionIds.Length);
        var list = Marshal.AllocHGlobal(Marshal.SizeOf<PerConnOptionList>());
        try
        {
            for (var i = 0; i < optionIds.Length; i++)
            {
                WriteOption(options, i, new PerConnOption { Option = optionIds[i] });
            }

            Marshal.StructureToPtr(
                new PerConnOptionList
                {
                    Size = Marshal.SizeOf<PerConnOptionList>(),
                    Connection = IntPtr.Zero,
                    OptionCount = optionIds.Length,
                    Options = options,
                },
                list,
                fDeleteOld: false);

            var size = Marshal.SizeOf<PerConnOptionList>();
            if (!InternetQueryOption(IntPtr.Zero, InternetOptionPerConnectionOption, list, ref size))
            {
                return null;
            }

            var result = new PerConnOption[optionIds.Length];
            for (var i = 0; i < optionIds.Length; i++)
            {
                result[i] = Marshal.PtrToStructure<PerConnOption>(options + (i * optionSize));
            }

            // Строки выделены WinINet через GlobalAlloc — освобождаем GlobalFree.
            string? TakeString(IntPtr pointer)
            {
                if (pointer == IntPtr.Zero)
                {
                    return null;
                }

                var value = Marshal.PtrToStringUni(pointer);
                GlobalFree(pointer);
                return value;
            }

            // Флаги — DWORD в младших 4 байтах объединения.
            return (unchecked((int)result[0].Value.ToInt64()), TakeString(result[1].Value), TakeString(result[2].Value), TakeString(result[3].Value));
        }
        finally
        {
            Marshal.FreeHGlobal(options);
            Marshal.FreeHGlobal(list);
        }
    }

    private static void WriteOption(IntPtr options, int index, PerConnOption option) =>
        Marshal.StructureToPtr(option, options + (index * Marshal.SizeOf<PerConnOption>()), fDeleteOld: false);

    [LibraryImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetQueryOption(IntPtr internet, int option, IntPtr buffer, ref int length);

    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalFree(IntPtr memory);

    // INTERNET_PER_CONN_OPTION: DWORD dwOption + union { DWORD; LPWSTR; FILETIME }. Раскладка для x64/ARM64.
    [StructLayout(LayoutKind.Sequential)]
    private struct PerConnOption
    {
        public int Option;
        public IntPtr Value;
    }

    // INTERNET_PER_CONN_OPTION_LIST.
    [StructLayout(LayoutKind.Sequential)]
    private struct PerConnOptionList
    {
        public int Size;
        public IntPtr Connection;
        public int OptionCount;
        public int OptionError;
        public IntPtr Options;
    }
}
