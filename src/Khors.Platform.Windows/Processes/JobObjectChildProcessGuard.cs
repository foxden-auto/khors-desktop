using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Khors.Platform.Windows.Processes;

/// <summary>
/// Job Object с флагом JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Дескриптор задания живёт, пока жив процесс KHORS;
/// когда процесс завершается (штатно, по исключению или через kill), Windows закрывает дескриптор
/// и завершает все процессы задания.
/// </summary>
public sealed partial class JobObjectChildProcessGuard : IChildProcessGuard
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeJobHandle _job;

    public JobObjectChildProcessGuard()
    {
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed.");
        }

        var info = new JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };

        if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationData>()))
        {
            var error = Marshal.GetLastPInvokeError();
            _job.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject failed.");
        }
    }

    public void Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_job.IsClosed, this);

        if (!AssignProcessToJobObject(_job, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed.");
        }
    }

    /// <summary>Закрывает задание — привязанные процессы завершаются.</summary>
    public void Dispose() => _job.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JobObjectExtendedLimitInformationData info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
