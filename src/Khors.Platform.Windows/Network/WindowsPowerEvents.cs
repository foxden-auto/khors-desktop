using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Khors.Platform.Windows.Network;

/// <summary>
/// Пробуждение после сна: <c>PowerRegisterSuspendResumeNotification</c> с обратным вызовом — без окна и цикла
/// сообщений. Windows присылает <c>PBT_APMRESUMEAUTOMATIC</c> при любом пробуждении.
/// </summary>
public sealed partial class WindowsPowerEvents : IPowerEvents
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtApmResumeAutomatic = 0x12;

    private readonly GCHandle _self;
    private IntPtr _registration;

    public unsafe WindowsPowerEvents()
    {
        _self = GCHandle.Alloc(this);
        var parameters = new DeviceNotifySubscribeParameters
        {
            Callback = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint>)&OnPowerEvent,
            Context = GCHandle.ToIntPtr(_self),
        };
        var error = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref parameters, out _registration);
        if (error != 0)
        {
            _self.Free();
            throw new Win32Exception((int)error, "PowerRegisterSuspendResumeNotification failed.");
        }
    }

    public event EventHandler? Resumed;

    public void Dispose()
    {
        var registration = Interlocked.Exchange(ref _registration, IntPtr.Zero);
        if (registration == IntPtr.Zero)
        {
            return;
        }

        _ = PowerUnregisterSuspendResumeNotification(registration);
        _self.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnPowerEvent(IntPtr context, uint type, IntPtr setting)
    {
        if (type == PbtApmResumeAutomatic && GCHandle.FromIntPtr(context).Target is WindowsPowerEvents events)
        {
            // Обратный вызов — в потоке Windows: подписчиков зовём из пула и сразу возвращаем управление.
            ThreadPool.QueueUserWorkItem(static e => e.Resumed?.Invoke(e, EventArgs.Empty), events, preferLocal: false);
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerRegisterSuspendResumeNotification(uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr registrationHandle);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
}
