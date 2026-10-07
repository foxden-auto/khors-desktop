using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using static Khors.Platform.Windows.KillSwitch.WfpNative;

namespace Khors.Platform.Windows.KillSwitch;

/// <summary>
/// Kill Switch на Windows Filtering Platform (docs/SPEC.md, 4.6). Свои провайдер и sublayer «KHORS» (постоянные,
/// чтобы к ним могли относиться и постоянные фильтры), фильтры — на уровнях ALE connect IPv4/IPv6.
/// Временные фильтры добавляются в динамической сессии: Windows снимает их сама, когда процесс службы завершается.
/// Постоянные — в обычной сессии с флагом PERSISTENT: живут до явного снятия (<see cref="Remove"/>).
/// Смена набора — новый набор одной транзакцией, затем старый одной транзакцией: между ними трафик не проходит.
/// Работает только с правами SYSTEM или администратора.
/// </summary>
internal sealed class WfpKillSwitch : IKillSwitch, IDisposable
{
    public static readonly Guid ProviderKey = new("6b0e4f8a-3c1d-4b7e-9a52-4b484f525301");
    public static readonly Guid SubLayerKey = new("6b0e4f8a-3c1d-4b7e-9a52-4b484f525302");

    private const ushort SubLayerWeight = 0xFFFF;

    private static readonly Guid[] s_layers = [LayerAleAuthConnectV4, LayerAleAuthConnectV6];

    private readonly Lock _lock = new();
    private IntPtr _dynamic;
    private IntPtr _static;

