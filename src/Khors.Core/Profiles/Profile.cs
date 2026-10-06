using System.Text.Json.Serialization;

namespace Khors.Core.Profiles;

/// <summary>
/// Профиль подключения — внутренняя модель, к которой приводятся все форматы импорта
/// и из которой генерируются конфиги ядер (docs/SPEC.md, 3.1 п. 2 и 4.2).
/// </summary>
public sealed record Profile
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Group { get; init; }

    /// <summary>Подписка-источник; <c>null</c> — профиль создан вручную или импортирован ссылкой.</summary>
    public Guid? SubscriptionId { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public CorePreference Core { get; init; } = CorePreference.Auto;

    /// <summary>Отмечен пользователем как избранный. В файле — только когда <c>true</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsFavorite { get; init; }

    public required ServerEndpoint Server { get; init; }

    public required ProtocolSettings Protocol { get; init; }

    // Сериализатор передаёт null в init для отсутствующих полей — подставляем значение по умолчанию.
    public TransportSettings Transport { get; init => field = value ?? new TcpTransport(); } = new TcpTransport();

    public SecuritySettings Security { get; init => field = value ?? new NoSecurity(); } = new NoSecurity();

    public MuxSettings? Mux { get; init; }

    /// <summary>Параметры импорта, которые модель не понимает. Сохраняются, а не отбрасываются.</summary>
    public EquatableArray<UnknownParam> UnknownParams { get; init; }

    // Краткое описание без адресов и секретов: профиль можно безопасно передать в лог.
    public override string ToString() =>
        $"Profile {{ Id = {Id}, Name = {Name}, Protocol = {Protocol.GetType().Name}, Transport = {Transport.GetType().Name}, Security = {Security.GetType().Name} }}";
}

/// <summary>Какое ядро использовать для профиля.</summary>
public enum CorePreference
{
    [JsonStringEnumMemberName("auto")]
    Auto,

    [JsonStringEnumMemberName("xray")]
    Xray,

    [JsonStringEnumMemberName("singbox")]
    SingBox,
}

/// <summary>Адрес сервера: домен или IP (IPv6 — без квадратных скобок) и порт.</summary>
public sealed record ServerEndpoint(string Host, int Port)
{
    public override string ToString() => $"***:{Port}";
}

/// <summary>Мультиплексирование соединений (mux).</summary>
public sealed record MuxSettings
{
    public bool Enabled { get; init; }

    public int? Concurrency { get; init; }
}

/// <summary>Нераспознанный параметр импорта. Значение может быть секретом, поэтому не выводится в ToString.</summary>
public sealed record UnknownParam(string Key, string Value)
{
    public override string ToString() => $"{Key}=***";
}
