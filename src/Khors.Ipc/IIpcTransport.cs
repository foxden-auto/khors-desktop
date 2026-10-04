namespace Khors.Ipc;

/// <summary>
/// Транспорт IPC между UI и службой: подключённый поток и проверка, что клиент —
/// разрешённый локальный пользователь. Реализации — в Khors.Platform.*
/// (Windows — named pipe с ACL, Linux — Unix-сокет). Методы — в ROADMAP 3.1.
/// </summary>
public interface IIpcTransport;