    public void Apply(KillSwitchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_lock)
        {
            EnsureProviderAndSubLayer();
            var luid = policy.TunnelInterface is { } name && ConvertInterfaceAliasToLuid(name, out var value) == 0 ? value : (ulong?)null;
            var filters = KillSwitchFilters.Build(policy, luid);
            var old = Enumerate().Select(f => f.Key).ToList();

            var engine = policy.Persistent ? Static : Dynamic;
            InTransaction(engine, () =>
            {
                foreach (var filter in filters)
                {
                    AddFilter(engine, filter, policy.Persistent);
                }
            });

            InTransaction(Dynamic, () =>
            {
                foreach (var key in old)
                {
                    Check(FwpmFilterDeleteByKey0(Dynamic, key), "FwpmFilterDeleteByKey0", ErrorFilterNotFound);
                }
            });
        }
    }

    public void Remove()
    {
        lock (_lock)
        {
            var keys = Enumerate().Select(f => f.Key).ToList();
            InTransaction(Dynamic, () =>
            {
                foreach (var key in keys)
                {
                    Check(FwpmFilterDeleteByKey0(Dynamic, key), "FwpmFilterDeleteByKey0", ErrorFilterNotFound);
                }
            });

            // Без фильтров не нужны и служебные объекты: после удаления программы в WFP ничего не остаётся.
            Check(FwpmSubLayerDeleteByKey0(Static, SubLayerKey), "FwpmSubLayerDeleteByKey0", ErrorSubLayerNotFound, ErrorInUse);
            Check(FwpmProviderDeleteByKey0(Static, ProviderKey), "FwpmProviderDeleteByKey0", ErrorProviderNotFound, ErrorInUse);
        }
    }

    public bool HasPersistentFilters()
    {
        lock (_lock)
        {
            return Enumerate().Any(f => (f.Flags & FilterFlagPersistent) != 0);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            // Закрытие динамической сессии снимает её фильтры — так задумано для режима «пока подключено».
            Close(ref _dynamic);
            Close(ref _static);
        }
    }

    private IntPtr Dynamic => _dynamic != IntPtr.Zero ? _dynamic : _dynamic = Open(dynamic: true);

    private IntPtr Static => _static != IntPtr.Zero ? _static : _static = Open(dynamic: false);

    private static IntPtr Open(bool dynamic)
    {
        var session = new FwpmSession { Flags = dynamic ? SessionFlagDynamic : 0 };
        Check(FwpmEngineOpen0(IntPtr.Zero, RpcAuthnWinNt, IntPtr.Zero, session, out var handle), "FwpmEngineOpen0");
        return handle;
    }

    private static void Close(ref IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            // Закрываем при остановке — исправить ошибку закрытия уже нечем.
            _ = FwpmEngineClose0(handle);
            handle = IntPtr.Zero;
        }
    }

    private void EnsureProviderAndSubLayer()
    {
        using var arena = new NativeArena();
        var provider = new FwpmProvider
        {
            ProviderKey = ProviderKey,
            Name = arena.String("KHORS"),
            Description = arena.String("KHORS Kill Switch"),
            Flags = ProviderFlagPersistent,
        };
        Check(FwpmProviderAdd0(Static, provider, IntPtr.Zero), "FwpmProviderAdd0", ErrorAlreadyExists);

        var subLayer = new FwpmSubLayer
        {
            SubLayerKey = SubLayerKey,
            Name = arena.String("KHORS"),
            Description = arena.String("KHORS Kill Switch"),
            Flags = SubLayerFlagPersistent,
            ProviderKey = arena.Struct(ProviderKey),
            Weight = SubLayerWeight,
        };
        Check(FwpmSubLayerAdd0(Static, subLayer, IntPtr.Zero), "FwpmSubLayerAdd0", ErrorAlreadyExists);
    }

    private static void AddFilter(IntPtr engine, WfpFilterSpec spec, bool persistent)
    {
        using var arena = new NativeArena();
        var conditions = new List<FwpmFilterCondition>();
        foreach (var condition in spec.Conditions)
        {
            if (!AddConditions(conditions, condition, arena))
            {
                // Программы нет на диске — разрешать некому.
                return;
            }
        }

        var array = arena.Array(conditions);
        var filter = new FwpmFilter
        {
            FilterKey = Guid.NewGuid(),
            Name = arena.String(spec.Name),
            Description = arena.String("KHORS Kill Switch"),
            Flags = persistent ? FilterFlagPersistent : 0,
            ProviderKey = arena.Struct(ProviderKey),
            LayerKey = spec.Layer == WfpLayer.ConnectV4 ? LayerAleAuthConnectV4 : LayerAleAuthConnectV6,
            SubLayerKey = SubLayerKey,
            Weight = new FwpValue { Type = DataUInt8, Value = spec.Weight },
            NumFilterConditions = (uint)conditions.Count,
            FilterCondition = array,
            Action = new FwpmAction { Type = spec.Permit ? ActionPermit : ActionBlock },
        };
        Check(FwpmFilterAdd0(engine, filter, IntPtr.Zero, out _), "FwpmFilterAdd0");
    }

    private static bool AddConditions(List<FwpmFilterCondition> conditions, WfpCondition condition, NativeArena arena)
    {
        switch (condition)
        {
            case LoopbackCondition:
                conditions.Add(Condition(ConditionFlags, MatchFlagsAllSet, DataUInt32, ConditionFlagIsLoopback));
                return true;
            case InterfaceCondition(var luid):
                conditions.Add(Condition(ConditionIpLocalInterface, MatchEqual, DataUInt64, (ulong)arena.Struct(luid)));
                return true;
            case ApplicationCondition(var path):
                if (!File.Exists(path) || FwpmGetAppIdFromFileName0(path, out var appId) != 0)
                {
                    return false;
                }

                arena.TrackWfp(appId);
                conditions.Add(Condition(ConditionAleAppId, MatchEqual, DataByteBlob, (ulong)appId));
                return true;
            case RemoteNetworkCondition(var network) when network.BaseAddress.AddressFamily == AddressFamily.InterNetwork:
                conditions.Add(Condition(ConditionIpRemoteAddress, MatchEqual, DataV4AddrMask, (ulong)arena.Struct(V4(network))));
                return true;
            case RemoteNetworkCondition(var network):
                conditions.Add(Condition(ConditionIpRemoteAddress, MatchEqual, DataV6AddrMask, (ulong)arena.Struct(V6(network))));
                return true;
            case UdpRemotePortCondition(var port):
                conditions.Add(Condition(ConditionIpProtocol, MatchEqual, DataUInt8, ProtocolUdp));
                conditions.Add(Condition(ConditionIpRemotePort, MatchEqual, DataUInt16, port));
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(condition), condition, null);
        }
    }

    private static FwpmFilterCondition Condition(Guid field, uint match, uint type, ulong value) =>
        new() { FieldKey = field, MatchType = match, ConditionValue = new FwpValue { Type = type, Value = value } };

    /// <summary>IPv4 в WFP — в порядке байтов хоста.</summary>
    internal static FwpV4AddrAndMask V4(IPNetwork network)
    {
        var bytes = network.BaseAddress.GetAddressBytes();
        var address = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
        var mask = network.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - network.PrefixLength);
        return new FwpV4AddrAndMask { Address = address, Mask = mask };
    }

    private static unsafe FwpV6AddrAndMask V6(IPNetwork network)
    {
        var value = new FwpV6AddrAndMask { PrefixLength = (byte)network.PrefixLength };
        var bytes = network.BaseAddress.GetAddressBytes();
        for (var i = 0; i < 16; i++)
        {
            value.Address[i] = bytes[i];
        }

        return value;
    }

    /// <summary>Фильтры с провайдером KHORS на наших уровнях: ключ и флаги.</summary>
    private List<(Guid Key, uint Flags)> Enumerate()
    {
        var result = new List<(Guid, uint)>();
        using var arena = new NativeArena();
        var providerKey = arena.Struct(ProviderKey);
        var flagsOffset = (int)Marshal.OffsetOf<FwpmFilter>(nameof(FwpmFilter.Flags));
        foreach (var layer in s_layers)
        {
            var template = new FwpmFilterEnumTemplate { ProviderKey = providerKey, LayerKey = layer, EnumType = 1, ActionMask = uint.MaxValue };
            Check(FwpmFilterCreateEnumHandle0(Dynamic, template, out var enumHandle), "FwpmFilterCreateEnumHandle0");
            try
            {
                while (true)
                {
                    Check(FwpmFilterEnum0(Dynamic, enumHandle, 256, out var entries, out var count), "FwpmFilterEnum0");
                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            var entry = Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                            result.Add((Marshal.PtrToStructure<Guid>(entry), (uint)Marshal.ReadInt32(entry, flagsOffset)));
                        }
                    }
                    finally
                    {
                        if (entries != IntPtr.Zero)
                        {
                            FwpmFreeMemory0(ref entries);
                        }
                    }

                    if (count < 256)
                    {
                        break;
                    }
                }
            }
            finally
            {
                _ = FwpmFilterDestroyEnumHandle0(Dynamic, enumHandle);
            }
        }

        return result;
    }

    private static void InTransaction(IntPtr engine, Action body)
    {
        Check(FwpmTransactionBegin0(engine, 0), "FwpmTransactionBegin0");
        try
        {
            body();
            Check(FwpmTransactionCommit0(engine), "FwpmTransactionCommit0");
        }
        catch
        {
            // Исходная ошибка важнее ошибки отмены: незавершённую транзакцию WFP всё равно отменит при закрытии сессии.
            _ = FwpmTransactionAbort0(engine);
            throw;
        }
    }

    private static void Check(uint code, string function, params uint[] allowed)
    {
        if (code != 0 && !allowed.Contains(code))
        {
            throw new Win32Exception(unchecked((int)code), $"{function} failed: 0x{code:X8}.");
        }
    }

    /// <summary>Неуправляемая память на время одного вызова WFP.</summary>
    private sealed class NativeArena : IDisposable
    {
        private readonly List<IntPtr> _hglobal = [];
        private readonly List<IntPtr> _wfp = [];

        public IntPtr String(string value) => Keep(Marshal.StringToHGlobalUni(value));

        public IntPtr Struct<T>(T value)
            where T : struct
        {
            var pointer = Keep(Marshal.AllocHGlobal(Marshal.SizeOf<T>()));
            Marshal.StructureToPtr(value, pointer, fDeleteOld: false);
            return pointer;
        }

        public IntPtr Array<T>(List<T> values)
            where T : struct
        {
            if (values.Count == 0)
            {
                return IntPtr.Zero;
            }

            var size = Marshal.SizeOf<T>();
            var pointer = Keep(Marshal.AllocHGlobal(size * values.Count));
            for (var i = 0; i < values.Count; i++)
            {
                Marshal.StructureToPtr(values[i], pointer + (i * size), fDeleteOld: false);
            }

            return pointer;
        }

        public void TrackWfp(IntPtr pointer) => _wfp.Add(pointer);

        public void Dispose()
        {
            foreach (var pointer in _hglobal)
            {
                Marshal.FreeHGlobal(pointer);
            }

            for (var i = 0; i < _wfp.Count; i++)
            {
                var pointer = _wfp[i];
                FwpmFreeMemory0(ref pointer);
            }
        }

        private IntPtr Keep(IntPtr pointer)
        {
            _hglobal.Add(pointer);
            return pointer;
        }
    }
}
