using Khors.Core.Generators;

namespace Khors.Engines.Processes;

public enum CoreStartFailure
{
    /// <summary>Исполняемый файл ядра не найден.</summary>
    ExecutableNotFound,

    /// <summary>Конфиг не построен: профиль невалиден или ядро не поддерживает его возможности.</summary>
    ConfigNotGenerated,

    /// <summary>Ядро завершилось во время запуска (обычно — отвергло конфиг или порт занят).</summary>
    ExitedDuringStart,

    /// <summary>Ядро запущено, но локальный вход не начал принимать соединения вовремя.</summary>
    ReadyTimeout,
}

/// <summary>Ядро не запустилось. Сообщение и хвост лога не содержат секретов.</summary>
public sealed class CoreStartException : Exception
{
    public CoreStartException()
    {
    }

    public CoreStartException(string message)
        : base(message)
    {
    }

    public CoreStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CoreStartException(CoreStartFailure failure, string message, int? exitCode = null, IReadOnlyList<string>? logTail = null, string? field = null)
        : base(message)
    {
        Failure = failure;
        ExitCode = exitCode;
        LogTail = logTail ?? [];
        Field = field;
    }

    public CoreStartFailure Failure { get; }

    public int? ExitCode { get; }

    /// <summary>Последние строки лога ядра (замаскированные).</summary>
    public IReadOnlyList<string> LogTail { get; } = [];

    /// <summary>Поле профиля для <see cref="CoreStartFailure.ConfigNotGenerated"/>.</summary>
    public string? Field { get; }

    /// <summary>Причина <see cref="CoreStartFailure.ConfigNotGenerated"/>.</summary>
    public CoreConfigError? ConfigError { get; init; }

    /// <summary>Ядро, которое не запустилось.</summary>
    public CoreKind? Core { get; init; }
}
