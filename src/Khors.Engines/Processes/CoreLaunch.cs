using System.Net;

namespace Khors.Engines.Processes;

/// <summary>Как запустить ядро и как понять, что оно готово принимать соединения.</summary>
/// <param name="StandardInput">Конфиг, передаваемый через stdin, — так секреты не попадают на диск.</param>
/// <param name="ReadinessEndpoint">Локальный вход ядра: готовность — когда он принимает TCP-соединения.</param>
public sealed record CoreLaunch(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? StandardInput,
    IPEndPoint ReadinessEndpoint,
    TimeSpan ReadyTimeout)
{
    /// <summary>Какие строки лога записывать в буфер (до маскировки); <c>null</c> — все.</summary>
    public Func<string, bool>? KeepLine { get; init; }

    /// <summary>Дополнительные переменные окружения процесса ядра.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    // Конфиг содержит секреты — не выводим.
    public override string ToString() => $"CoreLaunch {{ {Path.GetFileName(ExecutablePath)} {string.Join(' ', Arguments)}, Ready = {ReadinessEndpoint} }}";
}

/// <summary>Завершение процесса ядра.</summary>
/// <param name="Expected"><c>true</c> — остановлен по запросу; <c>false</c> — упал или был завершён извне.</param>
public sealed record CoreExit(int ExitCode, bool Expected, DateTimeOffset Time);
