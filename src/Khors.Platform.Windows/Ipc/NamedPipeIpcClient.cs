using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Khors.Ipc;

namespace Khors.Platform.Windows.Ipc;

/// <summary>
/// Клиент IPC на named pipe. Служба получает только право узнать, кто подключился (уровень Identification),
/// а не действовать от имени пользователя. После подключения проверяется процесс на другом конце канала
/// (<paramref name="isTrustedServer"/>): канал с тем же именем мог создать чужой процесс.
/// </summary>
public sealed partial class NamedPipeIpcClient(string pipeName, Func<int, bool> isTrustedServer) : IIpcClientTransport
{
    public async Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId) || !isTrustedServer((int)serverProcessId))
            {
                throw new UnauthorizedAccessException("The KHORS pipe is not served by the KHORS service.");
            }

            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint processId);
}
