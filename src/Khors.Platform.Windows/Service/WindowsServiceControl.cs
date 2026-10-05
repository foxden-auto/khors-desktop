using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;

namespace Khors.Platform.Windows.Service;

/// <summary>
/// Служба Windows <c>KhorsService</c>: автозапуск, перезапуск после сбоя. Файлы службы и ядер копируются в
/// <c>%ProgramFiles%\KHORS\service</c> (писать туда может только администратор) и регистрируются оттуда.
/// </summary>
public sealed partial class WindowsServiceControl : IServiceControl
{
    public const string ServiceName = "KhorsService";
    public const string ExecutableName = "khors-service.exe";
    private const string DisplayName = "KHORS Service";
    private const string Description = "KHORS Desktop: TUN mode and network protection.";
    private const string CoresDirectoryName = "cores";

    private const int ErrorCancelled = 1223;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;
    private static readonly TimeSpan s_statusTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Каталог установленной службы: <c>%ProgramFiles%\KHORS\service</c>.</summary>
    public static string InstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KHORS", "service");

    public ServiceState GetState()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ServiceState.Starting,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending or ServiceControllerStatus.Paused => ServiceState.Stopping,
                _ => ServiceState.Unknown,
            };
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: ErrorServiceDoesNotExist })
        {
            return ServiceState.NotInstalled;
        }
        catch (InvalidOperationException)
        {
            return ServiceState.Unknown;
        }
    }

    public int? GetProcessId()
    {
        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            return null;
        }

        using var service = OpenService(manager, ServiceName, ServiceQueryStatus);
        if (service.IsInvalid)
        {
            return null;
        }

        var status = default(ServiceStatusProcess);
        return QueryServiceStatusEx(service, ScStatusProcessInfo, ref status, Marshal.SizeOf<ServiceStatusProcess>(), out _)
            && status.CurrentState == ServiceRunning
            && status.ProcessId != 0
                ? (int)status.ProcessId
                : null;
    }

    public void Install(string sourceDirectory)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        var source = Path.GetFullPath(sourceDirectory);
        if (!File.Exists(Path.Combine(source, ExecutableName)))
        {
            throw new FileNotFoundException("KHORS service executable not found.", Path.Combine(source, ExecutableName));
        }

        StopIfRunning();
        if (!string.Equals(source.TrimEnd('\\'), InstallDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            CopyServiceFiles(source, InstallDirectory);
        }

        var binaryPath = "\"" + Path.Combine(InstallDirectory, ExecutableName) + "\"";
        using (var manager = OpenManagerForChanges())
        {
            using var existing = OpenService(manager, ServiceName, ServiceAllAccess);
            if (existing.IsInvalid)
            {
                using var created = CreateService(
                    manager, ServiceName, DisplayName, ServiceAllAccess, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal,
                    binaryPath, null, IntPtr.Zero, null, null, null);
                ThrowIfInvalid(created, "CreateService");
                Configure(created);
            }
            else
            {
                Check(ChangeServiceConfig(
                    existing, ServiceNoChange, ServiceAutoStart, ServiceNoChange, binaryPath, null, IntPtr.Zero, null, null, null, DisplayName), "ChangeServiceConfig");
                Configure(existing);
            }
        }

        using var controller = new ServiceController(ServiceName);
        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, s_statusTimeout);
    }

    public void Uninstall()
    {
        StopIfRunning();
        using (var manager = OpenManagerForChanges())
        using (var service = OpenService(manager, ServiceName, ServiceDelete))
        {
            if (!service.IsInvalid && !DeleteService(service) && Marshal.GetLastPInvokeError() != ErrorServiceMarkedForDelete)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "DeleteService failed.");
            }
        }

        // Процесс службы мог ещё не освободить файлы — несколько попыток.
        DeleteWithRetry(InstallDirectory);
        var parent = Path.GetDirectoryName(InstallDirectory)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
        }
    }

    public async Task<ServiceSetupResult> RunElevatedSetupAsync(ServiceSetupAction action, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        if (!File.Exists(executable) && action == ServiceSetupAction.Uninstall && File.Exists(Path.Combine(InstallDirectory, ExecutableName)))
        {
            // Удаление из установленной копии: она удалит свой каталог, поэтому запускается из временной папки.
            executable = Path.Combine(Path.GetTempPath(), "khors-service-uninstall.exe");
            File.Copy(Path.Combine(InstallDirectory, ExecutableName), executable, overwrite: true);
        }

        if (!File.Exists(executable))
        {
            return ServiceSetupResult.SetupNotFound;
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = action == ServiceSetupAction.Install ? "install" : "uninstall",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return ServiceSetupResult.Cancelled;
        }

        if (process is null)
        {
            return ServiceSetupResult.Failed;
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? ServiceSetupResult.Succeeded : ServiceSetupResult.Failed;
        }
    }

    private static void StopIfRunning()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status is not ServiceControllerStatus.Stopped)
            {
                if (controller.Status is not ServiceControllerStatus.StopPending)
                {
                    controller.Stop();
                }

                controller.WaitForStatus(ServiceControllerStatus.Stopped, s_statusTimeout);
            }
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: ErrorServiceDoesNotExist })
        {
            // Службы нет — останавливать нечего.
        }
    }

    /// <summary>Исполняемый файл службы и ядра (только каталоги ядер из поставки).</summary>
    private static void CopyServiceFiles(string source, string target)
    {
        Directory.CreateDirectory(target);
        CopyWithRetry(Path.Combine(source, ExecutableName), Path.Combine(target, ExecutableName));

        var cores = Path.Combine(source, CoresDirectoryName);
        if (!Directory.Exists(cores))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(cores, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, CoresDirectoryName, Path.GetRelativePath(cores, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyWithRetry(file, destination);
        }
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void DeleteWithRetry(string directory)
    {
        for (var attempt = 1; Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void Configure(SafeServiceHandle service)
    {
        var description = new ServiceDescriptionInfo { Description = Marshal.StringToHGlobalUni(Description) };
        try
        {
            Check(ChangeServiceConfig2(service, ServiceConfigDescription, ref description), "ChangeServiceConfig2(description)");
        }
        finally
        {
            Marshal.FreeHGlobal(description.Description);
        }

        // Перезапуск после сбоя: через 5 с, трижды; счётчик сбрасывается через сутки.
        var actions = new ScAction[3];
        for (var i = 0; i < actions.Length; i++)
        {
            actions[i] = new ScAction { Type = ScActionRestart, Delay = 5000 };
        }

        var handle = GCHandle.Alloc(actions, GCHandleType.Pinned);
        try
        {
            var failure = new ServiceFailureActions
            {
                ResetPeriod = 24 * 60 * 60,
                ActionCount = (uint)actions.Length,
                Actions = handle.AddrOfPinnedObject(),
            };
            Check(ChangeServiceConfig2(service, ServiceConfigFailureActions, ref failure), "ChangeServiceConfig2(failure actions)");
        }
        finally
        {
            handle.Free();
        }
    }

    private static SafeServiceHandle OpenManagerForChanges()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
        ThrowIfInvalid(manager, "OpenSCManager");
        return manager;
    }

    private static void ThrowIfInvalid(SafeServiceHandle handle, string operation)
    {
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, operation + " failed.");
        }
    }

    private static void Check(bool ok, string operation)
    {
        if (!ok)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), operation + " failed.");
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceDelete = 0x10000;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint ServiceAutoStart = 0x2;
    private const uint ServiceErrorNormal = 0x1;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceRunning = 0x4;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceConfigDescription = 1;
    private const uint ServiceConfigFailureActions = 2;
    private const int ScActionRestart = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescriptionInfo
    {
        public IntPtr Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public IntPtr RebootMessage;
        public IntPtr Command;
        public uint ActionCount;
        public IntPtr Actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public int Type;
        public uint Delay;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle OpenService(SafeServiceHandle manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeServiceHandle CreateService(
        SafeServiceHandle manager, string serviceName, string displayName, uint desiredAccess, uint serviceType, uint startType,
        uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig(
        SafeServiceHandle service, uint serviceType, uint startType, uint errorControl, string binaryPath, string? loadOrderGroup,
        IntPtr tagId, string? dependencies, string? account, string? password, string displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2(SafeServiceHandle service, uint infoLevel, ref ServiceDescriptionInfo info);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2(SafeServiceHandle service, uint infoLevel, ref ServiceFailureActions info);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(SafeServiceHandle service, int infoLevel, ref ServiceStatusProcess status, int bufferSize, out int bytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(SafeServiceHandle service);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
