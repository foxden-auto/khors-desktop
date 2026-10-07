using System.Runtime.InteropServices;

namespace Khors.Platform.Windows.KillSwitch;

/// <summary>
/// Объявления Windows Filtering Platform (fwpuclnt.dll) и IP Helper (iphlpapi.dll) — по документации Windows SDK
/// (fwpmu.h, fwpmtypes.h, fwptypes.h). Раскладка структур — как у x64/ARM64: указатели по 8 байт.
/// </summary>
internal static partial class WfpNative
{
    public const uint RpcAuthnWinNt = 10;

    public const uint SessionFlagDynamic = 0x1;
    public const uint ProviderFlagPersistent = 0x1;
    public const ushort SubLayerFlagPersistent = 0x1;
    public const uint FilterFlagPersistent = 0x1;

    public const uint ActionBlock = 0x1 | 0x1000;
    public const uint ActionPermit = 0x2 | 0x1000;

    public const uint MatchEqual = 0;
    public const uint MatchFlagsAllSet = 6;

    public const uint DataUInt8 = 1;
    public const uint DataUInt16 = 2;
    public const uint DataUInt32 = 3;
    public const uint DataUInt64 = 4;
    public const uint DataByteBlob = 12;
    public const uint DataV4AddrMask = 0x100;
    public const uint DataV6AddrMask = 0x101;

    public const uint ConditionFlagIsLoopback = 0x1;
    public const byte ProtocolUdp = 17;

    public const uint ErrorFilterNotFound = 0x80320003;
    public const uint ErrorProviderNotFound = 0x80320005;
    public const uint ErrorSubLayerNotFound = 0x80320007;
    public const uint ErrorAlreadyExists = 0x80320009;
    public const uint ErrorInUse = 0x8032000A;

    public static readonly Guid LayerAleAuthConnectV4 = new(0xc38d57d1, 0x05a7, 0x4c33, 0x90, 0x4f, 0x7f, 0xbc, 0xee, 0xe6, 0x0e, 0x82);
    public static readonly Guid LayerAleAuthConnectV6 = new(0x4a72393b, 0x319f, 0x44bc, 0x84, 0xc3, 0xba, 0x54, 0xdc, 0xb3, 0xb6, 0xb4);

    public static readonly Guid ConditionAleAppId = new(0xd78e1e87, 0x8644, 0x4ea5, 0x94, 0x37, 0xd8, 0x09, 0xec, 0xef, 0xc9, 0x71);
    public static readonly Guid ConditionIpLocalInterface = new(0x4cd62a49, 0x59c3, 0x4969, 0xb7, 0xf3, 0xbd, 0xa5, 0xd3, 0x28, 0x90, 0xa4);
    public static readonly Guid ConditionIpRemoteAddress = new(0xb235ae9a, 0x1d64, 0x49b8, 0xa4, 0x4c, 0x5f, 0xf3, 0xd9, 0x09, 0x50, 0x45);
    public static readonly Guid ConditionIpRemotePort = new(0xc35a604d, 0xd22b, 0x4e1a, 0x91, 0xb4, 0x68, 0xf6, 0x74, 0xee, 0x67, 0x4b);
    public static readonly Guid ConditionIpProtocol = new(0x3971ef2b, 0x623e, 0x4f9a, 0x8c, 0xb1, 0x6e, 0x79, 0xb8, 0x06, 0xb9, 0xa7);
    public static readonly Guid ConditionFlags = new(0x632ce23b, 0x5167, 0x435c, 0x86, 0xd7, 0xe9, 0x03, 0x68, 0x4a, 0xa8, 0x0c);

    [StructLayout(LayoutKind.Sequential)]
    public struct FwpByteBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    /// <summary>FWP_VALUE0 и FWP_CONDITION_VALUE0: тип и 8 байт значения (число или указатель).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FwpValue
    {
        public uint Type;
        public ulong Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FwpmSession
    {
        public Guid SessionKey;
        public IntPtr Name;
        public IntPtr Description;
        public uint Flags;
        public uint TxnWaitTimeoutInMSec;
        public uint ProcessId;
        public IntPtr Sid;
        public IntPtr Username;
        public int KernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FwpmProvider
    {
        public Guid ProviderKey;
        public IntPtr Name;
        public IntPtr Description;
        public uint Flags;
        public FwpByteBlob ProviderData;
        public IntPtr ServiceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FwpmSubLayer
    {
        public Guid SubLayerKey;
        public IntPtr Name;
        public IntPtr Description;
        public ushort Flags;
        public IntPtr ProviderKey;
        public FwpByteBlob ProviderData;
        public ushort Weight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FwpmAction
    {
        public uint Type;
        public Guid FilterType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FwpmFilterCondition
    {
        public Guid FieldKey;
        public uint MatchType;
        public FwpValue ConditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FwpmFilter
    {
        public Guid FilterKey;
        public IntPtr Name;
        public IntPtr Description;
        public uint Flags;
        public IntPtr ProviderKey;
        public FwpByteBlob ProviderData;
        public Guid LayerKey;
        public Guid SubLayerKey;
        public FwpValue Weight;
        public uint NumFilterConditions;
        public IntPtr FilterCondition;
        public FwpmAction Action;

        // union { UINT64 rawContext; GUID providerContextKey; } — 16 байт, выравнивание 8.
        public ulong ProviderContextLow;
        public ulong ProviderContextHigh;
        public IntPtr Reserved;
        public ulong FilterId;
        public FwpValue EffectiveWeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FwpmFilterEnumTemplate
    {
        public IntPtr ProviderKey;
        public Guid LayerKey;
        public uint EnumType;
        public uint Flags;
        public IntPtr ProviderContextTemplate;
        public uint NumFilterConditions;
        public IntPtr FilterCondition;
        public uint ActionMask;
        public IntPtr CalloutKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FwpV4AddrAndMask
    {
        public uint Address;
        public uint Mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FwpV6AddrAndMask
    {
        public fixed byte Address[16];
        public byte PrefixLength;
    }

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmEngineOpen0(IntPtr serverName, uint authnService, IntPtr authIdentity, in FwpmSession session, out IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmEngineClose0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmTransactionCommit0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmTransactionAbort0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmProviderAdd0(IntPtr engineHandle, in FwpmProvider provider, IntPtr securityDescriptor);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmProviderDeleteByKey0(IntPtr engineHandle, in Guid key);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmSubLayerAdd0(IntPtr engineHandle, in FwpmSubLayer subLayer, IntPtr securityDescriptor);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, in Guid key);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmFilterAdd0(IntPtr engineHandle, in FwpmFilter filter, IntPtr securityDescriptor, out ulong id);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmFilterDeleteByKey0(IntPtr engineHandle, in Guid key);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmFilterCreateEnumHandle0(IntPtr engineHandle, in FwpmFilterEnumTemplate enumTemplate, out IntPtr enumHandle);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmFilterEnum0(IntPtr engineHandle, IntPtr enumHandle, uint numEntriesRequested, out IntPtr entries, out uint numEntriesReturned);

    [LibraryImport("fwpuclnt.dll")]
    public static partial uint FwpmFilterDestroyEnumHandle0(IntPtr engineHandle, IntPtr enumHandle);

    [LibraryImport("fwpuclnt.dll")]
    public static partial void FwpmFreeMemory0(ref IntPtr p);

    [LibraryImport("fwpuclnt.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [LibraryImport("iphlpapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ConvertInterfaceAliasToLuid(string interfaceAlias, out ulong interfaceLuid);
}
