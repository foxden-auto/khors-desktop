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

/// <summary>Настройки приложения (<c>settings.json</c>). Отсутствующие поля получают значения по умолчанию.</summary>
/// <param name="SelectedProfileId">Профиль, выбранный для подключения.</param>
/// <param name="SocksPort">Желаемый порт SOCKS-входа; занят — берётся свободный.</param>
/// <param name="HttpPort">Желаемый порт HTTP-входа (на него указывает системный прокси).</param>
/// <param name="CoreLogLevel">Уровень лога ядра.</param>
/// <param name="Language">Язык интерфейса (ru, en); <c>null</c> — как в системе.</param>
/// <param name="LatencyTestUrl">Адрес теста задержки: запрос через прокси, ожидается ответ 204 или 200.</param>
/// <param name="SubscriptionAutoUpdate">Обновлять подписки автоматически (ROADMAP 2.2).</param>
/// <param name="SubscriptionUpdateIntervalHours">Интервал, если подписка не сообщила свой (<c>profile-update-interval</c>).</param>
public sealed record AppSettings(
    int SchemaVersion = AppSettings.CurrentSchemaVersion,
    Guid? SelectedProfileId = null,
    int SocksPort = 10808,
    int HttpPort = 10809,
    string CoreLogLevel = "warning",
    string? Language = null,
    string LatencyTestUrl = "https://cp.cloudflare.com/generate_204",
    bool SubscriptionAutoUpdate = true,
    int SubscriptionUpdateIntervalHours = 12)
{
    public const int CurrentSchemaVersion = 1;
}
