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

/// <summary>Кадр протокола: запрос и ответ на него с одинаковым <see cref="Id"/>; события службы — с <c>Id = 0</c>.</summary>
public sealed record IpcEnvelope(long Id, IpcPayload Payload);

/// <summary>Сериализация кадров. Сообщения не содержат секретов в открытом виде сверх нужного для команды.</summary>
public static class IpcSerializer
{
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static byte[] Serialize(IpcEnvelope envelope) => JsonSerializer.SerializeToUtf8Bytes(envelope, s_options);

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

            envelope = new IpcEnvelope(id, payload.Deserialize<IpcPayload>(s_options)!);
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
