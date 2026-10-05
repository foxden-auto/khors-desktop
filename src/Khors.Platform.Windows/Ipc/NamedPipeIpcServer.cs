using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Khors.Ipc;

namespace Khors.Platform.Windows.Ipc;

/// <summary>
/// Сервер IPC на named pipe (docs/SPEC.md, раздел 5). ACL канала: SYSTEM и администраторы — полный доступ,
/// интерактивные пользователи — чтение и запись (без права создавать экземпляры канала), сетевые клиенты — запрет.
/// Если ни одного нашего экземпляра нет, новый создаётся как первый (FILE_FLAG_FIRST_PIPE_INSTANCE): имя, занятое
/// чужим процессом, приводит к ошибке, а не к подключению к чужому каналу.
/// </summary>
public sealed partial class NamedPipeIpcServer : IIpcServerTransport
{
    /// <summary>Имя канала службы: <c>\\.\pipe\KHORS</c>.</summary>
    public const string ServicePipeName = "KHORS";

    private const int BufferSize = 64 * 1024;

    private readonly string _pipeName;
    private readonly PipeSecurity _security;
    private int _instances;

    public NamedPipeIpcServer()
        : this(ServicePipeName, ownerFullControl: null)
    {
    }

    /// <summary>
    /// Служба работает от SYSTEM — ACL как описано выше. Запуск из консоли под обычной учётной записью (разработка):
    /// той же учётной записи — полный доступ, иначе она не сможет создавать следующие экземпляры канала.
    /// </summary>
    public static NamedPipeIpcServer ForCurrentProcess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem ? new NamedPipeIpcServer() : new NamedPipeIpcServer(ServicePipeName, identity.User);
    }

    /// <param name="ownerFullControl">Дополнительно полный доступ этой учётной записи — для тестов без прав SYSTEM.</param>
    internal NamedPipeIpcServer(string pipeName, SecurityIdentifier? ownerFullControl)
    {
        _pipeName = pipeName;
        _security = CreateSecurity(ownerFullControl);
    }

    public async Task<IIpcConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        var pipe = CreateInstance();
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeInstanceAsync(pipe).ConfigureAwait(false);
            throw;
        }

        var processId = GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id) ? (int)id : 0;
        string? user;
        try
        {
            user = pipe.GetImpersonationUserName();
        }
        catch (IOException)
        {
            user = null;
        }

        return new Connection(this, pipe, new IpcPeer(processId, user));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static PipeSecurity CreateSecurity(SecurityIdentifier? ownerFullControl)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        if (ownerFullControl is not null)
        {
            security.AddAccessRule(new PipeAccessRule(ownerFullControl, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    private NamedPipeServerStream CreateInstance()
    {
        var first = Interlocked.Increment(ref _instances) == 1;
        try
        {
            return NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
                // С нулевыми буферами запись ждёт, пока другая сторона начнёт читать.
                inBufferSize: BufferSize,
                outBufferSize: BufferSize,
                _security);
        }
        catch
        {
            Interlocked.Decrement(ref _instances);
            throw;
        }
    }

    private async ValueTask DisposeInstanceAsync(NamedPipeServerStream pipe)
    {
        await pipe.DisposeAsync().ConfigureAwait(false);
        Interlocked.Decrement(ref _instances);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint processId);

    private sealed class Connection(NamedPipeIpcServer owner, NamedPipeServerStream pipe, IpcPeer peer) : IIpcConnection
    {
        public Stream Stream => pipe;

        public IpcPeer Peer { get; } = peer;

        public ValueTask DisposeAsync() => owner.DisposeInstanceAsync(pipe);
    }
}
