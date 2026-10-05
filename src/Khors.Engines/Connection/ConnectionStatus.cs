using Khors.Core.Generators;
using Khors.Core.Profiles;
using Khors.Engines.Diagnostics;
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
/// <param name="Problem">
/// Подключено, но соединения с сервером не устанавливаются: последняя причина из лога ядра
/// (<see cref="CoreErrorClassifier"/>). Сбрасывается переподключением и <see cref="ConnectionManager.ClearProblem"/>.
/// </param>
public sealed record ConnectionStatus(
    ConnectionState State,
    Profile? Profile = null,
    int? SocksPort = null,
    int? HttpPort = null,
    DateTimeOffset? ConnectedAt = null,
    ConnectionFailure? Failure = null,
    CoreKind? Core = null,
    CoreDiagnosis? Problem = null)
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
/// <param name="Core">Ядро, с которым произошла неудача; <c>null</c> — до запуска ядра.</param>
/// <param name="Problem">Известная причина по логу ядра (<see cref="CoreErrorClassifier"/>).</param>
public sealed record ConnectionFailure(
    ConnectionFailureKind Kind,
    ProfileIssueCode? Issue = null,
    CoreConfigError? ConfigError = null,
    int? ExitCode = null,
    EquatableArray<string> LogTail = default,
    CoreKind? Core = null,
    CoreDiagnosis? Problem = null);
