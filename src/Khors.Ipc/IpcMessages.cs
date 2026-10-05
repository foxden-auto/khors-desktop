using System.Text.Json;
using System.Text.Json.Serialization;

namespace Khors.Ipc;

/// <summary>Версия протокола UI ↔ служба. Меняется при несовместимом изменении сообщений.</summary>
public static class IpcProtocol
{
    public const int Version = 1;
}

/// <summary>
/// Сообщение протокола. Список типов закрыт: служба принимает только описанные здесь команды
/// (CLAUDE.md, правило 8); неизвестный тип — ошибка <see cref="IpcErrorCode.UnknownCommand"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloRequest), "hello")]
[JsonDerivedType(typeof(HelloResponse), "helloOk")]
[JsonDerivedType(typeof(GetStatusRequest), "getStatus")]
[JsonDerivedType(typeof(ServiceStatusResponse), "status")]
[JsonDerivedType(typeof(ErrorResponse), "error")]
[JsonDerivedType(typeof(StartTunRequest), "startTun")]
[JsonDerivedType(typeof(TunStartedResponse), "tunStarted")]
[JsonDerivedType(typeof(TunFailedResponse), "tunFailed")]
[JsonDerivedType(typeof(StopTunRequest), "stopTun")]
[JsonDerivedType(typeof(OkResponse), "ok")]
[JsonDerivedType(typeof(TunLogEvent), "tunLog")]
[JsonDerivedType(typeof(TunExitedEvent), "tunExited")]
public abstract record IpcPayload;

/// <summary>Первое сообщение клиента: версия протокола и версия приложения.</summary>
public sealed record HelloRequest(int ProtocolVersion, string ClientVersion) : IpcPayload;

public sealed record HelloResponse(int ProtocolVersion, string ServiceVersion) : IpcPayload;

public sealed record GetStatusRequest : IpcPayload;

/// <summary>Состояние службы.</summary>
public sealed record ServiceStatusResponse(string ServiceVersion, DateTimeOffset StartedAt) : IpcPayload;

public enum IpcErrorCode
{
    /// <summary>Версии протокола UI и службы не совпадают — службу нужно обновить.</summary>
    ProtocolMismatch,

    /// <summary>Команда до рукопожатия.</summary>
    HelloRequired,

    /// <summary>Такой команды нет в протоколе.</summary>
    UnknownCommand,

    /// <summary>Сообщение не разбирается или параметры неверны.</summary>
    BadRequest,

    /// <summary>Ошибка в службе при выполнении команды.</summary>
    Internal,
}

/// <summary>Отказ службы. <paramref name="ServiceProtocolVersion"/> — при <see cref="IpcErrorCode.ProtocolMismatch"/>.</summary>
public sealed record ErrorResponse(IpcErrorCode Code, int? ServiceProtocolVersion = null) : IpcPayload;

/// <summary>Команда выполнена.</summary>
public sealed record OkResponse : IpcPayload;

/// <summary>
/// Включить режим TUN с профилем. Профиль — JSON модели (<c>StorageJson.SerializeProfile</c>): служба сама проверяет
/// его и строит конфиги ядер (CLAUDE.md, правила 3 и 8). TUN живёт, пока открыто соединение, которое его включило.
/// </summary>
/// <param name="LogLevel">Уровень лога ядер: debug, info, warning, error, none.</param>
public sealed record StartTunRequest(string Profile, string LogLevel) : IpcPayload
{
    // Профиль содержит ключи доступа — в журнал не выводим.
    public override string ToString() => $"StartTunRequest {{ LogLevel = {LogLevel} }}";
}

public enum IpcCore
{
    Xray,
    SingBox,
}

/// <param name="Core">Ядро профиля (Xray — цепочка TUN → Xray).</param>
/// <param name="SocksPort">Локальные входы sing-box на 127.0.0.1 — для теста задержки.</param>
public sealed record TunStartedResponse(IpcCore Core, int SocksPort, int HttpPort) : IpcPayload;

public enum IpcTunFailure
{
    /// <summary>Конфиг не построен: профиль с ошибкой или возможность не поддерживается ядром.</summary>
    ConfigNotGenerated,

    /// <summary>Ядро не найдено в каталоге службы.</summary>
    ExecutableNotFound,

    /// <summary>Ядро завершилось при запуске (в том числе не удалось создать адаптер TUN).</summary>
    ExitedDuringStart,

    /// <summary>Ядро не открыло локальный вход вовремя.</summary>
    ReadyTimeout,
}

/// <summary>Режим TUN не включился. Лог — уже замаскирован службой.</summary>
/// <param name="ConfigErrorCode">Код <c>CoreConfigErrorCode</c> при <see cref="IpcTunFailure.ConfigNotGenerated"/>.</param>
/// <param name="Problem">Известная причина (<c>CoreProblem</c>), если служба определила её сама.</param>
/// <remarks>Пустые поля в JSON не пишутся, поэтому у необязательных параметров — значения по умолчанию.</remarks>
public sealed record TunFailedResponse(
    IpcTunFailure Failure,
    IpcCore? Core = null,
    string? Field = null,
    string? ConfigErrorCode = null,
    int? ExitCode = null,
    IReadOnlyList<string>? LogTail = null,
    string? Problem = null) : IpcPayload;

/// <summary>Выключить режим TUN.</summary>
public sealed record StopTunRequest : IpcPayload;

/// <summary>Событие: строка лога ядра (уже замаскирована службой).</summary>
public sealed record TunLogEvent(string Line) : IpcPayload;

/// <summary>Событие: ядро режима TUN завершилось само (не по команде). Адаптер и маршруты сняты.</summary>
public sealed record TunExitedEvent(int ExitCode, IReadOnlyList<string> LogTail) : IpcPayload;

/// <summary>Кадр протокола: запрос и ответ на него с одинаковым <see cref="Id"/>; события службы — с <c>Id = 0</c>.</summary>
public sealed record IpcEnvelope(long Id, IpcPayload Payload);

/// <summary>
/// Сериализация кадров через генератор исходников (<see cref="IpcJsonContext"/>): не зависит от отражения,
/// обрезки и AOT. Сообщения не содержат секретов сверх нужного для команды.
/// </summary>
public static class IpcSerializer
{
    public static byte[] Serialize(IpcEnvelope envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope, IpcJsonContext.Default.IpcEnvelope);

    /// <summary>
    /// Разбор кадра. При ошибке — <c>false</c>, <paramref name="error"/> и, если удалось прочитать, <paramref name="id"/>,
    /// чтобы ответить отказом на тот же запрос.
    /// </summary>
    public static bool TryDeserialize(byte[] frame, out IpcEnvelope? envelope, out long id, out IpcErrorCode error)
    {
        ArgumentNullException.ThrowIfNull(frame);
        envelope = null;
        id = 0;
        error = IpcErrorCode.BadRequest;
        try
        {
            using var document = JsonDocument.Parse(frame);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var idElement)
                || !idElement.TryGetInt64(out id)
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!payload.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !IsKnownType(type.GetString()))
            {
                error = IpcErrorCode.UnknownCommand;
                return false;
            }

            envelope = new IpcEnvelope(id, payload.Deserialize(IpcJsonContext.Default.IpcPayload)!);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static readonly HashSet<string> s_knownTypes =
    [
        .. typeof(IpcPayload).GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .Select(a => (string)a.TypeDiscriminator!),
    ];

    private static bool IsKnownType(string? type) => type is not null && s_knownTypes.Contains(type);
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(IpcEnvelope))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
