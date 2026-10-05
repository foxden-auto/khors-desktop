using Khors.Core.Generators;
using Khors.Core.Profiles;
using Khors.Engines.Processes;

namespace Khors.Engines.Connection;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,

    /// <summary>Подключение не удалось или оборвалось; причина — в <see cref="ConnectionStatus.Failure"/>.</summary>
    Failed,
}

/// <summary>Текущее состояние подключения. Не содержит секретов.</summary>
public sealed record ConnectionStatus(
    ConnectionState State,
    Profile? Profile = null,
    int? SocksPort = null,
    int? HttpPort = null,
    DateTimeOffset? ConnectedAt = null,
    ConnectionFailure? Failure = null)
{
    public static ConnectionStatus Disconnected { get; } = new(ConnectionState.Disconnected);
}

public enum ConnectionFailureKind
{
    /// <summary>В профиле ошибки валидации.</summary>
    ProfileInvalid,

    /// <summary>Ядро не поддерживает возможность профиля (конфиг не построен).</summary>
    UnsupportedByCore,

    /// <summary>Исполняемый файл ядра не найден.</summary>
    CoreNotFound,

    /// <summary>Ядро не запустилось.</summary>
    CoreStartFailed,

    /// <summary>Ядро завершилось во время работы.</summary>
    CoreCrashed,

    /// <summary>Не удалось включить системный прокси.</summary>
    SystemProxyFailed,
}

/// <summary>Причина неудачи. <see cref="LogTail"/> — последние строки лога ядра, уже замаскированные.</summary>
public sealed record ConnectionFailure(
    ConnectionFailureKind Kind,
    ProfileIssueCode? Issue = null,
    CoreConfigError? ConfigError = null,
    int? ExitCode = null,
    EquatableArray<string> LogTail = default);
