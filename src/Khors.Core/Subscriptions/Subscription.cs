using Khors.Core.Profiles;

namespace Khors.Core.Subscriptions;

/// <summary>Подписка: адрес, с которого загружается список профилей (docs/SPEC.md, 4.1).</summary>
public sealed record Subscription
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Адрес подписки. Обычно содержит токен доступа — хранится как секрет.</summary>
    public required Secret Url { get; init; }

    /// <summary>Свой User-Agent для этой подписки; <c>null</c> — по умолчанию KHORS.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Последнее успешное обновление.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Последняя попытка обновления (успешная или нет) — для повтора после ошибки.</summary>
    public DateTimeOffset? LastAttemptAt { get; init; }

    /// <summary>Трафик и срок из заголовка <c>subscription-userinfo</c>.</summary>
    public SubscriptionUserInfo? UserInfo { get; init; }

    /// <summary>Интервал обновления из заголовка <c>profile-update-interval</c>, в часах.</summary>
    public int? UpdateIntervalHours { get; init; }

    /// <summary>Причина неудачи последнего обновления; <c>null</c> — обновилась успешно (или ещё не обновлялась).</summary>
    public SubscriptionUpdateError? LastError { get; init; }

    // Адрес не выводим: в нём токен.
    public override string ToString() => $"Subscription {{ Id = {Id}, Name = {Name} }}";
}

/// <param name="Upload">Отправлено, байт.</param>
/// <param name="Download">Получено, байт.</param>
/// <param name="Total">Лимит, байт; <c>null</c> или 0 — без лимита.</param>
/// <param name="Expire">Окончание подписки; <c>null</c> — бессрочно.</param>
/// <remarks>Параметры необязательны: при записи <c>null</c> опускается, и чтение не должно требовать его обратно.</remarks>
public sealed record SubscriptionUserInfo(long? Upload = null, long? Download = null, long? Total = null, DateTimeOffset? Expire = null)
{
    public long Used => (Upload ?? 0) + (Download ?? 0);
}

public enum SubscriptionUpdateError
{
    /// <summary>Сервер недоступен или соединение оборвалось.</summary>
    Network,

    Timeout,

    /// <summary>Сервер ответил ошибкой HTTP.</summary>
    HttpError,

    /// <summary>Ответ больше допустимого размера.</summary>
    TooLarge,

    /// <summary>Ответ получен, но в нём нет ни одного профиля известного формата.</summary>
    NoProfiles,
}
