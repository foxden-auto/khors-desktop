using Khors.Core.Profiles;
using Khors.Core.Subscriptions;

namespace Khors.Core.Storage;

/// <summary>Файл профилей (<c>profiles.json</c>). Версия схемы — в корне (docs/SPEC.md, 4.2).</summary>
/// <remarks>Версия 2 — добавлены подписки (ROADMAP 2.1).</remarks>
public sealed record ProfileDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public EquatableArray<Profile> Profiles { get; init; }

    public EquatableArray<Subscription> Subscriptions { get; init; }
}

/// <summary>Режим подключения (docs/SPEC.md, 3.3).</summary>
public enum ConnectionMode
{
    /// <summary>Ядро с локальными входами, системный прокси Windows указывает на HTTP-вход.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("systemProxy")]
    SystemProxy,

    /// <summary>Весь трафик системы через адаптер TUN; ядра запускает служба KHORS.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("tun")]
    Tun,
}

/// <summary>Настройки приложения (<c>settings.json</c>). Отсутствующие поля получают значения по умолчанию.</summary>
/// <param name="SelectedProfileId">Профиль, выбранный для подключения.</param>
/// <param name="SocksPort">Желаемый порт SOCKS-входа; занят — берётся свободный.</param>
/// <param name="HttpPort">Желаемый порт HTTP-входа (на него указывает системный прокси).</param>
/// <param name="CoreLogLevel">Уровень лога ядра.</param>
/// <param name="Language">Язык интерфейса (ru, en); <c>null</c> — как в системе.</param>
/// <param name="LatencyTestUrl">Адрес теста задержки: запрос через прокси, ожидается ответ 204 или 200.</param>
/// <param name="SubscriptionAutoUpdate">Обновлять подписки автоматически (ROADMAP 2.2).</param>
/// <param name="SubscriptionUpdateIntervalHours">Интервал, если подписка не сообщила свой (<c>profile-update-interval</c>).</param>
/// <param name="AutoSelect">Выбрана группа «Авто» вместо одного профиля (ROADMAP 2.8).</param>
/// <param name="AutoRecheckMinutes">Как часто «Авто» перепроверяет задержку (1…1440 минут).</param>
/// <param name="SortProfilesByLatency">Список профилей — по возрастанию задержки.</param>
/// <param name="ConnectionMode">Режим подключения: системный прокси или TUN через службу (ROADMAP 3.2).</param>
/// <param name="RemoteDns">Удалённый DNS в текстовой форме <see cref="Dns.DnsServer"/> — для TUN и WireGuard (ROADMAP 3.4).</param>
/// <param name="GeoAutoUpdate">Проверять гео-базы раз в сутки (ROADMAP 3.6).</param>
/// <param name="GeoLastCheck">Последняя успешная проверка гео-баз окна.</param>
public sealed record AppSettings(
    int SchemaVersion = AppSettings.CurrentSchemaVersion,
    Guid? SelectedProfileId = null,
    int SocksPort = 10808,
    int HttpPort = 10809,
    string CoreLogLevel = "warning",
    string? Language = null,
    string LatencyTestUrl = "https://cp.cloudflare.com/generate_204",
    bool SubscriptionAutoUpdate = true,
    int SubscriptionUpdateIntervalHours = 12,
    bool AutoSelect = false,
    int AutoRecheckMinutes = 10,
    bool SortProfilesByLatency = false,
    ConnectionMode ConnectionMode = ConnectionMode.SystemProxy,
    string RemoteDns = AppSettings.DefaultRemoteDns,
    bool GeoAutoUpdate = true,
    DateTimeOffset? GeoLastCheck = null)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Совпадает с <see cref="Dns.DnsPresets.Default"/> (проверяется тестом).</summary>
    public const string DefaultRemoteDns = "https://1.1.1.1/dns-query";
}
