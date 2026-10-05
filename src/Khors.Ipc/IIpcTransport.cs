namespace Khors.Ipc;

/// <summary>Клиент, подключившийся к службе. Имя пользователя не секретно и пишется в журнал службы.</summary>
/// <param name="ProcessId">Процесс клиента.</param>
/// <param name="UserName">Имя учётной записи клиента или <c>null</c>, если его не удалось определить.</param>
public sealed record IpcPeer(int ProcessId, string? UserName);

/// <summary>Принятое службой соединение.</summary>
public interface IIpcConnection : IAsyncDisposable
{
    Stream Stream { get; }

    IpcPeer Peer { get; }
}

/// <summary>
/// Серверная сторона транспорта (служба). Доступ — только разрешённым локальным пользователям; проверку выполняет
/// реализация (Windows — ACL named pipe, Linux — права Unix-сокета). Реализации — в Khors.Platform.*.
/// </summary>
public interface IIpcServerTransport : IAsyncDisposable
{
    /// <summary>Ждёт следующего клиента.</summary>
    Task<IIpcConnection> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Клиентская сторона транспорта (UI). Реализация проверяет, что на другом конце — служба KHORS,
/// а не процесс, занявший имя канала раньше неё.
/// </summary>
public interface IIpcClientTransport
{
    /// <summary>Подключается к службе. Служба не запущена или не отвечает — <see cref="TimeoutException"/>.</summary>
    Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
